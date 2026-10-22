using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.Fibo2;

/// <summary>Encodes Fibo 2.0 prompts with ComfyUI's native TextEncodeFibo2 inside SwarmUI's native Fibo 2.0 workflow.</summary>
public class Fibo2Extension : Extension
{
    private const string Feature = "secourses_fibo2";
    public const string AutoSize = "Auto: edits keep the first image size (1-4 MP)";
    public const string BriaSize = "Bria default: edits at the trained ~1 MP size";
    public const string FixedSize = "Always use Width / Height";
    public const string OfficialShift = "Official: dynamic shift by image size (Bria)";
    public const string FixedShift = "ComfyUI default: fixed shift 1.15";
    public const string Int8TextEncoder = "qwen3vl_4b_int8_convrot.safetensors";
    private const string InputError = "secourses_fibo2_input_error";
    private static T2IRegisteredParam<bool> Enabled;
    private static T2IRegisteredParam<string> EditSize, Shift, PromptList;
    private static readonly Regex Choice = new(@"(?<!\\)\{([^{}]*\|[^{}]*)\}");
    // Swarm keeps unknown <tags>, but <image_1> must not look like a Swarm tag to its parser; the ComfyUI node maps @image1 back.
    private static readonly Regex ImageTag = new(@"<image_(\d+)>");
    /// <summary>Prompt images withheld from Swarm's own reference latents while the Fibo 2.0 conditioning already holds them.</summary>
    private static readonly ConditionalWeakTable<T2IParamInput, List<Image>> HeldPromptImages = new();

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Fibo 2.0 (Bria) conditioning with ComfyUI's native Text Encode Fibo 2.0: JSON captions, image editing with up to 4 references, masked edits, Bria's negative prompts and resolution-dependent shift, automatic edit size and prompt batches.";
        License = "MIT";
        Version = "1.0.0";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/Fibo2";
    }

    public override void OnPreInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["SEFibo2GalleryEncode"] = Feature;
    }

    public override void OnInit()
    {
        T2IParamGroup group = new("Fibo 2.0", Open: true, OrderPriority: -4,
            Description: "Bria Fibo 2.0. Dev: 50 steps, CFG 5 (Bria's JSON negative for text to image, empty negative for edits). Edit Turbo: 4 steps, CFG 1. Write a JSON caption (Bria's VGL format) or plain text. Attach images to the prompt to edit them (@image1, @image2 in the instruction); paint a mask on an Init Image for a masked edit.");
        Enabled = T2IParamTypes.Register<bool>(new("Fibo 2 Advanced", "Encode the prompt with ComfyUI's native Text Encode Fibo 2.0 (JSON caption cleaning, edit wrapping, the caption's text images and reference latents exactly like Bria's pipeline) instead of Swarm's generic encoder. Also gives Bria's resolution-dependent shift, automatic edit size, masked edits and prompt batches. Only applies to Fibo 2.0 models.",
            "false", IgnoreIf: "false", Group: group, FeatureFlag: Feature, OrderPriority: -20));
        EditSize = T2IParamTypes.Register<string>(new("Fibo 2 Edit Size", "Result size of an edit (text to image always uses Width / Height). Auto keeps the first image's own size from 1 to 4 MP (a 2 MP photo is edited at 2 MP), scales larger images down to 4 MP and smaller ones up to Fibo 2.0's trained ~1 MP size. Bria default edits at the trained ~1 MP size for the first image's aspect ratio. Always use Width / Height center-crops a single image to that aspect ratio.",
            AutoSize, GetValues: _ => [AutoSize, BriaSize, FixedSize], Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -10));
        Shift = T2IParamTypes.Register<string>(new("Fibo 2 Sigma Shift", "Official: Bria's pipeline raises the shift with the image size (1.15 at about 1024x1024, about 2 at 1536x1536), and the caption's text images still start at Bria's step. ComfyUI default: 1.15 at every size.",
            OfficialShift, GetValues: _ => [OfficialShift, FixedShift], Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -9));
        PromptList = T2IParamTypes.Register<string>(new("Fibo 2 Prompt List", "Extra prompts for Generate prompt batch. Add prompts or import .txt files/a folder; each text file is one complete prompt, JSON captions included. The batch includes the main prompt and runs sequentially with the same captured settings and attached images.",
            "[]", ViewType: ParamViewType.BIG, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 5, IntentionalUnused: true));
        T2IParamInput.SpecialParameterHandlers.Add(PrepareInput);
        // After the Megapixels extension, which sets Width / Height in an early handler; errors raised here reach the browser.
        T2IParamInput.LateSpecialParameterHandlers.Insert(0, input =>
        {
            if (input.ExtraMeta.TryGetValue(InputError, out object error))
            {
                throw new SwarmUserErrorException(error.ToString());
            }
            ApplyEditSize(input);
        });
        // After the native text encoders (-7), before the main sampler (-5) adds Swarm's own reference latents.
        WorkflowGenerator.AddStep(ReplaceConditioning, -6.5);
        // Before the final save step (10) fills the image placeholders of the workflow.
        WorkflowGenerator.AddStep(RestorePromptImages, 9.9);
        ScriptFiles.Add("Assets/fibo2.js");
        StyleSheetFiles.Add("Assets/fibo2.css");
    }

    public static bool IsFibo2(T2IModel model)
    {
        if (model?.ModelClass is not null)
        {
            return model.ModelClass.CompatClass?.ID == "fibo-2";
        }
        // Older metadata caches can leave new architectures unclassified: use Swarm's own detector.
        return model is not null && File.Exists(model.RawFilePath)
            && Path.GetExtension(model.RawFilePath) is ".safetensors" or ".sft"
            && T2IModelClassSorter.ModelClasses.TryGetValue("fibo-2", out T2IModelClass cls)
            && cls.IsThisModelOfClass(model, T2IModel.GetMetadataHeaderFrom(model.RawFilePath));
    }

    private static void PrepareInput(T2IParamInput input)
    {
        input.ExtraMeta.Remove(InputError);
        try
        {
            PrepareInputChecked(input);
        }
        catch (SwarmUserErrorException ex)
        {
            input.ExtraMeta[InputError] = ex.Message;
        }
    }

    private static void PrepareInputChecked(T2IParamInput input)
    {
        bool fibo = IsFibo2(input.Get(T2IParamTypes.Model));
        // The Fibo 2.0 Core Bundle ships the INT8 ConvRot Qwen3-VL 4B under its own name; use it instead of letting Swarm
        // download its FP8 qwen3vl_4b.safetensors default.
        if (fibo && !input.TryGet(T2IParamTypes.QwenModel, out _) && Program.T2IModelSets.TryGetValue("Clip", out T2IModelHandler clips)
            && clips.Models.TryGetValue(Int8TextEncoder, out T2IModel int8))
        {
            input.Set(T2IParamTypes.QwenModel, int8);
        }
        if (!input.Get(Enabled, false))
        {
            return;
        }
        if (!fibo)
        {
            // Presets only change their own keys. A leftover switch must never affect another model.
            input.Remove(Enabled);
            input.Remove(EditSize);
            input.Remove(Shift);
            input.Remove(PromptList);
            input.RequiredFlags.Remove(Feature);
            return;
        }
        input.Set(T2IParamTypes.Prompt, PreparePrompt(input.Get(T2IParamTypes.Prompt, "")));
        input.Set(T2IParamTypes.NegativePrompt, PreparePrompt(input.Get(T2IParamTypes.NegativePrompt, "")));
        if (MaskedEdit(input))
        {
            if (input.TryGet(T2IParamTypes.PromptImages, out List<Image> extra) && extra.Count > 0)
            {
                throw new SwarmUserErrorException("A Fibo 2.0 masked edit changes one image: the Init Image with its painted mask. Remove the images attached to the prompt, or clear the mask to edit with the attached images.");
            }
            // Fibo 2.0 regenerates the greyed-out region from noise; Swarm's masked latent keeps the rest of the image.
            input.Set(T2IParamTypes.InitImageCreativity, 1.0);
            if (input.TryGet(T2IParamTypes.MaskShrinkGrow, out _))
            {
                Logs.Info("Fibo 2.0 masked edit: Mask Shrink Grow is turned off, the whole image is edited at its own size.");
                input.Remove(T2IParamTypes.MaskShrinkGrow);
            }
        }
    }

    /// <summary>JSON captions are sent as written; plain prompts get Swarm's seeded random choices for {a|b}.
    /// Image labels become @imageN, which the ComfyUI node turns back into Fibo 2.0's <image_N>.</summary>
    public static string PreparePrompt(string prompt)
    {
        prompt = ImageTag.Replace(prompt ?? "", match => $"@image{match.Groups[1].Value}");
        if (IsJsonObject(prompt))
        {
            return prompt;
        }
        while (Choice.IsMatch(prompt))
        {
            prompt = Choice.Replace(prompt, match => $"<random:{match.Groups[1].Value}>");
        }
        return prompt;
    }

    public static bool IsJsonObject(string prompt)
    {
        string text = (prompt ?? "").Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}'))
        {
            return false;
        }
        try
        {
            return JToken.Parse(text) is JObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool MaskedEdit(T2IParamInput input) => input.TryGet(T2IParamTypes.InitImage, out Image _) && input.TryGet(T2IParamTypes.MaskImage, out Image _);

    private static bool Active(T2IParamInput input) => input.Get(Enabled, false) && IsFibo2(input.Get(T2IParamTypes.Model));

    private static void ApplyEditSize(T2IParamInput input)
    {
        if (!Active(input))
        {
            return;
        }
        string mode = input.Get(EditSize, AutoSize);
        Image first = null;
        if (MaskedEdit(input))
        {
            first = input.Get(T2IParamTypes.InitImage);
        }
        else if (input.TryGet(T2IParamTypes.PromptImages, out List<Image> images) && images.Count > 0)
        {
            first = images[0];
        }
        if (first is null || mode == FixedSize)
        {
            return;
        }
        (int imageWidth, int imageHeight) = first.GetResolution();
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return;
        }
        (int width, int height) = mode == BriaSize ? TrainedSize((double)imageWidth / imageHeight) : EditSizeFor(imageWidth, imageHeight);
        input.Set(T2IParamTypes.Width, width);
        input.Set(T2IParamTypes.Height, height);
        input.Remove(T2IParamTypes.SideLength);
        input.Remove(T2IParamTypes.AltResolutionHeightMult);
        input.Remove(T2IParamTypes.RawResolution);
    }

    /// <summary>The auto edit size: the image's own size in multiples of 16 from 1 to 4 MP, scaled down above 4 MP,
    /// Bria's trained size for its aspect ratio below 1 MP. Same as SECoursesAudioTools fibo2_nodes.edit_size.</summary>
    public static (int, int) EditSizeFor(int width, int height)
    {
        const double minArea = 1024 * 1024, maxArea = 2048 * 2048;
        double area = (double)width * height;
        if (area < minArea)
        {
            return TrainedSize((double)width / height);
        }
        double scale = Math.Min(1.0, Math.Sqrt(maxArea / area));
        return (Math.Max(16, (int)(width * scale) / 16 * 16), Math.Max(16, (int)(height * scale) / 16 * 16));
    }

    /// <summary>Bria's (width, height) for an aspect ratio: of the multiples of 16 within 10% of 1 MP, the least cropping,
    /// then the closest to 1 MP, then the widest. Port of ComfyUI comfy_extras/nodes_fibo2.trained_size.</summary>
    public static (int, int) TrainedSize(double aspect)
    {
        const int area = 1024 * 1024;
        (int, int) best = (1024, 1024);
        (double, long, int) bestKey = (double.MaxValue, long.MaxValue, int.MaxValue);
        for (int w = 64; w < 2 * area / 64; w += 16)
        {
            int hMin = Math.Max(64, 16 * (int)Math.Ceiling(0.9 * area / w / 16)), hMax = 16 * (int)Math.Floor(1.1 * area / w / 16);
            for (int h = hMin; h <= hMax; h += 16)
            {
                double crop = Math.Round(1 - Math.Min((double)w / h / aspect, aspect * h / w), 9);
                (double, long, int) key = (crop, Math.Abs((long)w * h - area), -w);
                if (key.CompareTo(bestKey) < 0)
                {
                    bestKey = key;
                    best = (w, h);
                }
            }
        }
        return best;
    }

    private static void ReplaceConditioning(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(Enabled, false) || !g.IsFibo2())
        {
            return;
        }
        if (!g.Features.Contains(Feature))
        {
            throw new SwarmUserErrorException("Fibo 2 Advanced needs the updated SECoursesAudioTools nodes in the ComfyUI backend. Run the SwarmUI updater, then restart the backend.");
        }
        if (!g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> images) && HeldPromptImages.TryGetValue(g.UserInput, out List<Image> held))
        {
            // A previous attempt of this generation stopped between withholding and restoring them.
            images = held;
            g.UserInput.Set(T2IParamTypes.PromptImages, held);
        }
        images ??= [];
        JArray references = [];
        foreach (Image image in images)
        {
            references.Add(ImageData(image));
        }
        bool masked = MaskedEdit(g.UserInput);
        string inputs = g.CreateNode("SEFibo2SwarmInputs", new JObject()
        {
            ["prompt"] = new PromptRegion(g.UserInput.Get(T2IParamTypes.Prompt, "")).GlobalPrompt,
            ["references_json"] = references.ToString(Formatting.None),
            ["init_base64"] = masked ? ImageData(g.UserInput.Get(T2IParamTypes.InitImage)) : "",
            ["mask_base64"] = masked ? ImageData(g.UserInput.Get(T2IParamTypes.MaskImage)) : ""
        });
        string encode = g.CreateNode("SEFibo2GalleryEncode", new JObject()
        {
            ["clip"] = g.CurrentTextEnc.Path,
            ["vae"] = g.CurrentVae.Path,
            ["references"] = WorkflowGenerator.NodePath(inputs, 0),
            ["negative_prompt"] = new PromptRegion(g.UserInput.Get(T2IParamTypes.NegativePrompt, "")).GlobalPrompt,
            // Width and Height already hold the edit size (ApplyEditSize), so Swarm's own latent matches.
            ["size_mode"] = "Always use the resolution inputs",
            ["width"] = g.UserInput.GetImageWidth(),
            ["height"] = g.UserInput.GetImageHeight(),
            ["batch_size"] = 1,
            ["seed"] = g.UserInput.Get(T2IParamTypes.Seed, 0),
            ["edit_image"] = WorkflowGenerator.NodePath(inputs, 1),
            ["edit_mask"] = WorkflowGenerator.NodePath(inputs, 2)
        });
        string sampling = g.CreateNode("SEFibo2Sampling", new JObject()
        {
            ["model"] = g.CurrentModel.Path,
            ["positive"] = WorkflowGenerator.NodePath(encode, 0),
            ["latent"] = WorkflowGenerator.NodePath(encode, 2),
            // The same choice strings as the ComfyUI node's SHIFT_MODES.
            ["shift"] = g.UserInput.Get(Shift, OfficialShift)
        });
        g.CurrentModel = g.CurrentModel.WithPath([sampling, 0]);
        g.FinalPrompt = [sampling, 1];
        g.FinalNegativePrompt = [encode, 1];
        if (images.Count > 0)
        {
            // The conditioning already holds the reference latents: keep Swarm's sampler from adding a second set.
            HeldPromptImages.AddOrUpdate(g.UserInput, images);
            g.UserInput.Remove(T2IParamTypes.PromptImages);
        }
    }

    private static void RestorePromptImages(WorkflowGenerator g)
    {
        if (HeldPromptImages.TryGetValue(g.UserInput, out List<Image> images))
        {
            g.UserInput.Set(T2IParamTypes.PromptImages, images);
            HeldPromptImages.Remove(g.UserInput);
        }
    }

    private static string ImageData(Image image) => image.Type.MetaType == MediaMetaType.Image ? image.AsBase64
        : throw new SwarmUserErrorException("Fibo 2.0 edits take still images. Remove video or audio attachments.");
}
