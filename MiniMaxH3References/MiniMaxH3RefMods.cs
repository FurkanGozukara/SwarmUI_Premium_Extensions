using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.MiniMaxH3References;

public partial class MiniMaxH3ReferencesExtension
{
    private static T2IRegisteredParam<bool> RefModsEnabled, RefModBuilder;
    private static T2IRegisteredParam<string> RefModSelection, RefModName, RefModDescription, RefModImageMode, RefModCompression;
    private static T2IRegisteredParam<int> RefModResolution;
    public const string RefModFeatureId = "minimax_h3_refmods";

    private void RegisterRefModParameters()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["SECoursesH3RefModBuild"] = RefModFeatureId;
        ScriptFiles.Add("Assets/refmod_picker.js");
        ScriptFiles.Add("Assets/minimax_h3_refmods.js");
        T2IParamGroup group = new("MiniMax H3 RefMods", Toggles: false, Open: false, OrderPriority: -8.9,
            Description: "Optional saved image, video and audio references. Import RefMod safetensors, add as many optional rows as needed, and cite @refmod1, @refmod2 etc. Build new files from the prompt's media with the RefMod Builder preset.");
        RefModsEnabled = T2IParamTypes.Register<bool>(new("MiniMax H3 RefMods", "Use the selected RefMods. Disabled and empty rows have no effect. Active RefMods select the matching Ref2VA model and LoRA variant.",
            "false", IgnoreIf: "false", FeatureFlag: RefModFeatureId, Group: group, OrderPriority: -10));
        RefModSelection = T2IParamTypes.Register<string>(new("MiniMax H3 RefMod Selection", "Use the picker below. API: JSON rows with file, enabled, strength (0-1), components (all/visual/audio), or one filename per line. Files live in the ComfyUI backend's models/refmods folder.",
            "[]", IgnoreIf: "[]", FeatureFlag: RefModFeatureId, Group: group, OrderPriority: -9,
            ViewType: ParamViewType.BIG, DependNonDefault: RefModsEnabled.Type.ID));
        RefModBuilder = T2IParamTypes.Register<bool>(new("MiniMax H3 RefMod Builder", "Save the prompt's attached images, videos and audio as a reusable safetensors bundle. No diffusion model or text encoder is run. Use the builder preset, attach media, then Generate. Existing files receive a numbered new filename.",
            "false", IgnoreIf: "false", FeatureFlag: RefModFeatureId, Group: group, OrderPriority: -8));
        RefModName = T2IParamTypes.Register<string>(new("MiniMax H3 RefMod Name", "Output filename inside models/refmods, optionally with a subfolder. Existing files are preserved.",
            "my_character", FeatureFlag: RefModFeatureId, Group: group, OrderPriority: -7, DependNonDefault: RefModBuilder.Type.ID));
        RefModDescription = T2IParamTypes.Register<string>(new("MiniMax H3 RefMod Description", "Description stored in the RefMod. The description is not a trigger word; use @refmod1 etc in generation prompts.",
            "", FeatureFlag: RefModFeatureId, Group: group, OrderPriority: -6, DependNonDefault: RefModBuilder.Type.ID));
        RefModResolution = T2IParamTypes.Register<int>(new("MiniMax H3 RefMod Resolution", "Maximum source edge before VAE encoding. 512 is a practical starting point; larger references cost more memory and sampling time.",
            "512", Min: 64, Max: 2048, Step: 32, FeatureFlag: RefModFeatureId, Group: group, OrderPriority: -5, DependNonDefault: RefModBuilder.Type.ID));
        RefModImageMode = T2IParamTypes.Register<string>(new("MiniMax H3 RefMod Image Mode", "One character stacks several photos into one video-kind reference. Separate references keeps each photo as a numbered picture. Keep different identities in separate RefMod files.",
            "one character", GetValues: _ => ["one character", "separate references"], FeatureFlag: RefModFeatureId, Group: group, OrderPriority: -4, DependNonDefault: RefModBuilder.Type.ID));
        RefModCompression = T2IParamTypes.Register<string>(new("MiniMax H3 RefMod Compression", "Optional spatial latent pooling. None preserves the encoded details. Smaller grids save tokens but can lose identity and motion detail; this is not LoRA training.",
            "none", GetValues: _ => ["none", "32", "16", "8"], FeatureFlag: RefModFeatureId, Group: group, OrderPriority: -3, DependNonDefault: RefModBuilder.Type.ID));
    }

    private static string ActiveRefMods(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(RefModsEnabled, false))
        {
            return "";
        }
        string selection = g.UserInput.Get(RefModSelection, "[]").Trim();
        if (selection.Length == 0)
        {
            return "";
        }
        if (!selection.StartsWith('['))
        {
            return selection;
        }
        JArray rows;
        try
        {
            rows = JArray.Parse(selection);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            throw new SwarmUserErrorException("RefMod Selection must be a JSON list or one filename per line.");
        }
        bool active = rows.Any(row => row.Type == JTokenType.String ? !string.IsNullOrWhiteSpace($"{row}")
            : row is JObject obj && !string.IsNullOrWhiteSpace($"{obj["file"]}") && (obj.Value<bool?>("enabled") ?? true) && (obj.Value<double?>("strength") ?? 1) > 0);
        return active ? selection : "";
    }

    private static void ApplyRefMods(WorkflowGenerator g)
    {
        bool build = g.UserInput.Get(RefModBuilder, false);
        string selection = ActiveRefMods(g);
        if (!build && selection.Length == 0)
        {
            return;
        }
        if (!g.Features.Contains(RefModFeatureId))
        {
            throw new SwarmUserErrorException("RefMods require the updated FoleyExtension nodes on the ComfyUI backend.");
        }
        if (build)
        {
            BuildRefModWorkflow(g);
            return;
        }
        // Walk only positive-conditioning dependencies. Negative encoders keep
        // their ordinary behavior, including zeroing a linked positive tensor.
        HashSet<string> positive = [];
        void walk(JToken token)
        {
            if (token is JArray link && link.Count == 2 && link[0].Type == JTokenType.String
                && g.Workflow[$"{link[0]}"] is JObject node && positive.Add($"{link[0]}"))
            {
                foreach (JProperty input in ((JObject)node["inputs"]).Properties())
                {
                    walk(input.Value);
                }
            }
        }
        walk(g.FinalPrompt);
        foreach (JProperty property in g.Workflow.Properties())
        {
            JObject inputs = property.Value["inputs"] as JObject;
            walk(inputs?["positive"]);
            walk(inputs?["positive_cond"]);
            if ($"{property.Value["class_type"]}" == "BasicGuider")
            {
                walk(inputs?["conditioning"]);
            }
        }
        int replaced = 0;
        foreach (string id in positive)
        {
            JObject node = (JObject)g.Workflow[id];
            string type = $"{node["class_type"]}";
            string replacement = type switch
            {
                "CLIPTextEncode" => "SECoursesH3RefModTextEncode",
                "SwarmTextEncodeAdvanced" => "SECoursesH3RefModSwarmTextEncode",
                "MiniMaxH3ReferenceToVideo" => "SECoursesH3RefModReferences",
                "MiniMaxH3ImageToVideo" => "SECoursesH3RefModImageToVideo",
                _ => null
            };
            if (replacement is null)
            {
                continue;
            }
            JObject inputs = (JObject)node["inputs"];
            inputs["refmods"] = selection;
            if (inputs["vae"] is null)
            {
                inputs["vae"] = g.CurrentVae.Path;
            }
            node["class_type"] = replacement;
            replaced++;
        }
        if (replaced == 0)
        {
            throw new SwarmUserErrorException("No supported MiniMax H3 positive text encoder was found for RefMods. Select a MiniMax H3 preset.");
        }
        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value["inputs"] is not JObject inputs)
            {
                continue;
            }
            foreach (string key in new[] { "unet_name", "lora_name" })
            {
                if (inputs[key]?.Type == JTokenType.String)
                {
                    string name = $"{inputs[key]}";
                    if (name.Contains("minimax", StringComparison.OrdinalIgnoreCase) || name.Contains("h3", StringComparison.OrdinalIgnoreCase))
                    {
                        name = Regex.Replace(name, "fl2va", "ref2va", RegexOptions.IgnoreCase);
                        name = Regex.Replace(name, "fl2v_turbo_8step_v1[.]0_768p", "ref2v_turbo_8step_v1.0_768p", RegexOptions.IgnoreCase);
                        name = Regex.Replace(name, "fl2v_turbo_4step_v1[.]2_768p", "ref2v_turbo_4step_v0.1", RegexOptions.IgnoreCase);
                        inputs[key] = name;
                    }
                }
            }
        }
        Logs.Info($"MiniMax H3 RefMods attached to {replaced} positive encoder(s).");
    }

    private static void BuildRefModWorkflow(WorkflowGenerator g)
    {
        JObject source = g.Workflow.Properties().Select(p => p.Value as JObject)
            .FirstOrDefault(n => $"{n?["class_type"]}" == "MiniMaxH3ReferenceToVideo");
        if (source?["inputs"] is not JObject original)
        {
            throw new SwarmUserErrorException("Use the MiniMax H3 RefMod Builder preset, enable MiniMax H3 References and attach at least one image, video or audio file.");
        }
        JObject inputs = new()
        {
            ["name"] = g.UserInput.Get(RefModName, "my_character"),
            ["description"] = g.UserInput.Get(RefModDescription, ""),
            ["resolution"] = g.UserInput.Get(RefModResolution, 512),
            ["image_mode"] = g.UserInput.Get(RefModImageMode, "one character"),
            ["compression"] = g.UserInput.Get(RefModCompression, "none")
        };
        foreach (JProperty property in original.Properties())
        {
            if (property.Name is "vae" or "audio_vae" || property.Name.StartsWith("ref_images.")
                || property.Name.StartsWith("ref_videos.") || property.Name.StartsWith("ref_video_audios.") || property.Name.StartsWith("ref_audios."))
            {
                inputs[property.Name] = property.Value.DeepClone();
            }
        }
        string builder = g.CreateNode("SECoursesH3RefModBuild", inputs);
        string output = g.CreateNode("SwarmSaveImageWS", new JObject { ["images"] = WorkflowGenerator.NodePath(builder, 1) });
        HashSet<string> keep = [];
        void retain(string id)
        {
            if (!keep.Add(id) || g.Workflow[id]?["inputs"] is not JObject nodeInputs)
            {
                return;
            }
            foreach (JProperty property in nodeInputs.Properties())
            {
                if (property.Value is JArray link && link.Count == 2 && link[0].Type == JTokenType.String && g.Workflow[$"{link[0]}"] is not null)
                {
                    retain($"{link[0]}");
                }
            }
        }
        retain(output);
        foreach (string id in g.Workflow.Properties().Select(p => p.Name).Where(id => !keep.Contains(id)).ToArray())
        {
            g.Workflow.Remove(id);
        }
        Logs.Info("MiniMax H3 RefMod builder: only source media and VAEs will run; the preview returns to SwarmUI.");
    }
}
