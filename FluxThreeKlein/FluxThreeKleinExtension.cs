using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace FurkanGozukara.SwarmExtensions.FluxThreeKlein;

/// <summary>Adds the SECourses FLUX.3 preset controls to Swarm's native workflow.</summary>
public class FluxThreeKleinExtension : Extension
{
    private const string Feature = "secourses_flux_three_klein";
    private const string TextMode = "Text / optional references";
    private const string EditMode = "Reference edit";
    private const string LayoutMode = "Layout / optional references";
    private const string InputError = "secourses_flux3_input_error";
    private static T2IRegisteredParam<bool> Enabled;
    private static T2IRegisteredParam<string> Mode, Boxes, PromptList;
    private static T2IRegisteredParam<double> ReferenceMegapixels;
    private static readonly Regex ReferenceAlias = new(@"(?<![\w@])@(?:image|img|picture|pic)(\d+)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ReferenceLabel = new(@"<ref_image_(\d+)>", RegexOptions.IgnoreCase);
    private static readonly Regex Choice = new(@"(?<!\\)\{([^{}]*\|[^{}]*)\}");

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "FLUX.3 Klein advanced prompting, image references, visual layout boxes and prompt batches, using SwarmUI's native ComfyUI workflow.";
        License = "MIT";
        Version = "1.0.1";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/FluxThreeKlein";
    }

    public override void OnPreInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["Flux3LayoutPrompt"] = Feature;
    }

    public override void OnInit()
    {
        API.RegisterAPICall(FluxThreeKleinPresetAPI.FluxKleinSavePngPreset, true, Permissions.ManagePresets);
        T2IParamGroup group = new("FLUX.3 Klein", Open: true, OrderPriority: -4,
            Description: "Describe the complete result. Attach up to 10 images to the prompt and refer to them as @image1, @image2, etc. Layout boxes guide placement. Main seed, steps, CFG, sampler, resolution, LoRAs and variation controls still apply.");
        Enabled = T2IParamTypes.Register<bool>(new("FLUX Klein Advanced", "Enable the SECourses FLUX.3 presets: Comfy-style weights, schedules and choices, reference labels, layout editor and prompt batches. Only applies to FLUX.3 Klein models.",
            "false", IgnoreIf: "false", Group: group, FeatureFlag: Feature, OrderPriority: -20));
        Mode = T2IParamTypes.Register<string>(new("FLUX Klein Mode", "Text mode also accepts optional reference images. Reference edit requires at least one prompt image. Layout mode adds the boxes below; images remain optional. The separate Init Image uses Swarm's image-to-image workflow.",
            TextMode, GetValues: _ => [TextMode, EditMode, LayoutMode], Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -10));
        ReferenceMegapixels = T2IParamTypes.Register<double>(new("FLUX Klein Reference Megapixels", "Size of EACH reference image in millions of pixels, independent of output resolution. Preserves aspect ratio and rounds to 16 pixels. Transparent areas are composited on white. More/larger references use more VRAM.",
            "1", Min: 0.1, Max: 4, Step: 0.05, ViewType: ParamViewType.SLIDER, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -5));
        Boxes = T2IParamTypes.Register<string>(new("FLUX Klein Layout Boxes", "Draw or resize boxes, describe each object, and cite <obj_1>, <obj_2>, etc. in the main prompt. Coordinates are normalized [top, left, bottom, right] and follow the output aspect ratio. Boxes guide the model; they are not masks.",
            "[]", ViewType: ParamViewType.BIG, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 0));
        PromptList = T2IParamTypes.Register<string>(new("FLUX Klein Prompt List", "Extra prompts for Generate prompt batch. Add prompts or import .txt files/a folder. Each text file is one complete prompt. The batch button includes the main prompt and runs sequentially with the same captured settings and references.",
            "[]", ViewType: ParamViewType.BIG, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 5, IntentionalUnused: true));
        T2IParamInput.SpecialParameterHandlers.Add(PrepareInput);
        // Early handlers run outside Swarm's readable-error catch. Report our
        // validation failures at the late stage, before claiming a backend.
        T2IParamInput.LateSpecialParameterHandlers.Insert(0, input => {
            if (input.ExtraMeta.TryGetValue(InputError, out object error))
            {
                throw new SwarmUserErrorException(error.ToString());
            }
        });
        WorkflowGenerator.AddStep(AddLayout, -6.9);
        WorkflowGenerator.AddStep(ResizeReferences, -4.99);
        ScriptFiles.Add("Assets/flux_three_klein.js");
        StyleSheetFiles.Add("Assets/flux_three_klein.css");
    }

    /// <summary>SwarmUI names the native FLUX.3 Klein family 'flux-3-image-klein' (model class and compat class) and remaps the
    /// former 'flux.3-klein' model class to it; cores before that rename used the compat class 'flux-3-klein'.</summary>
    private static bool IsFluxThree(T2IModel model)
    {
        if (model?.ModelClass is not null)
        {
            return model.ModelClass.CompatClass?.ID is "flux-3-image-klein" or "flux-3-klein";
        }
        return model is not null && File.Exists(model.RawFilePath)
            && Path.GetExtension(model.RawFilePath) is ".safetensors" or ".sft"
            && (T2IModelClassSorter.ModelClasses.TryGetValue("flux-3-image-klein", out T2IModelClass cls)
                || T2IModelClassSorter.ModelClasses.TryGetValue("flux.3-klein", out cls))
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
        if (!input.Get(Enabled, false))
        {
            return;
        }
        if (!IsFluxThree(input.Get(T2IParamTypes.Model)))
        {
            // Presets only change their own keys. A leftover switch must never affect another model.
            input.Remove(Enabled);
            input.Remove(Mode);
            input.Remove(Boxes);
            input.Remove(PromptList);
            input.Remove(ReferenceMegapixels);
            input.RequiredFlags.Remove(Feature);
            return;
        }
        int count = input.Get(T2IParamTypes.PromptImages, new List<Image>()).Count;
        if (count > 10)
        {
            throw new SwarmUserErrorException("FLUX.3 Klein supports at most 10 prompt images. Remove the extra attachments.");
        }
        if (input.Get(T2IParamTypes.PromptVideos, new List<VideoFile>()).Count > 0
            || input.Get(T2IParamTypes.PromptAudios, new List<AudioFile>()).Count > 0)
        {
            throw new SwarmUserErrorException("FLUX.3 Klein accepts image references only. Remove the video/audio attachments.");
        }
        string mode = input.Get(Mode, TextMode);
        if (mode == EditMode && count == 0)
        {
            throw new SwarmUserErrorException("Reference edit needs at least one image attached to the prompt. Use the prompt's Add media button, or choose Text / optional references.");
        }
        JArray boxes = mode == LayoutMode ? ReadBoxes(input.Get(Boxes, "[]")) : null;
        if (boxes is not null && SplitNativeTable(input.Get(T2IParamTypes.Prompt, "")).Table is not null)
        {
            throw new SwarmUserErrorException("The visual layout mode creates its JSON table automatically. Put only the caption in the prompt, or switch to Text / optional references or Reference edit to use a complete native JSON table.");
        }
        string prompt = PreparePrompt(input.Get(T2IParamTypes.Prompt, ""), count);
        if (boxes is not null)
        {
            foreach (Match match in Regex.Matches(prompt, @"<obj_(\d+)>", RegexOptions.IgnoreCase))
            {
                if (!int.TryParse(match.Groups[1].Value, out int number) || number < 1 || number > boxes.Count)
                {
                    throw new SwarmUserErrorException($"{match.Value} has no layout box. Update the prompt after removing or reordering boxes.");
                }
            }
        }
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.NegativePrompt, PreparePrompt(input.Get(T2IParamTypes.NegativePrompt, ""), count));
    }

    /// <summary>Convert only the caption. The layout table is appended later, outside every legacy parser.</summary>
    public static string PreparePrompt(string prompt, int referenceCount)
    {
        (string caption, string table) = SplitNativeTable(prompt);
        prompt = caption;
        string Label(int number, string original)
        {
            if (number < 1 || number > referenceCount)
            {
                throw new SwarmUserErrorException($"{original} has no matching prompt image. Update the prompt after removing or reordering attachments.");
            }
            return $"<ref_image_{number - 1}>";
        }
        prompt = ReferenceAlias.Replace(prompt, match => Label(int.TryParse(match.Groups[1].Value, out int n) ? n : 0, match.Value));
        foreach (Match match in ReferenceLabel.Matches(prompt))
        {
            if (!int.TryParse(match.Groups[1].Value, out int n) || n < 0 || n >= referenceCount)
            {
                throw new SwarmUserErrorException($"{match.Value} has no matching prompt image. Native reference labels start at <ref_image_0>.");
            }
        }
        // Swarm resolves these with its seeded wildcard RNG during normal prompt preprocessing.
        while (Choice.IsMatch(prompt))
        {
            prompt = Choice.Replace(prompt, match => $"<random:{match.Groups[1].Value}>");
        }
        prompt = LegacyPromptParser.Convert(prompt);
        if (table is not null)
        {
            foreach (JObject row in JArray.Parse(table))
            {
                string source = row.Value<string>("from") ?? "";
                Match reference = Regex.Match(source, @"^ref_image_(\d+)$");
                if (reference.Success && (!int.TryParse(reference.Groups[1].Value, out int n) || n >= referenceCount))
                {
                    throw new SwarmUserErrorException($"The layout row's {source} has no matching prompt image. Native references start at ref_image_0.");
                }
            }
            prompt = prompt.TrimEnd() + "\n" + table;
        }
        return prompt;
    }

    /// <summary>Keep an explicitly supplied native table outside caption syntax conversion.</summary>
    private static (string Caption, string Table) SplitNativeTable(string prompt)
    {
        string text = prompt.TrimEnd();
        if (text.EndsWith(']'))
        {
            for (int start = text.IndexOf('['); start >= 0; start = text.IndexOf('[', start + 1))
            {
                try
                {
                    JArray rows = JArray.Parse(text[start..]);
                    if (rows.Count > 0 && rows.All(row => row is JObject obj && obj["id"]?.Type == JTokenType.String))
                    {
                        return (text[..start], text[start..]);
                    }
                }
                catch (JsonException) { }
            }
        }
        return (prompt, null);
    }

    public static JArray ReadBoxes(string text)
    {
        JArray boxes;
        try
        {
            boxes = JArray.Parse(text);
        }
        catch (JsonException)
        {
            throw new SwarmUserErrorException("Layout boxes must be a JSON array. Use the visual editor or restore the layout preset.");
        }
        if (boxes.Count is < 1 or > 32)
        {
            throw new SwarmUserErrorException("Layout mode needs between 1 and 32 boxes. Draw a box or choose Text / optional references.");
        }
        for (int i = 0; i < boxes.Count; i++)
        {
            if (boxes[i] is not JObject box || box["bbox"] is not JArray coords || coords.Count != 4
                || coords.Any(v => v.Type is not (JTokenType.Float or JTokenType.Integer)))
            {
                throw new SwarmUserErrorException($"Box {i + 1} needs four numeric coordinates: [top, left, bottom, right].");
            }
            double[] values = coords.Values<double>().ToArray();
            if (values.Any(v => !double.IsFinite(v) || v < 0 || v > 1) || values[0] >= values[2] || values[1] >= values[3])
            {
                throw new SwarmUserErrorException($"Box {i + 1} must have positive size and coordinates from 0 to 1.");
            }
            if (string.IsNullOrWhiteSpace(box.Value<string>("desc")) && string.IsNullOrWhiteSpace(box.Value<string>("text")))
            {
                throw new SwarmUserErrorException($"Describe the object or text in box {i + 1}.");
            }
            string type = box.Value<string>("type") ?? "obj";
            if (type is not ("obj" or "text"))
            {
                throw new SwarmUserErrorException($"Box {i + 1} type must be obj or text.");
            }
            box["type"] = type;
        }
        return boxes;
    }

    private static bool Active(WorkflowGenerator g) => g.UserInput.Get(Enabled, false) && IsFluxThree(g.UserInput.Get(T2IParamTypes.Model));

    private static void AddLayout(WorkflowGenerator g)
    {
        if (!Active(g) || g.UserInput.Get(Mode, TextMode) != LayoutMode)
        {
            return;
        }
        if (!g.Features.Contains(Feature))
        {
            throw new SwarmUserErrorException("Update the ComfyUI backend to native FLUX.3 support, then restart it. Flux3LayoutPrompt is required.");
        }
        JObject encoder = g.Workflow["6"] as JObject;
        string type = encoder?.Value<string>("class_type");
        string field = type == "CLIPTextEncode" ? "text" : type == "SwarmTextEncodeAdvanced" ? "prompt" : null;
        if (field is null || encoder["inputs"]?[field]?.Type != JTokenType.String)
        {
            throw new SwarmUserErrorException("The FLUX.3 layout preset needs a normal main prompt. Disable conflicting custom workflow/region overrides and try again.");
        }
        int width = g.UserInput.GetImageWidth(), height = g.UserInput.GetImageHeight();
        JArray pixelBoxes = new();
        foreach (JObject box in ReadBoxes(g.UserInput.Get(Boxes, "[]")))
        {
            double[] b = box["bbox"].Values<double>().ToArray();
            pixelBoxes.Add(new JObject {
                ["x"] = (int)Math.Round(b[1] * width), ["y"] = (int)Math.Round(b[0] * height),
                ["width"] = Math.Max(1, (int)Math.Round((b[3] - b[1]) * width)),
                ["height"] = Math.Max(1, (int)Math.Round((b[2] - b[0]) * height)),
                ["metadata"] = new JObject { ["type"] = box["type"], ["desc"] = box["desc"] ?? "", ["text"] = box["text"] ?? "" } });
        }
        string boxes = g.CreateNode("CreateBoundingBoxes", new JObject {
            ["width"] = width, ["height"] = height,
            ["editor_state"] = new JArray(), ["bboxes"] = pixelBoxes.ToString(Formatting.None) });
        string layout = g.CreateNode("Flux3LayoutPrompt", new JObject {
            ["prompt"] = encoder["inputs"][field].DeepClone(), ["element"] = WorkflowGenerator.NodePath(boxes, 2) });
        // New SwarmTextEncodeAdvanced parses angle tags, not this table's square brackets.
        encoder["inputs"][field] = WorkflowGenerator.NodePath(layout, 0);
    }

    private static void ResizeReferences(WorkflowGenerator g)
    {
        if (!Active(g))
        {
            return;
        }
        List<Image> images = g.UserInput.Get(T2IParamTypes.PromptImages, new List<Image>());
        if (images.Count == 0)
        {
            return;
        }
        // The native base sampler just created one shared VAE encode per reference and
        // a ReferenceLatent link for each positive/negative stream. Keep that native routing.
        List<string> encoders = g.Workflow.Properties()
            .Where(p => p.Value.Value<string>("class_type") == "ReferenceLatent")
            .Select(p => p.Value["inputs"]?["latent"]?[0]?.ToString()).Where(id => id is not null).Distinct().ToList();
        if (encoders.Any(id => g.Workflow[id]?.Value<string>("class_type") != "VAEEncode"))
        {
            throw new SwarmUserErrorException("FLUX.3 reference routing differs from the supported native workflow. Update the SECourses extension and SwarmUI together.");
        }
        // Native Swarm shares a VAE encode when the same image is attached twice.
        // Match by its original pixel input before replacing any inputs, retaining
        // every reference position while resizing a shared encode only once.
        List<string> perImageEncoders = new();
        for (int i = 0; i < images.Count; i++)
        {
            JArray original = g.GetPromptImage(true, false, i);
            List<string> matches = encoders.Where(id => JToken.DeepEquals(g.Workflow[id]["inputs"]?["pixels"], original)).ToList();
            if (matches.Count != 1)
            {
                throw new SwarmUserErrorException("FLUX.3 reference routing differs from the supported native workflow. Update the SECourses extension and SwarmUI together.");
            }
            perImageEncoders.Add(matches[0]);
        }
        HashSet<string> updated = [];
        for (int i = 0; i < images.Count; i++)
        {
            if (!updated.Add(perImageEncoders[i]))
            {
                continue;
            }
            JArray pixels = g.GetPromptImage(false, false, i);
            string imageType = g.Workflow[pixels[0].ToString()]?.Value<string>("class_type");
            if (imageType is "SwarmLoadImageB64" or "LoadImage")
            {
                (int width, int height) = images[i].GetResolution();
                string white = g.CreateNode("EmptyImage", new JObject {
                    ["width"] = width, ["height"] = height, ["batch_size"] = 1, ["color"] = 16777215 });
                string composite = g.CreateNode("ImageCompositeMasked", new JObject {
                    ["destination"] = pixels, ["source"] = WorkflowGenerator.NodePath(white, 0),
                    ["mask"] = new JArray(pixels[0], 1), ["x"] = 0, ["y"] = 0, ["resize_source"] = false });
                pixels = WorkflowGenerator.NodePath(composite, 0);
            }
            string scaled = g.CreateNode("ImageScaleToTotalPixels", new JObject {
                ["image"] = pixels, ["upscale_method"] = "lanczos", ["megapixels"] = g.UserInput.Get(ReferenceMegapixels, 1), ["resolution_steps"] = 16 });
            g.Workflow[perImageEncoders[i]]["inputs"]["pixels"] = WorkflowGenerator.NodePath(scaled, 0);
        }
    }
}
