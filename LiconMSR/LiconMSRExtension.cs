using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.LiconMSR;

/// <summary>Adds LiconStudio's LTX 2.3 multiple-subject reference workflow to SwarmUI.</summary>
public class LiconMSRExtension : Extension
{
    private const string RequiredLoraName = "LTX-2.3-Licon-MSR-V2";
    private const string DistilledSigmas = "1.0, 0.99375, 0.9875, 0.98125, 0.975, 0.909375, 0.725, 0.421875, 0.0";

    private static bool _patched;
    private static T2IRegisteredParam<bool> Enabled;
    private static T2IRegisteredParam<int> ReferenceFrames;
    private static T2IRegisteredParam<double> GuideStrength;

    /// <summary>This extension is installed by the SECourses updater rather than a git clone, so metadata is set directly instead of read from git.</summary>
    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Adds the LiconStudio LTX 2.3 Multiple Subject Reference V2 workflow.";
        License = "MIT";
        Version = "1.0.0";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions";
    }

    public override void OnInit()
    {
        if (_patched)
        {
            Logs.Info("LTX 2.3 Licon MSR extension is already initialized.");
            return;
        }
        _patched = true;

        RegisterParameters();
        PatchSamplerStep();
        Logs.Info("LTX 2.3 Licon MSR V2 workflow support initialized.");
    }

    private static void RegisterParameters()
    {
        T2IParamGroup group = new("LTX 2.3 Licon MSR", Open: true, OrderPriority: 8,
            Description: "Generate an LTX 2.3 video from one to four subject references plus a background reference.");
        Enabled = T2IParamTypes.Register<bool>(new(
            "LTX 2.3 Licon MSR",
            "Use Prompt Images as Licon MSR references. Supply 2-5 images: up to four subjects first and the background last. Do not use Init Image.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", Group: group, OrderPriority: -10, ChangeWeight: 8));
        ReferenceFrames = T2IParamTypes.Register<int>(new(
            "LTX 2.3 Licon MSR Reference Frames",
            "Length of the internal reference sequence. Longer values give more reference capacity. Must be 17-65 in increments of 8.",
            "65", Min: 17, Max: 65, Step: 8, ViewMax: 65, FeatureFlag: "comfyui", Group: group,
            OrderPriority: -9, DependNonDefault: Enabled.Type.ID));
        GuideStrength = T2IParamTypes.Register<double>(new(
            "LTX 2.3 Licon MSR Guide Strength",
            "Strength of the multiple-subject reference guide.",
            "1", Min: 0, Max: 1, Step: 0.01, ViewMax: 1, FeatureFlag: "comfyui", Group: group,
            ViewType: ParamViewType.SLIDER, OrderPriority: -8, DependNonDefault: Enabled.Type.ID));
    }

    private static void PatchSamplerStep()
    {
        _ = WorkflowGenerator.Steps;
        List<WorkflowGenerator.WorkflowGenStep> steps = WorkflowGenerator.Steps;
        int samplerIndex = steps.FindIndex(step => Math.Abs(step.Priority - (-5)) < 0.0001);
        if (samplerIndex < 0)
        {
            Logs.Warning("Could not find the base sampler step for LTX 2.3 Licon MSR.");
            return;
        }

        var originalSamplerAction = steps[samplerIndex].Action;
        steps[samplerIndex] = new WorkflowGenerator.WorkflowGenStep(g =>
        {
            if (g.UserInput.Get(Enabled, false))
            {
                ApplyLiconMSR(g);
                return;
            }
            originalSamplerAction(g);
        }, -5);
        WorkflowGenerator.Steps = [.. steps.OrderBy(step => step.Priority)];
        Logs.Debug("Wrapped the base sampler step for LTX 2.3 Licon MSR V2.");
    }

    private static void ApplyLiconMSR(WorkflowGenerator g)
    {
        if (g.FinalLoadedModel?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID)
        {
            throw new SwarmUserErrorException("LTX 2.3 Licon MSR requires an LTX 2.x base model.");
        }
        if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image _))
        {
            throw new SwarmUserErrorException("LTX 2.3 Licon MSR uses Prompt Images. Remove the Init Image before generating.");
        }
        if (!g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> images) || images.Count < 2 || images.Count > 5)
        {
            throw new SwarmUserErrorException("LTX 2.3 Licon MSR requires 2-5 Prompt Images: one to four subjects first and the background last.");
        }
        List<string> loras = g.UserInput.Get(T2IParamTypes.Loras, new List<string>());
        if (!loras.Any(name => name.Contains(RequiredLoraName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SwarmUserErrorException($"LTX 2.3 Licon MSR requires the {RequiredLoraName} LoRA.");
        }
        if (g.CurrentVae is null || g.CurrentAudioVae is null)
        {
            throw new SwarmUserErrorException("The selected LTX model must provide both video and audio VAEs for Licon MSR.");
        }

        int steps = g.UserInput.Get(T2IParamTypes.Steps, 8);
        if (steps != 8)
        {
            throw new SwarmUserErrorException("LTX 2.3 Licon MSR V2 uses the official eight-step distilled sigma schedule. Set Steps to 8.");
        }
        int referenceFrames = g.UserInput.Get(ReferenceFrames, 65);
        if (referenceFrames < 17 || referenceFrames > 65 || (referenceFrames - 1) % 8 != 0)
        {
            throw new SwarmUserErrorException("LTX 2.3 Licon MSR Reference Frames must be 17, 25, 33, 41, 49, 57, or 65.");
        }

        int width = g.UserInput.GetImageWidth();
        int height = g.UserInput.GetImageHeight();
        int fps = g.UserInput.Get(T2IParamTypes.VideoFPS, 24);
        int outputFrames = g.UserInput.Get(T2IParamTypes.Text2VideoFrames, 121);
        long seed = g.UserInput.Get(T2IParamTypes.Seed);
        double cfg = g.UserInput.Get(T2IParamTypes.CFGScale, 1);
        double strength = g.UserInput.Get(GuideStrength, 1);

        JObject referenceInputs = new()
        {
            ["width"] = width,
            ["height"] = height,
            ["frame_count"] = referenceFrames.ToString()
        };
        for (int i = 0; i < images.Count - 1; i++)
        {
            WGNodeData loaded = g.LoadImage(images[i], "${promptimages." + i + "}", false);
            referenceInputs[$"{i + 1}"] = loaded.Path;
        }
        WGNodeData background = g.LoadImage(images[^1], "${promptimages." + (images.Count - 1) + "}", false);
        referenceInputs["background"] = background.Path;
        string referenceVideo = g.CreateNode("LiconMSR", referenceInputs);

        string frameRateConditioning = g.CreateNode("LTXVConditioning", new JObject()
        {
            ["positive"] = g.FinalPrompt,
            ["negative"] = g.FinalNegativePrompt,
            ["frame_rate"] = (double)fps
        });
        string guide = g.CreateNode("LTXAddVideoICLoRAGuide", new JObject()
        {
            ["positive"] = WorkflowGenerator.NodePath(frameRateConditioning, 0),
            ["negative"] = WorkflowGenerator.NodePath(frameRateConditioning, 1),
            ["vae"] = g.CurrentVae.Path,
            ["latent"] = g.CurrentMedia.Path,
            ["image"] = WorkflowGenerator.NodePath(referenceVideo, 0),
            ["frame_idx"] = 0,
            ["strength"] = strength,
            ["latent_downscale_factor"] = 1.0,
            ["crop"] = "center",
            ["use_tiled_encode"] = false,
            ["tile_size"] = 256,
            ["tile_overlap"] = 64
        });

        WGNodeData guidedVideo = g.CurrentMedia.WithPath([guide, 2], WGNodeData.DT_LATENT_VIDEO);
        guidedVideo.Width = width;
        guidedVideo.Height = height;
        guidedVideo.Frames = outputFrames;
        guidedVideo.FPS = fps;
        g.CurrentMedia = guidedVideo.AsSamplingLatent(g.CurrentVae, g.CurrentAudioVae);

        string guider = g.CreateNode("CFGGuider", new JObject()
        {
            ["model"] = g.CurrentModel.Path,
            ["positive"] = WorkflowGenerator.NodePath(guide, 0),
            ["negative"] = WorkflowGenerator.NodePath(guide, 1),
            ["cfg"] = cfg
        });
        string sampler = g.CreateNode("KSamplerSelect", new JObject()
        {
            ["sampler_name"] = "euler_ancestral"
        });
        string sigmas = g.CreateNode("ManualSigmas", new JObject()
        {
            ["sigmas"] = DistilledSigmas
        });
        string noise = g.CreateNode("RandomNoise", new JObject()
        {
            ["noise_seed"] = seed
        });
        string sampled = g.CreateNode("SamplerCustomAdvanced", new JObject()
        {
            ["noise"] = WorkflowGenerator.NodePath(noise, 0),
            ["guider"] = WorkflowGenerator.NodePath(guider, 0),
            ["sampler"] = WorkflowGenerator.NodePath(sampler, 0),
            ["sigmas"] = WorkflowGenerator.NodePath(sigmas, 0),
            ["latent_image"] = g.CurrentMedia.Path
        }, "10");

        WGNodeData sampledAV = new([sampled, 0], g, WGNodeData.DT_LATENT_AUDIOVIDEO, g.CurrentCompat());
        WGNodeData sampledVideo = sampledAV.AsLatentImage(g.CurrentVae);
        string cropped = g.CreateNode("LTXVCropGuides", new JObject()
        {
            ["positive"] = WorkflowGenerator.NodePath(guide, 0),
            ["negative"] = WorkflowGenerator.NodePath(guide, 1),
            ["latent"] = sampledVideo.Path
        });
        g.CurrentMedia = sampledVideo.WithPath([cropped, 2], WGNodeData.DT_LATENT_VIDEO);
        g.CurrentMedia.Width = width;
        g.CurrentMedia.Height = height;
        g.CurrentMedia.Frames = outputFrames;
        g.CurrentMedia.FPS = fps;

        Logs.Info($"Created LTX 2.3 Licon MSR V2 workflow with {images.Count - 1} subject reference(s), "
            + $"one background reference, {referenceFrames} internal reference frames, and {outputFrames} output frames.");
    }
}
