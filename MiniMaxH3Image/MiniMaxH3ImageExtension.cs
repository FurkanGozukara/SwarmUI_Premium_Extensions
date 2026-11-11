using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.MiniMaxH3Image;

/// <summary>The SECourses MiniMax H3 Image presets on SwarmUI's native MiniMax H3 Image workflow: MiniMax's official prompt
/// enhancer (SECoursesAudioTools SEH3ImageSwarmEnhance on a local Qwen3.5 9B), the enhancer's aspect ratio at the chosen
/// megapixels, @image1-@image16 prompt labels, seeded {a|b} choices and sequential prompt batches.</summary>
public class MiniMaxH3ImageExtension : Extension
{
    private const string Feature = "secourses_h3_image";
    private const string InputError = "secourses_h3_image_input_error";
    private const string CompatId = "minimax-h3-image";
    private const string EnhancerDefault = "qwen3.5_9b_int8_convrot.safetensors";
    private const int MaxImages = 16;
    // Same choices as the ComfyUI presets' SEH3ImagePromptEnhance / SEH3ImageGalleryEncode, which validate them again.
    private static readonly List<string> EnhanceModes = ["On: official enhancer with reasoning (best)", "On: fast enhancer without reasoning", "Off: use my prompt exactly as written"];
    private static readonly List<string> SizeModes = ["Auto: enhancer picks the aspect ratio (edits keep image 1's shape) at these megapixels", "Exact: always use the width and height above"];
    private static T2IRegisteredParam<bool> Enabled;
    private static T2IRegisteredParam<string> Enhance, SizeMode, EnhancerModel, PromptList;
    private static readonly Regex ReferenceAlias = new(@"(?<![\w@])@(?:image|img|picture|pic)(\d+)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Choice = new(@"(?<!\\)\{([^{}]*\|[^{}]*)\}");

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "MiniMax H3 Image presets: MiniMax's official prompt enhancer run locally, the enhancer's aspect ratio at your megapixels, @image1-@image16 prompt images and sequential prompt batches, on SwarmUI's native MiniMax H3 Image workflow.";
        License = "MIT";
        Version = "1.0.0";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/MiniMaxH3Image";
    }

    public override void OnPreInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["SEH3ImageSwarmEnhance"] = Feature;
    }

    public override void OnInit()
    {
        T2IParamGroup group = new("MiniMax H3 Image", Open: true, OrderPriority: -4,
            Description: "Write your idea in any language; a few words are enough. Attach up to 16 images to the prompt to edit or combine them and call them @image1, @image2, ... (@image1 is the image that is edited). Seed, steps, CFG, Megapixels, aspect ratio, images and LoRAs still use the normal controls.");
        Enabled = T2IParamTypes.Register<bool>(new("MiniMax H3 Image Advanced", "Enable the SECourses MiniMax H3 Image tools: the official prompt enhancer, Auto aspect ratio, @image labels, {a|b} choices and prompt batches. Only applies to MiniMax H3 Image models.",
            "false", IgnoreIf: "false", Group: group, FeatureFlag: Feature, OrderPriority: -20));
        Enhance = T2IParamTypes.Register<string>(new("MiniMax H3 Image Prompt Enhancer", "On (official, with reasoning): a local Qwen3.5 9B turns your idea into the detailed English caption MiniMax H3 Image was trained on, with MiniMax's official instructions, and picks the aspect ratio; with images it looks at them and writes the edit. About a minute on a fast GPU. Fast: the same without the reasoning, a few seconds and slightly less careful. Off: your prompt goes to the model exactly as written; use it for long captions you wrote yourself. The prompt the model received is saved in each image's metadata.",
            EnhanceModes[0], GetValues: _ => EnhanceModes, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -10));
        SizeMode = T2IParamTypes.Register<string>(new("MiniMax H3 Image Size", "Auto: the result keeps the pixel count of Width x Height (set with Megapixels) and takes the aspect ratio the enhancer chose; edits with the enhancer Off keep image 1's shape. Exact: always Width x Height.",
            SizeModes[0], GetValues: _ => SizeModes, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -9));
        EnhancerModel = T2IParamTypes.Register<string>(new("MiniMax H3 Image Enhancer Model", "The Qwen3.5 9B text encoder file that runs the enhancer (INT8 ConvRot in the MiniMax-H3 Core Bundle, W4A8 in the Low VRAM Bundle). It is loaded only while the enhancer is on.",
            EnhancerDefault, GetValues: EnhancerModels, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -8, IsAdvanced: true));
        PromptList = T2IParamTypes.Register<string>(new("MiniMax H3 Image Prompt List", "Extra prompts for Generate prompt batch. Add prompts or import .txt files or a folder; each file is one complete prompt. The main prompt runs first, then the list, with the same captured settings and attached images.",
            "[]", ViewType: ParamViewType.BIG, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 5, IntentionalUnused: true));
        T2IParamInput.SpecialParameterHandlers.Add(PrepareInput);
        // Early handlers run outside Swarm's readable-error catch: report our validation failures at the late stage.
        T2IParamInput.LateSpecialParameterHandlers.Insert(0, input =>
        {
            if (input.ExtraMeta.TryGetValue(InputError, out object error))
            {
                throw new SwarmUserErrorException(error.ToString());
            }
        });
        // After the native encode (-8) and the empty latent (-9) exist, before the sampler.
        WorkflowGenerator.AddStep(ApplyEnhancer, -6.9);
        ScriptFiles.Add("Assets/minimax_h3_image.js");
        StyleSheetFiles.Add("Assets/minimax_h3_image.css");
    }

    private static List<string> EnhancerModels(Session session)
    {
        List<string> models = Program.T2IModelSets["Clip"].ListModelNamesFor(session).Where(name => name.Contains("qwen3.5", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!models.Contains(EnhancerDefault))
        {
            models.Insert(0, EnhancerDefault);
        }
        return models;
    }

    private static bool IsH3Image(T2IModel model) => model?.ModelClass?.CompatClass?.ID == CompatId;

    private static void PrepareInput(T2IParamInput input)
    {
        input.ExtraMeta.Remove(InputError);
        if (!input.Get(Enabled, false))
        {
            return;
        }
        if (!IsH3Image(input.Get(T2IParamTypes.Model)))
        {
            // Presets only change their own keys. A leftover switch must never affect another model.
            foreach (T2IRegisteredParam<string> param in new[] { Enhance, SizeMode, EnhancerModel, PromptList })
            {
                input.Remove(param);
            }
            input.Remove(Enabled);
            input.RequiredFlags.Remove(Feature);
            return;
        }
        try
        {
            int count = input.Get(T2IParamTypes.PromptImages, new List<Image>()).Count;
            if (count > MaxImages)
            {
                throw new SwarmUserErrorException($"MiniMax H3 Image takes at most {MaxImages} prompt images. Remove the extra attachments.");
            }
            if (input.Get(T2IParamTypes.PromptVideos, new List<VideoFile>()).Count > 0 || input.Get(T2IParamTypes.PromptAudios, new List<AudioFile>()).Count > 0)
            {
                throw new SwarmUserErrorException("MiniMax H3 Image takes image attachments only. Remove the video and audio attachments.");
            }
            string prompt = input.Get(T2IParamTypes.Prompt, "");
            foreach (Match match in ReferenceAlias.Matches(prompt))
            {
                if (!int.TryParse(match.Groups[1].Value, out int number) || number < 1 || number > count)
                {
                    throw new SwarmUserErrorException($"{match.Value} has no matching prompt image. Update the prompt after removing or reordering attachments.");
                }
            }
            // Swarm resolves these with its seeded wildcard RNG during normal prompt preprocessing.
            while (Choice.IsMatch(prompt))
            {
                prompt = Choice.Replace(prompt, match => $"<random:{match.Groups[1].Value}>");
            }
            input.Set(T2IParamTypes.Prompt, prompt);
        }
        catch (SwarmUserErrorException ex)
        {
            input.ExtraMeta[InputError] = ex.Message;
        }
    }

    /// <summary>SEH3ImageSwarmEnhance reads the native encode's prompt and prompt images; its prompt replaces that text, its size the
    /// empty latent's, and the prompt is saved in the image metadata.</summary>
    private static void ApplyEnhancer(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(Enabled, false) || !g.IsMiniMaxH3Image())
        {
            return;
        }
        if (!g.Features.Contains(Feature))
        {
            throw new SwarmUserErrorException("The MiniMax H3 Image presets need the updated SECoursesAudioTools nodes on the ComfyUI backend. Run Windows_Install_or_Update_SwarmUI.bat and restart SwarmUI.");
        }
        JObject encoder = g.Workflow["6"] as JObject;
        string type = encoder?.Value<string>("class_type");
        string field = type == "CLIPTextEncode" ? "text" : type == "SwarmTextEncodeAdvanced" ? "prompt" : null;
        if (field is null || encoder["inputs"]?[field]?.Type != JTokenType.String)
        {
            throw new SwarmUserErrorException("The MiniMax H3 Image presets need a normal main prompt. Disable regional prompts, segments and custom workflows, or turn MiniMax H3 Image Advanced off.");
        }
        string enhance = g.UserInput.Get(Enhance, EnhanceModes[0]);
        JObject inputs = new()
        {
            ["prompt"] = encoder["inputs"][field].DeepClone(),
            ["enhance"] = enhance,
            ["seed"] = g.UserInput.Get(T2IParamTypes.Seed, 0),
            ["width"] = g.UserInput.GetImageWidth(),
            ["height"] = g.UserInput.GetImageHeight(),
            ["size_mode"] = g.UserInput.Get(SizeMode, SizeModes[0])
        };
        if (encoder["inputs"]["minimax_refs"] is JArray refs)
        {
            inputs["minimax_refs"] = refs.DeepClone();
        }
        if (enhance != EnhanceModes[2])
        {
            string loader = g.CreateNode("CLIPLoader", new JObject()
            {
                ["clip_name"] = g.UserInput.Get(EnhancerModel, EnhancerDefault),
                ["type"] = "minimax",
                ["device"] = "default"
            });
            inputs["enhancer_clip"] = WorkflowGenerator.NodePath(loader, 0);
        }
        string node = g.CreateNode("SEH3ImageSwarmEnhance", inputs);
        encoder["inputs"][field] = WorkflowGenerator.NodePath(node, 0);
        // An init image (image to image) keeps its own size: only the native empty latent follows the enhancer.
        foreach (JProperty latent in g.Workflow.Properties().Where(p => p.Value.Value<string>("class_type") == "SwarmEmptyMiniMaxH3ImageLatent"))
        {
            latent.Value["inputs"]["width"] = WorkflowGenerator.NodePath(node, 1);
            latent.Value["inputs"]["height"] = WorkflowGenerator.NodePath(node, 2);
        }
        g.CreateNode("SwarmAddSaveMetadataWS", new JObject()
        {
            ["key"] = "h3_image_prompt_received",
            ["value"] = WorkflowGenerator.NodePath(node, 0)
        });
    }
}
