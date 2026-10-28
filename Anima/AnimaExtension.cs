using System;
using System.Collections.Generic;
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

namespace FurkanGozukara.SwarmExtensions.Anima;

/// <summary>Single-image character/style guidance in SwarmUI's native Anima workflow.</summary>
public class AnimaExtension : Extension
{
    private const string Feature = "secourses_anima";
    private static T2IRegisteredParam<bool> Enabled, Reference;
    private static T2IRegisteredParam<Image> ReferenceImage;
    private static T2IRegisteredParam<double> Strength, Guidance, Phi;
    private static T2IRegisteredParam<string> Correction, PromptList;
    private static readonly ConditionalWeakTable<T2IParamInput, List<Image>> HeldImages = new();
    private static readonly Regex Choice = new(@"(?<!\\)\{([^{}]*\|[^{}]*)\}");

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Anima character and style references, separate reference guidance, APG/rescale, BF16 compute and sequential prompt batches. Native prompts, LoRAs, resolution, seeds, image-to-image and refiners stay in the normal SwarmUI workflow.";
        License = "MIT";
        Version = "1.0.2";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/Anima";
    }

    public override void OnPreInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["SEAnimaGalleryGuider"] = Feature;
    }

    public override void OnInit()
    {
        T2IParamGroup group = new("Anima", Open: true, OrderPriority: -4,
            Description: "Anima base v1.0: Euler/simple, 30 steps, CFG 4.5, shift 3. Attach one character image for reference transfer and describe the new scene. Ordinary LoRAs use the normal LoRA controls.");
        Enabled = T2IParamTypes.Register<bool>(new("Anima Advanced", "Enable the Anima reference and prompt-batch controls. Applies only to Anima models.",
            "false", IgnoreIf: "false", Group: group, FeatureFlag: Feature, OrderPriority: -20));
        Reference = T2IParamTypes.Register<bool>(new("Anima Reference", "Read character identity and part of its drawing style from one image. Upload below or attach an image to the prompt. Describe the new pose, place and framing without repeating appearance tags.",
            "false", IgnoreIf: "false", Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: -10));
        ReferenceImage = T2IParamTypes.Register<Image>(new("Anima Reference Image", "One clear still image. This image takes priority over prompt attachments; otherwise the first attached image is used. The reference is fitted with white letterboxing, preserving its proportions.",
            null, Group: group, FeatureFlag: Feature, DependNonDefault: Reference.Type.ID, OrderPriority: -9));
        Strength = T2IParamTypes.Register<double>(new("Anima Adapter Strength", "Reference adapter LoRA strength. Keep 1 for the trained recipe. The adapter is added separately from your ordinary LoRAs and only runs with a reference.",
            "1", Min: 0, Max: 2, Step: 0.05, Group: group, FeatureFlag: Feature, DependNonDefault: Reference.Type.ID, OrderPriority: -8));
        Guidance = T2IParamTypes.Register<double>(new("Anima Reference Guidance", "Reference guidance, separate from text CFG. 4.5 is the starting recipe; 6 holds more character detail but can saturate colors. 1 is weaker and cheaper.",
            "4.5", Min: 0, Max: 20, Step: 0.1, Group: group, FeatureFlag: Feature, DependNonDefault: Reference.Type.ID, OrderPriority: -7));
        Correction = T2IParamTypes.Register<string>(new("Anima Reference Correction", "APG controls color saturation. Rescale is an alternative with phi 0.5; none disables the correction.",
            "apg", GetValues: _ => ["apg", "rescale", "none"], Group: group, FeatureFlag: Feature, DependNonDefault: Reference.Type.ID, OrderPriority: -6));
        Phi = T2IParamTypes.Register<double>(new("Anima Rescale Phi", "Only used by the rescale correction.",
            "0.5", Min: 0, Max: 1, Step: 0.05, Group: group, FeatureFlag: Feature, DependNonDefault: Reference.Type.ID, OrderPriority: -5));
        PromptList = T2IParamTypes.Register<string>(new("Anima Prompt List", "Extra scene prompts for Generate prompt batch. Import .txt files or a folder; one file is one complete prompt. The main prompt runs first, followed by the list with the same captured settings and reference.",
            "[]", ViewType: ParamViewType.BIG, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 5, IntentionalUnused: true));
        T2IParamInput.SpecialParameterHandlers.Add(input =>
        {
            if (input.Get(Enabled, false) && input.Get(T2IParamTypes.Model)?.ModelClass?.CompatClass?.ID == "anima")
            {
                foreach (var parameter in new[] { T2IParamTypes.Prompt, T2IParamTypes.NegativePrompt })
                {
                    string prompt = input.Get(parameter, "");
                    while (Choice.IsMatch(prompt))
                    {
                        prompt = Choice.Replace(prompt, match => $"<random:{match.Groups[1].Value}>");
                    }
                    input.Set(parameter, prompt);
                }
            }
        });
        WorkflowGenerator.AddModelGenStep(UseBf16, -3.1);
        // Hold prompt attachments before the generic text/reference encoder sees them.
        WorkflowGenerator.AddStep(HoldImages, -8.5);
        WorkflowGenerator.AddStep(ReplaceSampler, -4.99);
        WorkflowGenerator.AddStep(RestoreImages, 9.9);
        ScriptFiles.Add("Assets/anima.js");
        StyleSheetFiles.Add("Assets/anima.css");
    }

    private static bool Active(WorkflowGenerator g) => g.IsAnima() && g.UserInput.Get(Enabled, false);

    private static void UseBf16(WorkflowGenerator g)
    {
        // ModelComputeDtype keeps INT8 storage and changes only arithmetic.
        if (!g.IsAnima())
        {
            return;
        }
        string node = g.CreateNode("ModelComputeDtype", new JObject { ["model"] = g.LoadingModel, ["dtype"] = "bf16" });
        g.LoadingModel = [node, 0];
    }

    private static void HoldImages(WorkflowGenerator g)
    {
        if (Active(g) && g.UserInput.Get(Reference, false)
            && g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> images) && images.Count > 0)
        {
            HeldImages.AddOrUpdate(g.UserInput, images);
            g.UserInput.Remove(T2IParamTypes.PromptImages);
        }
    }

    private static void RestoreImages(WorkflowGenerator g)
    {
        if (HeldImages.TryGetValue(g.UserInput, out List<Image> images))
        {
            g.UserInput.Set(T2IParamTypes.PromptImages, images);
            HeldImages.Remove(g.UserInput);
        }
    }

    private static void ReplaceSampler(WorkflowGenerator g)
    {
        if (!Active(g) || !g.UserInput.Get(Reference, false))
        {
            return;
        }
        Image reference = g.UserInput.Get(ReferenceImage, null);
        if (reference is null && HeldImages.TryGetValue(g.UserInput, out List<Image> images))
        {
            reference = images.FirstOrDefault();
        }
        if (reference is null || reference.Type.MetaType != MediaMetaType.Image)
        {
            throw new SwarmUserErrorException("Upload one still image in Anima Reference Image or attach it to the prompt, or turn Anima Reference off.");
        }
        if (g.Workflow["10"] is not JObject sampler || sampler.Value<string>("class_type") is not ("SwarmKSampler" or "KSamplerAdvanced"))
        {
            throw new SwarmUserErrorException("Anima Reference needs the normal Anima main sampler. Reset parameters and apply the Anima reference preset.");
        }
        JObject input = (JObject)sampler["inputs"];
        string references = g.CreateNode("SEFibo2SwarmInputs", new JObject
        {
            ["prompt"] = "", ["references_json"] = new JArray(reference.AsBase64).ToString(Formatting.None),
            ["init_base64"] = "", ["mask_base64"] = ""
        });
        string guider = g.CreateNode("SEAnimaGalleryGuider", new JObject
        {
            ["model"] = input["model"].DeepClone(), ["positive"] = input["positive"].DeepClone(),
            ["negative"] = input["negative"].DeepClone(), ["vae"] = g.CurrentVae.Path,
            ["references"] = WorkflowGenerator.NodePath(references, 0),
            ["width"] = g.UserInput.GetImageWidth(), ["height"] = g.UserInput.GetImageHeight(),
            ["batch_size"] = g.UserInput.Get(T2IParamTypes.BatchSize, 1),
            ["use_reference"] = true, ["cfg"] = input["cfg"].DeepClone(),
            ["adapter_lora"] = "anima_ref_adapter_pilot_r32.safetensors", ["adapter_strength"] = g.UserInput.Get(Strength, 1),
            ["ref_guidance"] = g.UserInput.Get(Guidance, 4.5), ["correction"] = g.UserInput.Get(Correction, "apg"),
            ["rescale_phi"] = g.UserInput.Get(Phi, 0.5)
        });
        string noise = g.CreateNode("SEAnimaNoise", new JObject
        {
            ["noise_seed"] = input["noise_seed"].DeepClone(), ["variation_seed"] = input["var_seed"]?.DeepClone() ?? 0,
            ["variation_strength"] = input["var_seed_strength"]?.DeepClone() ?? 0,
            ["add_noise"] = input.Value<string>("add_noise") != "disable"
        });
        string samplerSelect = g.CreateNode("KSamplerSelect", new JObject { ["sampler_name"] = input["sampler_name"].DeepClone() });
        int steps = input.Value<int>("steps"), start = input.Value<int>("start_at_step"), end = input.Value<int>("end_at_step");
        string schedule = g.CreateNode("BasicScheduler", new JObject
        {
            ["model"] = input["model"].DeepClone(), ["scheduler"] = input["scheduler"].DeepClone(), ["steps"] = steps, ["denoise"] = 1
        });
        JArray sigmas = WorkflowGenerator.NodePath(schedule, 0);
        if (end < steps)
        {
            string split = g.CreateNode("SplitSigmas", new JObject { ["sigmas"] = sigmas, ["step"] = end });
            sigmas = WorkflowGenerator.NodePath(split, 0);
        }
        if (start > 0)
        {
            string split = g.CreateNode("SplitSigmas", new JObject { ["sigmas"] = sigmas, ["step"] = start });
            sigmas = WorkflowGenerator.NodePath(split, 1);
        }
        sampler["class_type"] = "SamplerCustomAdvanced";
        sampler["inputs"] = new JObject
        {
            ["guider"] = WorkflowGenerator.NodePath(guider, 0), ["noise"] = WorkflowGenerator.NodePath(noise, 0),
            ["sampler"] = WorkflowGenerator.NodePath(samplerSelect, 0), ["sigmas"] = sigmas,
            ["latent_image"] = input["latent_image"].DeepClone()
        };
    }
}
