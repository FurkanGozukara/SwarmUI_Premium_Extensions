using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.MossImage;

/// <summary>Adds the SECourses MOSS-Image preset controls to Swarm's native MOSS-Image workflow.</summary>
public class MossImageExtension : Extension
{
    private const string Feature = "secourses_moss_image";
    private const string AttentionFeature = "secourses_model_attention_backend";
    private const string RescalingFeature = "secourses_temporal_score_rescaling";
    private const string TextMode = "Text / JSON prompt";
    private const string LayoutMode = "Layout boxes";
    private const string KitchenAttention = "Comfy Kitchen INT8 (fastest)";
    private const string PytorchAttention = "PyTorch (matches official)";
    private const string DefaultAttention = "Model / backend default";
    private const string InputError = "secourses_moss_input_error";
    public const string DeBandLora = "MOSS_Image_SFT_DeBand_L37-41.safetensors";
    private static T2IRegisteredParam<bool> Enabled, DeBand;
    private static T2IRegisteredParam<string> Mode, SkipLayers, Attention, Boxes, PromptList;
    private static T2IRegisteredParam<double> SkipStart, SkipEnd, ScoreRescaling;
    private static readonly Regex Choice = new(@"(?<!\\)\{([^{}]*\|[^{}]*)\}");
    // (red car:1.2): Qwen3.5 reads weights literally, so keep only the words. A digit before the colon is not a weight: (10:30), (4:3).
    private static readonly Regex Weight = new(@"(?<!\\)\(([^()]*?[^\d\s():])\s*:\s*((?:\d+(?:\.\d*)?|\.\d+))\s*\)");
    private static readonly Regex Bracket = new(@"(?<!\\)\[([^\[\]]*)\]");
    private static readonly Regex FromTo = new(@"^([^:|]*):([^:|]*):\s*((?:\d+(?:\.\d*)?|\.\d+))\s*$");
    private static readonly Regex ToAt = new(@"^([^:|]*):\s*((?:\d+(?:\.\d*)?|\.\d+))\s*$");
    private static readonly string[] LayoutLabels = ["object", "character", "text"];

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "MOSS-Image negative-branch layer skipping, safe attention backends, SFT de-banding, score rescaling, JSON layout prompts, visual layout boxes and prompt batches, using SwarmUI's native ComfyUI workflow.";
        License = "MIT";
        Version = "1.0.0";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/MossImage";
    }

    public override void OnPreInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["MOSSImageLayoutPrompt"] = Feature;
        ComfyUIBackendExtension.NodeToFeatureMap["ModelAttentionBackend"] = AttentionFeature;
        ComfyUIBackendExtension.NodeToFeatureMap["TemporalScoreRescaling"] = RescalingFeature;
    }

    public override void OnInit()
    {
        T2IParamGroup group = new("MOSS-Image", Open: true, OrderPriority: -4,
            Description: "Official MOSS-Image guidance: skip DiT layer 22 in the negative branch, 50 Euler steps, CFG 3, shift 5. Write plain prompts, tags or JSON layouts (up to 4K tokens). Layout boxes place objects, characters and text. Main seed, steps, CFG, sampler, resolution and LoRAs still apply.");
        Enabled = T2IParamTypes.Register<bool>(new("MOSS Image Advanced", "Enable the SECourses MOSS-Image controls: negative-branch layer skipping, attention backend, JSON layout drafts, {a|b} choices, layout boxes and prompt batches. Only applies to MOSS-Image models.",
            "false", IgnoreIf: "false", Group: group, FeatureFlag: Feature, OrderPriority: -20));
        Mode = T2IParamTypes.Register<string>(new("MOSS Image Mode", "Text mode takes a plain prompt, tags or a complete JSON prompt (official compiled layouts and pixel drafts from the MOSS prompt enhancer both work). Layout boxes builds the JSON layout from the boxes below and uses the main prompt as the overall image caption.",
            TextMode, GetValues: _ => [TextMode, LayoutMode], Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -10));
        SkipLayers = T2IParamTypes.Register<string>(new("MOSS Image Negative Skip Layers", "Zero-based DiT layers skipped in the negative (CFG) branch, the official two-pass skip-layer guidance. 22 is the official default; 24 and 26 also help. Comma separated, empty turns it off (more realistic but details may collapse). Needs CFG above 1.",
            "22", Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -8));
        SkipStart = T2IParamTypes.Register<double>(new("MOSS Image Skip Start", "Fraction of sampling at which negative-branch skipping starts. Official default 0.",
            "0", Min: 0, Max: 1, Step: 0.01, ViewType: ParamViewType.SLIDER, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -7));
        SkipEnd = T2IParamTypes.Register<double>(new("MOSS Image Skip End", "Fraction of sampling at which negative-branch skipping ends. Official default 1.",
            "1", Min: 0, Max: 1, Step: 0.01, ViewType: ParamViewType.SLIDER, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -6));
        ScoreRescaling = T2IParamTypes.Register<double>(new("MOSS Image Score Rescaling", "ComfyUI Temporal Score Rescaling k. 1 turns it off (official recipe). The SECourses Fast 25 Steps preset uses 0.95 with ER-SDE: lower values give more detail, higher values smoother images.",
            "1", Min: 0.5, Max: 1.5, Step: 0.01, ViewType: ParamViewType.SLIDER, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -4.5));
        DeBand = T2IParamTypes.Register<bool>(new("MOSS Image SFT De-Band", $"Removes the faint vertical color stripes MOSS-Image SFT draws on skies, fog, walls and backdrops by loading {DeBandLora} (MATLOWAI/MOSS-Image-SFT-DeBand, Apache-2.0, part of the MOSS-Image Core Bundle). Applied only to SFT models (file name contains 'sft'); Base models are left unchanged.",
            "true", Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -4));
        Attention = T2IParamTypes.Register<string>(new("MOSS Image Attention", "Attention used by the MOSS-Image DiT. PyTorch matched the official MOSS-Image outputs with both the INT8 ConvRot and BF16 weights. The INT8 ConvRot files request Comfy Kitchen INT8 attention by default: about 20% faster, but together with the INT8 weights it changed images noticeably and could add a coarse texture/muddy colors on an RTX 3090. The backend default uses that file preference or your launch flags: BF16 with --use-sage-attention produced black (NaN) images on an RTX 3090.",
            PytorchAttention, GetValues: _ => [PytorchAttention, KitchenAttention, DefaultAttention], Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -5));
        Boxes = T2IParamTypes.Register<string>(new("MOSS Image Layout Boxes", "Draw or resize boxes and describe each one. Object and Character boxes place things; Text boxes render the exact text. Coordinates are normalized [top, left, bottom, right] and follow the output aspect ratio. Boxes guide the model; they are not masks.",
            "[]", ViewType: ParamViewType.BIG, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 0));
        PromptList = T2IParamTypes.Register<string>(new("MOSS Image Prompt List", "Extra prompts for Generate prompt batch. Add prompts or import .txt files/a folder; each text file is one complete prompt, JSON layouts included. The batch includes the main prompt and runs sequentially with the same captured settings.",
            "[]", ViewType: ParamViewType.BIG, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 5, IntentionalUnused: true));
        T2IParamInput.SpecialParameterHandlers.Add(PrepareInput);
        // Early handlers run outside Swarm's readable-error catch. Report our
        // validation failures at the late stage, before claiming a backend.
        T2IParamInput.LateSpecialParameterHandlers.Insert(0, input =>
        {
            if (input.ExtraMeta.TryGetValue(InputError, out object error))
            {
                throw new SwarmUserErrorException(error.ToString());
            }
        });
        // After LoRAs (-10) and the native sigma shift, so the skip window shares the model's own schedule.
        WorkflowGenerator.AddModelGenStep(ApplyModelPatches, -3.2);
        WorkflowGenerator.AddStep(AddLayout, -6.9);
        ScriptFiles.Add("Assets/moss_image.js");
        StyleSheetFiles.Add("Assets/moss_image.css");
    }

    private static bool IsMoss(T2IModel model)
    {
        if (model?.ModelClass is not null)
        {
            return model.ModelClass.CompatClass?.ID == "moss-image";
        }
        return model is not null && File.Exists(model.RawFilePath)
            && Path.GetExtension(model.RawFilePath) is ".safetensors" or ".sft"
            && T2IModelClassSorter.ModelClasses.TryGetValue("moss-image", out T2IModelClass cls)
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
        if (!IsMoss(input.Get(T2IParamTypes.Model)))
        {
            // Presets only change their own keys. A leftover switch must never affect another model.
            input.Remove(Enabled);
            input.Remove(Mode);
            input.Remove(SkipLayers);
            input.Remove(SkipStart);
            input.Remove(SkipEnd);
            input.Remove(Attention);
            input.Remove(DeBand);
            input.Remove(ScoreRescaling);
            input.Remove(Boxes);
            input.Remove(PromptList);
            input.RequiredFlags.Remove(Feature);
            return;
        }
        ParseLayers(input.Get(SkipLayers, "22"));
        if (input.Get(SkipStart, 0) > input.Get(SkipEnd, 1))
        {
            throw new SwarmUserErrorException("MOSS Image Skip Start must not be larger than Skip End.");
        }
        int width = input.GetImageWidth(), height = input.GetImageHeight();
        string prompt = input.Get(T2IParamTypes.Prompt, "");
        if (input.Get(Mode, TextMode) == LayoutMode)
        {
            ReadBoxes(input.Get(Boxes, "[]"));
            if (ParseJsonObject(prompt) is not null)
            {
                throw new SwarmUserErrorException("Layout boxes mode builds the JSON layout itself. Put only the overall image caption in the prompt, or switch MOSS Image Mode to Text / JSON prompt to use your own JSON layout.");
            }
        }
        input.Set(T2IParamTypes.Prompt, PreparePrompt(prompt, width, height));
        input.Set(T2IParamTypes.NegativePrompt, PreparePrompt(input.Get(T2IParamTypes.NegativePrompt, ""), width, height));
    }

    public static List<int> ParseLayers(string text)
    {
        List<int> layers = [];
        foreach (string part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int layer) || layer < 0 || layer > 41)
            {
                throw new SwarmUserErrorException($"MOSS Image Negative Skip Layers must be comma-separated layer numbers from 0 to 41 (the model has 42 layers), got '{part}'.");
            }
            if (!layers.Contains(layer))
            {
                layers.Add(layer);
            }
        }
        return layers;
    }

    public static JObject ParseJsonObject(string prompt)
    {
        string text = (prompt ?? "").Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}'))
        {
            return null;
        }
        try
        {
            return JToken.Parse(text, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }) as JObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>JSON prompts pass through (pixel drafts are compiled); plain prompts get seeded choices, schedule tags and no literal weights.</summary>
    public static string PreparePrompt(string prompt, int width, int height)
    {
        JObject json = ParseJsonObject(prompt);
        if (json is not null)
        {
            if (json.Count == 1 && json["caption"]?.Type == JTokenType.String)
            {
                prompt = json.Value<string>("caption");
            }
            else if (json.Value<string>("bbox_coord_space") == "abs" || json.Value<string>("bbox_format") == "xyxy")
            {
                return CompileDraft(json, width, height);
            }
            else
            {
                return json.ToString(Formatting.None);
            }
        }
        // Swarm resolves these with its seeded wildcard RNG during normal prompt preprocessing.
        while (Choice.IsMatch(prompt))
        {
            prompt = Choice.Replace(prompt, match => $"<random:{match.Groups[1].Value}>");
        }
        string previous = null;
        while (previous != prompt)
        {
            previous = prompt;
            prompt = Weight.Replace(prompt, match => double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) <= 3 ? match.Groups[1].Value : match.Value);
        }
        // Comfy-style [a:b:0.5] / [b:0.5] / [a|b] become Swarm tags; other brackets stay literal text.
        return Bracket.Replace(prompt, match =>
        {
            string inner = match.Groups[1].Value;
            Match fromTo = FromTo.Match(inner), toAt = ToAt.Match(inner);
            if (fromTo.Success)
            {
                return $"<fromto[{fromTo.Groups[3].Value}]:{fromTo.Groups[1].Value}||{fromTo.Groups[2].Value}>";
            }
            if (toAt.Success)
            {
                return $"<fromto[{toAt.Groups[2].Value}]: ||{toAt.Groups[1].Value}>";
            }
            if (inner.Contains('|') && !inner.Contains(':'))
            {
                return $"<alternate:{inner.Replace("|", "||")}>";
            }
            return match.Value;
        });
    }

    /// <summary>OpenMOSS prompt-enhancer pixel draft (abs, xyxy, width, height) -> model JSON, matching its compile_prompt.py.
    /// Boxes are scaled when the generation size differs from the draft canvas.</summary>
    public static string CompileDraft(JObject draft, int width, int height)
    {
        int canvasW = draft.Value<int?>("width") ?? 0, canvasH = draft.Value<int?>("height") ?? 0;
        if (canvasW <= 0 || canvasH <= 0)
        {
            throw new SwarmUserErrorException("A MOSS-Image pixel layout draft needs integer width and height.");
        }
        if (draft["summary"] is not JObject summary || string.IsNullOrWhiteSpace(summary.Value<string>("caption")))
        {
            throw new SwarmUserErrorException("A MOSS-Image layout needs summary.caption.");
        }
        if (draft["items"] is not JArray items || items.Count == 0)
        {
            throw new SwarmUserErrorException("A MOSS-Image layout needs a nonempty items array.");
        }
        double sx = (double)width / canvasW, sy = (double)height / canvasH;
        JArray compiled = [];
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not JObject item || !LayoutLabels.Contains(item.Value<string>("label")) || string.IsNullOrWhiteSpace(item.Value<string>("caption")))
            {
                throw new SwarmUserErrorException($"Layout item {i + 1} needs label object, character or text and a caption.");
            }
            if (item["bbox"] is not JArray box || box.Count != 4 || box.Any(v => v.Type is not (JTokenType.Integer or JTokenType.Float)))
            {
                throw new SwarmUserErrorException($"Layout item {i + 1} needs bbox [x_min, y_min, x_max, y_max].");
            }
            double[] b = box.Values<double>().ToArray();
            if (!(0 <= b[0] && b[0] < b[2] && b[2] <= canvasW && 0 <= b[1] && b[1] < b[3] && b[3] <= canvasH))
            {
                throw new SwarmUserErrorException($"Layout item {i + 1} box must lie inside the {canvasW}x{canvasH} draft canvas.");
            }
            static int Floor(double v) => (int)Math.Floor(Math.Round(v, 9));
            static int Ceil(double v) => (int)Math.Ceiling(Math.Round(v, 9));
            JObject result = (JObject)item.DeepClone();
            result["bbox"] = new JArray(Floor(b[1] * sy) - height / 2, Floor(b[0] * sx) - width / 2, Ceil(b[3] * sy) - height / 2, Ceil(b[2] * sx) - width / 2);
            compiled.Add(result);
        }
        JObject layout = new() { ["summary"] = summary, ["items"] = compiled, ["bbox_coord_space"] = "abs_centralized", ["bbox_format"] = "yxyx" };
        return layout.ToString(Formatting.None);
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
            throw new SwarmUserErrorException("MOSS Image Layout Boxes must be a JSON array. Use the visual editor or restore the layout preset.");
        }
        if (boxes.Count is < 1 or > 32)
        {
            throw new SwarmUserErrorException("Layout boxes mode needs between 1 and 32 boxes. Draw a box or choose Text / JSON prompt.");
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
            string type = box.Value<string>("type") ?? "obj";
            if (type is not ("obj" or "character" or "text"))
            {
                throw new SwarmUserErrorException($"Box {i + 1} type must be obj, character or text.");
            }
            if (string.IsNullOrWhiteSpace(box.Value<string>("desc")) && string.IsNullOrWhiteSpace(box.Value<string>("text")))
            {
                throw new SwarmUserErrorException($"Describe the object, character or text in box {i + 1}.");
            }
            if (type == "text" && string.IsNullOrWhiteSpace(box.Value<string>("text")))
            {
                throw new SwarmUserErrorException($"Text box {i + 1} needs the exact text to render.");
            }
            box["type"] = type;
        }
        return boxes;
    }

    private static bool Active(WorkflowGenerator g) => g.UserInput.Get(Enabled, false) && g.IsMossImage();

    private static void ApplyModelPatches(WorkflowGenerator g)
    {
        if (!Active(g))
        {
            return;
        }
        string attention = g.UserInput.Get(Attention, PytorchAttention);
        if (attention != DefaultAttention)
        {
            if (g.Features.Contains(AttentionFeature))
            {
                string node = g.CreateNode("ModelAttentionBackend", new JObject()
                {
                    ["model"] = g.LoadingModel,
                    ["attention"] = attention == PytorchAttention ? "pytorch attention" : "comfy kitchen attention"
                });
                g.LoadingModel = [node, 0];
            }
            else
            {
                Logs.Warning("MOSS Image Attention needs ComfyUI's Model Attention Backend node. Update the ComfyUI backend; using its default attention.");
            }
        }
        if (g.UserInput.Get(DeBand, true))
        {
            ApplyDeBand(g);
        }
        double rescaling = g.UserInput.Get(ScoreRescaling, 1);
        if (Math.Abs(rescaling - 1) > 1e-6)
        {
            if (g.Features.Contains(RescalingFeature))
            {
                string node = g.CreateNode("TemporalScoreRescaling", new JObject()
                {
                    ["model"] = g.LoadingModel,
                    ["tsr_k"] = rescaling,
                    ["tsr_sigma"] = 1.0
                });
                g.LoadingModel = [node, 0];
            }
            else
            {
                Logs.Warning("MOSS Image Score Rescaling needs ComfyUI's Temporal Score Rescaling node. Update the ComfyUI backend; generating without it.");
            }
        }
        List<int> layers = ParseLayers(g.UserInput.Get(SkipLayers, "22"));
        if (layers.Count > 0)
        {
            string node = g.CreateNode("SkipLayerGuidanceDiTSimple", new JObject()
            {
                ["model"] = g.LoadingModel,
                ["double_layers"] = "",
                ["single_layers"] = string.Join(", ", layers),
                ["start_percent"] = g.UserInput.Get(SkipStart, 0),
                ["end_percent"] = g.UserInput.Get(SkipEnd, 1)
            });
            g.LoadingModel = [node, 0];
        }
    }

    /// <summary>The SFT banding fix reverts SFT layers 37-41 to Base weights, so it must never be applied to Base.</summary>
    private static void ApplyDeBand(WorkflowGenerator g)
    {
        string modelName = Path.GetFileNameWithoutExtension(g.FinalLoadedModel?.Name ?? "").ToLowerInvariant();
        if (!modelName.Contains("sft") || modelName.Contains("base"))
        {
            return;
        }
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler loras) || !loras.Models.TryGetValue(DeBandLora, out T2IModel lora))
        {
            Logs.Warning($"MOSS Image SFT De-Band is on, but {DeBandLora} is not in your LoRA folder. Download the MOSS-Image Core Bundle with the SECourses model downloader; generating without the fix.");
            return;
        }
        string node = g.CreateNode("LoraLoaderModelOnly", new JObject()
        {
            ["model"] = g.LoadingModel,
            ["lora_name"] = lora.ToString(g.ModelFolderFormat),
            ["strength_model"] = 1.0
        });
        g.LoadingModel = [node, 0];
    }

    private static void AddLayout(WorkflowGenerator g)
    {
        if (!Active(g) || g.UserInput.Get(Mode, TextMode) != LayoutMode)
        {
            return;
        }
        if (!g.Features.Contains(Feature))
        {
            throw new SwarmUserErrorException("Update the ComfyUI backend to native MOSS-Image support, then restart it. MOSSImageLayoutPrompt is required.");
        }
        JObject encoder = g.Workflow["6"] as JObject;
        string type = encoder?.Value<string>("class_type");
        string field = type == "CLIPTextEncode" ? "text" : type == "SwarmTextEncodeAdvanced" ? "prompt" : null;
        if (field is null || encoder["inputs"]?[field]?.Type != JTokenType.String)
        {
            throw new SwarmUserErrorException("The MOSS-Image layout preset needs a normal main prompt. Disable conflicting custom workflow/region overrides and try again.");
        }
        int width = g.UserInput.GetImageWidth(), height = g.UserInput.GetImageHeight();
        JArray pixelBoxes = [];
        List<string> characters = [];
        JArray boxes = ReadBoxes(g.UserInput.Get(Boxes, "[]"));
        for (int i = 0; i < boxes.Count; i++)
        {
            JObject box = (JObject)boxes[i];
            double[] b = box["bbox"].Values<double>().ToArray();
            string type2 = box.Value<string>("type");
            if (type2 == "character")
            {
                characters.Add($"{i + 1}");
            }
            pixelBoxes.Add(new JObject
            {
                ["x"] = (int)Math.Round(b[1] * width), ["y"] = (int)Math.Round(b[0] * height),
                ["width"] = Math.Max(1, (int)Math.Round((b[3] - b[1]) * width)),
                ["height"] = Math.Max(1, (int)Math.Round((b[2] - b[0]) * height)),
                ["metadata"] = new JObject { ["type"] = type2 == "text" ? "text" : "obj", ["desc"] = box["desc"] ?? "", ["text"] = box["text"] ?? "" }
            });
        }
        string boxesNode = g.CreateNode("CreateBoundingBoxes", new JObject
        {
            ["width"] = width, ["height"] = height,
            ["editor_state"] = new JArray(), ["bboxes"] = pixelBoxes.ToString(Formatting.None)
        });
        string layout = g.CreateNode("MOSSImageLayoutPrompt", new JObject
        {
            ["prompt"] = encoder["inputs"][field].DeepClone(),
            ["element"] = WorkflowGenerator.NodePath(boxesNode, 2),
            ["width"] = width, ["height"] = height,
            ["character_boxes"] = string.Join(", ", characters)
        });
        encoder["inputs"][field] = WorkflowGenerator.NodePath(layout, 0);
    }
}
