using System;
using System.Collections.Generic;
using System.Linq;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.Ltxv2LatentUpscale;

public class Ltxv2LatentUpscaleExtension : Extension
{
    private static bool _patched;

    private static T2IRegisteredParam<bool> FoleyAudioGeneration;
    private static T2IRegisteredParam<bool> FoleyAutoReencodeInput;
    private static T2IRegisteredParam<bool> FoleyAutoLongVideo;
    private static T2IRegisteredParam<int> FoleyMaximumFrames;
    private static T2IRegisteredParam<int> FoleyWindowFrames;
    private static T2IRegisteredParam<double> FoleyWindowOverlap;
    private static T2IRegisteredParam<int> FoleyMaximumWindows;
    private static T2IRegisteredParam<int> FoleyConditioningSize;
    private static T2IRegisteredParam<double> FoleyAudioCFG;
    private static T2IRegisteredParam<double> FoleyVideoCFG;
    private static T2IRegisteredParam<double> FoleySTGScale;
    private static T2IRegisteredParam<double> FoleyModalityScale;

    /// <summary>This extension is installed by the SECourses updater rather than a git clone, so metadata is set directly instead of read from git.</summary>
    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Adds LTXV2 latent upscaling and native LTX 2.3 Foley video-to-audio generation.";
        License = "MIT";
        Version = "0.8.1";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions";
    }

    public override void OnInit()
    {
        if (_patched)
        {
            Logs.Info("LTXV2 I2V Latent Upscale extension already patched.");
            return;
        }
        _patched = true;

        RegisterFoleyParameters();
        PatchWorkflowSteps();
        Logs.Info("LTXV2 extension initialized with latent upscaling and LTX 2.3 Foley V2A support.");
    }

    private static void RegisterFoleyParameters()
    {
        T2IParamGroup group = new("LTX 2.3 Foley Audio", Open: true, OrderPriority: 8,
            Description: "Generate synchronized Foley sound effects for an input video with the LTX 2.3 Foley V2A LoRA.");
        FoleyAudioGeneration = T2IParamTypes.Register<bool>(new("LTX 2.3 Foley Audio", "Freezes the input video and generates only its synchronized audio track. Requires an LTX 2.3 base model, an input video, and the Foley V2A LoRA.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", Group: group, OrderPriority: -10, ChangeWeight: 8));
        FoleyAutoReencodeInput = T2IParamTypes.Register<bool>(new("LTX 2.3 Foley Auto Reencode Input", "Streams and resamples the source video to the selected Video FPS before it becomes an image tensor. Leave enabled for correct timing and low RAM use. The Foley preset selects 24 FPS.",
            "true", IgnoreIf: "true", FeatureFlag: "comfyui", Group: group, OrderPriority: -9, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleyAutoLongVideo = T2IParamTypes.Register<bool>(new("LTX 2.3 Foley Auto Long Video", "When enabled, the complete input video is automatically processed as overlapping low-memory windows and the stitched audio is muxed onto the original compressed video. Leave Maximum Frames at its 169 default for automatic full-video length; enter a different value to impose a custom total-frame limit.",
            "true", IgnoreIf: "true", FeatureFlag: "comfyui", Group: group, OrderPriority: -8.5, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleyMaximumFrames = T2IParamTypes.Register<int>(new("LTX 2.3 Foley Maximum Frames", "Maximum input frames to process after FPS conversion. The workflow automatically rounds down to a valid 8n+1 LTX frame count. The recommended default is 169; larger custom values are allowed but can require substantially more RAM and VRAM.",
            "169", Min: 1, Max: 4097, Step: 8, ViewMax: 673, FeatureFlag: "comfyui", Group: group, OrderPriority: -8, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleyWindowFrames = T2IParamTypes.Register<int>(new("LTX 2.3 Foley Window Frames", "Frame count for each automatically stitched long-video window. Must be 8n+1. The community sliding-window workflow recommends 89.",
            "89", Min: 9, Max: 257, Step: 8, ViewMax: 169, FeatureFlag: "comfyui", Group: group, OrderPriority: -7.9, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleyWindowOverlap = T2IParamTypes.Register<double>(new("LTX 2.3 Foley Window Overlap Seconds", "Crossfade overlap between neighboring Foley windows. One second is the community workflow default; reduce it if distinct sounds repeat at boundaries.",
            "1", Min: 0, Max: 10, Step: 0.1, ViewMax: 3, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -7.8, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleyMaximumWindows = T2IParamTypes.Register<int>(new("LTX 2.3 Foley Maximum Windows", "Safety limit for automatic long-video processing. Increase this for videos that require more than 16 windows.",
            "16", Min: 1, Max: 256, Step: 1, ViewMax: 32, FeatureFlag: "comfyui", Group: group, OrderPriority: -7.7, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleyConditioningSize = T2IParamTypes.Register<int>(new("LTX 2.3 Foley Conditioning Size", "Square resolution used only for each Foley analysis window. Final output keeps the original compressed video resolution.",
            "576", Min: 256, Max: 1024, Step: 32, ViewMax: 768, FeatureFlag: "comfyui", Group: group, OrderPriority: -7.6, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleyAudioCFG = T2IParamTypes.Register<double>(new("LTX 2.3 Foley Audio CFG", "Audio guidance. Lightricks recommends 6; values below 6 can produce near-silent audio on some seeds.",
            "6", Min: 0, Max: 20, Step: 0.1, ViewMax: 10, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -7, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleyVideoCFG = T2IParamTypes.Register<double>(new("LTX 2.3 Foley Video CFG", "Video guidance for the frozen source-video branch.",
            "1", Min: 0, Max: 20, Step: 0.1, ViewMax: 10, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -6, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleySTGScale = T2IParamTypes.Register<double>(new("LTX 2.3 Foley STG Scale", "Spatiotemporal guidance scale. The recommended Foley setting is 1 with block 29.",
            "1", Min: 0, Max: 10, Step: 0.1, ViewMax: 3, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -5, DependNonDefault: FoleyAudioGeneration.Type.ID));
        FoleyModalityScale = T2IParamTypes.Register<double>(new("LTX 2.3 Foley Modality Scale", "Cross-modality guidance strength between video and audio.",
            "3", Min: 0, Max: 20, Step: 0.1, ViewMax: 10, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -4, DependNonDefault: FoleyAudioGeneration.Type.ID));
    }

    private static void PatchWorkflowSteps()
    {
        _ = WorkflowGenerator.Steps;

        // Find and wrap the ImageToVideo step (priority 11)
        List<WorkflowGenerator.WorkflowGenStep> steps = WorkflowGenerator.Steps;
        int i2vIndex = steps.FindIndex(step => Math.Abs(step.Priority - 11) < 0.0001);
        
        if (i2vIndex >= 0)
        {
            var originalI2VAction = steps[i2vIndex].Action;
            steps[i2vIndex] = new WorkflowGenerator.WorkflowGenStep(g =>
            {
                // Only the LTX upscale path needs a raw source image. Converting
                // eagerly breaks audio-only workflows whose current media is AUDIO.
                bool shouldUpscale = ShouldApplyI2VUpscale(g);
                JArray originalInputImage = null;
                if (shouldUpscale && g.CurrentMedia is not null)
                {
                    originalInputImage = new JArray(g.CurrentMedia.AsRawImage(g.CurrentVae).Path);
                }

                if (shouldUpscale)
                {
                    Logs.Info("Using upscale workflow, skipping base I2V workflow");
                    // Don't call originalI2VAction - we'll create complete workflow in upscale
                    TryApplyLtxv2I2VUpscale(g, originalInputImage);
                }
                else
                {
                    // Normal I2V workflow without upscaling
                    originalI2VAction(g);
                }
            }, 11);
            Logs.Debug("Wrapped ImageToVideo step (priority 11)");
        }
        else
        {
            Logs.Warning("Could not find ImageToVideo step to patch");
        }

        // Find and wrap the Refiner step (last priority -4)
        int refinerIndex = steps.FindLastIndex(step => Math.Abs(step.Priority - (-4)) < 0.0001);

        if (refinerIndex >= 0)
        {
            var originalRefinerAction = steps[refinerIndex].Action;
            steps[refinerIndex] = new WorkflowGenerator.WorkflowGenStep(g =>
            {
                // Skip refiner ONLY for LTXV2 I2V with latent upscaling
                if (ShouldSkipRefinerForLtxv2I2V(g))
                {
                    Logs.Info("Skipping refiner for LTXV2 I2V with latent upscaling (handled in video workflow)");
                    return;
                }

                // Otherwise run original refiner
                originalRefinerAction(g);
            }, -4);
            Logs.Debug("Wrapped Refiner step (priority -4)");
        }
        else
        {
            Logs.Warning("Could not find Refiner step to patch");
        }

        // Replace the ordinary base sampler with the dedicated V2A sampler when Foley is enabled.
        int samplerIndex = steps.FindIndex(step => Math.Abs(step.Priority - (-5)) < 0.0001);
        if (samplerIndex >= 0)
        {
            var originalSamplerAction = steps[samplerIndex].Action;
            steps[samplerIndex] = new WorkflowGenerator.WorkflowGenStep(g =>
            {
                if (g.UserInput.Get(FoleyAudioGeneration, false))
                {
                    ApplyLtx23Foley(g);
                    return;
                }
                originalSamplerAction(g);
            }, -5);
            Logs.Debug("Wrapped base sampler step (priority -5) for LTX 2.3 Foley V2A");
        }
        else
        {
            Logs.Warning("Could not find base sampler step to patch for LTX 2.3 Foley V2A");
        }

        WorkflowGenerator.Steps = [.. steps.OrderBy(step => step.Priority)];
    }

    private static void ApplyLtx23Foley(WorkflowGenerator g)
    {
        if (g.FinalLoadedModel?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID)
        {
            throw new SwarmUserErrorException("LTX 2.3 Foley Audio requires an LTX 2.x base model.");
        }
        if (!g.UserInput.TryGet(T2IParamTypes.InitImage, out Image initVideo))
        {
            throw new SwarmUserErrorException("LTX 2.3 Foley Audio requires a video in Init Image.");
        }
        if (g.CurrentVae is null || g.CurrentAudioVae is null)
        {
            throw new SwarmUserErrorException("The selected LTX model did not provide both the video VAE and audio VAE required for Foley generation.");
        }

        int fps = g.UserInput.Get(T2IParamTypes.VideoFPS, 24);
        bool autoReencodeInput = g.UserInput.Get(FoleyAutoReencodeInput, true);
        bool autoLongVideo = g.UserInput.Get(FoleyAutoLongVideo, true);
        int maximumFrames = g.UserInput.Get(FoleyMaximumFrames, 169);
        int windowFrames = g.UserInput.Get(FoleyWindowFrames, 89);
        double windowOverlap = g.UserInput.Get(FoleyWindowOverlap, 1);
        int maximumWindows = g.UserInput.Get(FoleyMaximumWindows, 16);
        int conditioningSize = g.UserInput.Get(FoleyConditioningSize, 576);
        int steps = g.UserInput.Get(T2IParamTypes.Steps, 30);
        long seed = g.UserInput.Get(T2IParamTypes.Seed);
        double audioCfg = g.UserInput.Get(FoleyAudioCFG, 6);
        double videoCfg = g.UserInput.Get(FoleyVideoCFG, 1);
        double stg = g.UserInput.Get(FoleySTGScale, 1);
        double modalityScale = g.UserInput.Get(FoleyModalityScale, 3);

        if (initVideo.Type.MetaType != MediaMetaType.Video)
        {
            throw new SwarmUserErrorException("LTX 2.3 Foley Audio requires a video, not a still image, in Init Image.");
        }

        // Stream FPS conversion and frame limiting before materializing IMAGE tensors. Loading the
        // complete source first can consume tens of GB of system RAM for long, high-FPS videos.
        string sourceVideo = g.CreateNode("SwarmLoadVideoB64", new JObject()
        {
            ["video_base64"] = initVideo.AsBase64
        });
        if (autoLongVideo)
        {
            int longVideoFrameLimit = maximumFrames == 169 ? 4097 : maximumFrames;
            ApplyLtx23FoleyLong(g, sourceVideo, fps, longVideoFrameLimit, windowFrames, windowOverlap,
                maximumWindows, conditioningSize, steps, seed, audioCfg, videoCfg, stg, modalityScale);
            return;
        }
        string preparedVideo = g.CreateNode("SwarmLTXFoleyVideoFrames", new JObject()
        {
            ["video"] = WorkflowGenerator.NodePath(sourceVideo, 0),
            ["maximum_frames"] = maximumFrames,
            ["target_fps"] = fps,
            ["auto_reencode"] = autoReencodeInput
        });
        WGNodeData trimmedVideo = new([preparedVideo, 0], g, WGNodeData.DT_VIDEO, g.CurrentCompat()) { FPS = fps };
        WGNodeData videoLatent = trimmedVideo.EncodeToLatent(g.CurrentVae);

        string emptyAudio = g.CreateNode("LTXVEmptyLatentAudio", new JObject()
        {
            ["frames_number"] = WorkflowGenerator.NodePath(preparedVideo, 1),
            ["frame_rate"] = fps,
            ["batch_size"] = 1,
            ["audio_vae"] = g.CurrentAudioVae.Path
        });
        string avLatent = g.CreateNode("LTXVConcatAVLatent", new JObject()
        {
            ["video_latent"] = videoLatent.Path,
            ["audio_latent"] = WorkflowGenerator.NodePath(emptyAudio, 0)
        });
        string masked = g.CreateNode("LTXVSetAudioVideoMaskByTime", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(avLatent, 0),
            ["positive"] = g.FinalPrompt,
            ["negative"] = g.FinalNegativePrompt,
            ["model"] = g.CurrentModel.Path,
            ["vae"] = g.CurrentVae.Path,
            ["audio_vae"] = g.CurrentAudioVae.Path,
            ["start_time"] = 0.0,
            ["end_time"] = 30.0,
            ["video_fps"] = (double)fps,
            ["mask_video"] = false,
            ["mask_audio"] = true,
            ["mask_init_value_video"] = 0.0,
            ["mask_init_value_audio"] = 0.0,
            ["slope_len"] = 1
        });
        string audioParams = g.CreateNode("GuiderParameters", new JObject()
        {
            ["modality"] = "AUDIO",
            ["cfg"] = audioCfg,
            ["stg"] = stg,
            ["perturb_attn"] = true,
            ["rescale"] = 0.0,
            ["modality_scale"] = modalityScale,
            ["skip_step"] = 0,
            ["cross_attn"] = true
        });
        string videoParams = g.CreateNode("GuiderParameters", new JObject()
        {
            ["modality"] = "VIDEO",
            ["cfg"] = videoCfg,
            ["stg"] = stg,
            ["perturb_attn"] = true,
            ["rescale"] = 0.0,
            ["modality_scale"] = modalityScale,
            ["skip_step"] = 0,
            ["cross_attn"] = true,
            ["parameters"] = WorkflowGenerator.NodePath(audioParams, 0)
        });
        string guider = g.CreateNode("MultimodalGuider", new JObject()
        {
            ["model"] = g.CurrentModel.Path,
            ["positive"] = WorkflowGenerator.NodePath(masked, 0),
            ["negative"] = WorkflowGenerator.NodePath(masked, 1),
            ["parameters"] = WorkflowGenerator.NodePath(videoParams, 0),
            ["skip_blocks"] = "29"
        });
        string scheduler = g.CreateNode("LTXVScheduler", new JObject()
        {
            ["steps"] = steps,
            ["max_shift"] = 2.05,
            ["base_shift"] = 0.95,
            ["stretch"] = true,
            ["terminal"] = 0.1,
            ["latent"] = WorkflowGenerator.NodePath(masked, 2)
        });
        string sampler = g.CreateNode("KSamplerSelect", new JObject() { ["sampler_name"] = "euler" });
        string noise = g.CreateNode("RandomNoise", new JObject() { ["noise_seed"] = seed });
        string sampled = g.CreateNode("SamplerCustomAdvanced", new JObject()
        {
            ["noise"] = WorkflowGenerator.NodePath(noise, 0),
            ["guider"] = WorkflowGenerator.NodePath(guider, 0),
            ["sampler"] = WorkflowGenerator.NodePath(sampler, 0),
            ["sigmas"] = WorkflowGenerator.NodePath(scheduler, 0),
            ["latent_image"] = WorkflowGenerator.NodePath(masked, 2)
        });
        string separated = g.CreateNode("LTXVSeparateAVLatent", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(sampled, 0)
        });
        string decodedAudio = g.CreateNode("LTXVAudioVAEDecode", new JObject()
        {
            ["samples"] = WorkflowGenerator.NodePath(separated, 1),
            ["audio_vae"] = g.CurrentAudioVae.Path
        });

        g.CurrentMedia = trimmedVideo.Duplicate();
        g.CurrentMedia.AttachedAudio = new WGNodeData([decodedAudio, 0], g, WGNodeData.DT_AUDIO, g.CurrentCompat());
        g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, "9");
        g.SkipFurtherSteps = true;
        Logs.Info($"Created LTX 2.3 Foley V2A workflow (up to {maximumFrames} frames at {fps} FPS, auto reencode: {autoReencodeInput}, {steps} steps).");
    }

    private static void ApplyLtx23FoleyLong(WorkflowGenerator g, string sourceVideo, int fps, int maximumFrames,
        int windowFrames, double windowOverlap, int maximumWindows, int conditioningSize, int steps, long seed,
        double audioCfg, double videoCfg, double stg, double modalityScale)
    {
        if (windowFrames % 8 != 1)
        {
            throw new SwarmUserErrorException("LTX 2.3 Foley Window Frames must be one more than a multiple of 8 (for example 57, 89, or 169).");
        }
        string plan = g.CreateNode("SwarmLTXFoleyVideoWindowPlan", new JObject()
        {
            ["video"] = WorkflowGenerator.NodePath(sourceVideo, 0),
            ["target_fps"] = (double)fps,
            ["maximum_frames"] = maximumFrames,
            ["window_frames"] = windowFrames,
            ["overlap_seconds"] = windowOverlap,
            ["max_windows"] = maximumWindows
        });
        string loopOpen = g.CreateNode("LTXFoleyForLoopOpen", new JObject()
        {
            ["remaining"] = WorkflowGenerator.NodePath(plan, 1)
        });
        string selected = g.CreateNode("SwarmLTXFoleyVideoWindowSelect", new JObject()
        {
            ["video"] = WorkflowGenerator.NodePath(sourceVideo, 0),
            ["window_plan"] = WorkflowGenerator.NodePath(plan, 0),
            ["remaining"] = WorkflowGenerator.NodePath(loopOpen, 1),
            ["width"] = conditioningSize,
            ["height"] = conditioningSize
        });
        string conditioning = g.CreateNode("LTXVConditioning", new JObject()
        {
            ["positive"] = g.FinalPrompt,
            ["negative"] = g.FinalNegativePrompt,
            ["frame_rate"] = (double)fps
        });
        string prepared = g.CreateNode("LTXFoleyVideoToAudioLatent", new JObject()
        {
            ["images"] = WorkflowGenerator.NodePath(selected, 0),
            ["positive"] = WorkflowGenerator.NodePath(conditioning, 0),
            ["negative"] = WorkflowGenerator.NodePath(conditioning, 1),
            ["video_vae"] = g.CurrentVae.Path,
            ["audio_vae"] = g.CurrentAudioVae.Path,
            ["frame_rate"] = (double)fps,
            ["width"] = conditioningSize,
            ["height"] = conditioningSize,
            ["frames"] = windowFrames
        });
        string audioParams = g.CreateNode("GuiderParameters", new JObject()
        {
            ["modality"] = "AUDIO",
            ["cfg"] = audioCfg,
            ["stg"] = stg,
            ["perturb_attn"] = true,
            ["rescale"] = 0.0,
            ["modality_scale"] = modalityScale,
            ["skip_step"] = 0,
            ["cross_attn"] = true
        });
        string videoParams = g.CreateNode("GuiderParameters", new JObject()
        {
            ["modality"] = "VIDEO",
            ["cfg"] = videoCfg,
            ["stg"] = stg,
            ["perturb_attn"] = true,
            ["rescale"] = 0.0,
            ["modality_scale"] = modalityScale,
            ["skip_step"] = 0,
            ["cross_attn"] = true,
            ["parameters"] = WorkflowGenerator.NodePath(audioParams, 0)
        });
        string guider = g.CreateNode("MultimodalGuider", new JObject()
        {
            ["model"] = g.CurrentModel.Path,
            ["positive"] = WorkflowGenerator.NodePath(prepared, 0),
            ["negative"] = WorkflowGenerator.NodePath(prepared, 1),
            ["parameters"] = WorkflowGenerator.NodePath(videoParams, 0),
            ["skip_blocks"] = "29"
        });
        string scheduler = g.CreateNode("LTXVScheduler", new JObject()
        {
            ["steps"] = steps,
            ["max_shift"] = 2.05,
            ["base_shift"] = 0.95,
            ["stretch"] = true,
            ["terminal"] = 0.1,
            ["latent"] = WorkflowGenerator.NodePath(prepared, 2)
        });
        string sampler = g.CreateNode("KSamplerSelect", new JObject() { ["sampler_name"] = "euler" });
        string noise = g.CreateNode("RandomNoise", new JObject() { ["noise_seed"] = seed });
        string sampled = g.CreateNode("SamplerCustomAdvanced", new JObject()
        {
            ["noise"] = WorkflowGenerator.NodePath(noise, 0),
            ["guider"] = WorkflowGenerator.NodePath(guider, 0),
            ["sampler"] = WorkflowGenerator.NodePath(sampler, 0),
            ["sigmas"] = WorkflowGenerator.NodePath(scheduler, 0),
            ["latent_image"] = WorkflowGenerator.NodePath(prepared, 2)
        });
        string separated = g.CreateNode("LTXVSeparateAVLatent", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(sampled, 0)
        });
        string decodedAudio = g.CreateNode("LTXFoleyAudioVAEDecode", new JObject()
        {
            ["samples"] = WorkflowGenerator.NodePath(separated, 1),
            ["audio_vae"] = g.CurrentAudioVae.Path
        });
        string windowRecord = g.CreateNode("LTXFoleyWindowAudioSave", new JObject()
        {
            ["audio"] = WorkflowGenerator.NodePath(decodedAudio, 0),
            ["window_info"] = WorkflowGenerator.NodePath(selected, 1),
            ["save_audio"] = false,
            ["filename_prefix"] = "swarm_ltx_foley_window"
        });
        string accumulation = g.CreateNode("LTXFoleyAudioAccumulator", new JObject()
        {
            ["window_record"] = WorkflowGenerator.NodePath(windowRecord, 1),
            ["accumulation"] = WorkflowGenerator.NodePath(loopOpen, 2)
        });
        string loopClose = g.CreateNode("LTXFoleyForLoopClose", new JObject()
        {
            ["flow_control"] = WorkflowGenerator.NodePath(loopOpen, 0),
            ["audio_accumulation"] = WorkflowGenerator.NodePath(accumulation, 0)
        });
        string stitched = g.CreateNode("LTXFoleyAudioStitch", new JObject()
        {
            ["accumulation"] = WorkflowGenerator.NodePath(loopClose, 0),
            ["window_plan"] = WorkflowGenerator.NodePath(plan, 0)
        });
        g.CreateNode("SwarmLTXFoleyMuxVideoAudioWS", new JObject()
        {
            ["video"] = WorkflowGenerator.NodePath(sourceVideo, 0),
            ["audio"] = WorkflowGenerator.NodePath(stitched, 0),
            ["filename_prefix"] = "swarm_ltx_foley_long",
            ["save_output"] = false
        }, "9");
        g.SkipFurtherSteps = true;
        Logs.Info($"Created automatic long LTX 2.3 Foley workflow: up to {maximumFrames} frames at {fps} FPS, "
            + $"{windowFrames}-frame windows, {windowOverlap:0.###}s overlap, max {maximumWindows} windows, {steps} steps each.");
    }

    private static bool ShouldApplyI2VUpscale(WorkflowGenerator g)
    {
        return TryGetLtxv2I2vUpscaleSettings(g, out _, out _, out _, out _);
    }

    private static bool ShouldSkipRefinerForLtxv2I2V(WorkflowGenerator g)
    {
        return TryGetLtxv2I2vUpscaleSettings(g, out _, out _, out _, out _);
    }

    private static bool TryGetLtxv2I2vUpscaleSettings(WorkflowGenerator g, out T2IModel videoModel, out double refineUpscale, out string upscaleMethod, out double refinerControl)
    {
        videoModel = null;
        refineUpscale = 1;
        upscaleMethod = null;
        refinerControl = 0;

        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out videoModel))
            return false;

        if (videoModel.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID)
            return false;

        // Only apply to Image-to-Video
        if (!g.UserInput.TryGet(T2IParamTypes.InitImage, out _))
            return false;

        if (!g.UserInput.TryGet(T2IParamTypes.RefinerUpscale, out refineUpscale) || refineUpscale == 1)
            return false;

        upscaleMethod = g.UserInput.Get(ComfyUIBackendExtension.RefinerUpscaleMethod, "None");
        if (!upscaleMethod.StartsWith("latentmodel-"))
            return false;

        if (!g.UserInput.TryGet(T2IParamTypes.RefinerControl, out refinerControl) || refinerControl <= 0)
            return false;

        return true;
    }

    private static void TryApplyLtxv2I2VUpscale(WorkflowGenerator g, JArray originalInputImage = null)
    {
        if (!TryGetLtxv2I2vUpscaleSettings(g, out T2IModel videoModel, out double refineUpscale, out string upscaleMethod, out double refinerControl))
        {
            Logs.Warning("LTXV2 I2V latent upscale was requested but conditions were not met.");
            return;
        }

        Logs.Info($"Applying LTXV2 I2V latent upscale: {upscaleMethod}, scale={refineUpscale}x, control={refinerControl}");

        JArray imageToScale = originalInputImage ?? g.CurrentMedia?.AsRawImage(g.CurrentVae)?.Path;
        if (imageToScale is null)
        {
            Logs.Error("No input image found for LTXV2 I2V upscale.");
            return;
        }

        int? frames = g.UserInput.TryGet(T2IParamTypes.VideoFrames, out int framesRaw) ? framesRaw : null;
        int? videoFps = g.UserInput.TryGet(T2IParamTypes.VideoFPS, out int fpsRaw) ? fpsRaw : null;
        double? videoCfg = g.UserInput.GetNullable(T2IParamTypes.CFGScale, T2IParamInput.SectionID_Video, false)
            ?? g.UserInput.GetNullable(T2IParamTypes.VideoCFG, T2IParamInput.SectionID_Video);
        int videoSteps = g.UserInput.GetNullable(T2IParamTypes.Steps, T2IParamInput.SectionID_Video, false)
            ?? g.UserInput.Get(T2IParamTypes.VideoSteps, 20, sectionId: T2IParamInput.SectionID_Video);
        string resFormat = g.UserInput.Get(T2IParamTypes.VideoResolution, "Model Preferred");
        long seed = g.UserInput.Get(T2IParamTypes.Seed) + 42;
        string prompt = g.UserInput.Get(T2IParamTypes.Prompt, "");
        string negPrompt = g.UserInput.Get(T2IParamTypes.NegativePrompt, "");

        int width = videoModel.StandardWidth <= 0 ? 1024 : videoModel.StandardWidth;
        int height = videoModel.StandardHeight <= 0 ? 576 : videoModel.StandardHeight;
        int imageWidth = g.UserInput.GetImageWidth();
        int imageHeight = g.UserInput.GetImageHeight();
        int resPrecision = 64;
        if (videoModel.ModelClass?.CompatClass?.ID == "hunyuan-video")
        {
            resPrecision = 16;
        }
        if (resFormat == "Image Aspect, Model Res")
        {
            if (width == 1024 && height == 576 && imageWidth == 1344 && imageHeight == 768)
            {
                width = 1024;
                height = 576;
            }
            else
            {
                (width, height) = Utilities.ResToModelFit(imageWidth, imageHeight, width * height, resPrecision);
            }
        }
        else if (resFormat == "Image")
        {
            width = imageWidth;
            height = imageHeight;
            width = (int)Math.Round(width * refineUpscale);
            height = (int)Math.Round(height * refineUpscale);
        }

        int targetWidth = width;
        int targetHeight = height;
        int baseWidth = (int)Math.Round(targetWidth / refineUpscale);
        int baseHeight = (int)Math.Round(targetHeight / refineUpscale);
        if (baseWidth <= 0 || baseHeight <= 0)
        {
            Logs.Warning($"Invalid base resolution computed ({baseWidth}x{baseHeight}), falling back to target resolution.");
            baseWidth = Math.Max(16, targetWidth);
            baseHeight = Math.Max(16, targetHeight);
        }

        g.IsImageToVideo = true;
        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = g,
            VideoModel = videoModel,
            VideoSwapModel = g.UserInput.Get(T2IParamTypes.VideoSwapModel, null),
            VideoSwapPercent = g.UserInput.Get(T2IParamTypes.VideoSwapPercent, 0.5),
            Frames = frames,
            VideoCFG = videoCfg,
            VideoFPS = videoFps,
            Width = baseWidth,
            Height = baseHeight,
            Prompt = prompt,
            NegativePrompt = negPrompt,
            Steps = videoSteps,
            Seed = seed,
            ContextID = T2IParamInput.SectionID_Video,
            VideoEndFrame = g.UserInput.Get(T2IParamTypes.VideoEndFrame, null)
        };

        string scaledImage = g.CreateNode("ImageScale", new JObject()
        {
            ["image"] = imageToScale,
            ["width"] = targetWidth,
            ["height"] = targetHeight,
            ["upscale_method"] = "lanczos",
            ["crop"] = "disabled"
        });
        JArray scaledImageOut = [scaledImage, 0];
        g.CurrentMedia = new WGNodeData(scaledImageOut, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        WGNodeData srcImage = g.CurrentMedia;

        genInfo.PrepModelAndCond(g);
        genInfo.PrepFullCond(g, srcImage);
        genInfo.VideoCFG ??= genInfo.DefaultCFG;

        string previewType = g.UserInput.Get(ComfyUIBackendExtension.VideoPreviewType, "animate");
        string explicitSampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: genInfo.ContextID, includeBase: false);
        string explicitScheduler = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: genInfo.ContextID, includeBase: false);
        g.CurrentMedia = g.CurrentMedia.AsSamplingLatent(genInfo.Vae, g.CurrentAudioVae);

        string baseSampler = g.CreateKSampler(genInfo.Model.Path, genInfo.PosCond, genInfo.NegCond, g.CurrentMedia.Path,
            genInfo.VideoCFG.Value, genInfo.Steps, genInfo.StartStep, 10000, genInfo.Seed, false, true,
            sigmin: 0.002, sigmax: 1000, previews: previewType,
            defsampler: genInfo.DefaultSampler, defscheduler: genInfo.DefaultScheduler,
            hadSpecialCond: genInfo.HadSpecialCond, explicitSampler: explicitSampler, explicitScheduler: explicitScheduler,
            sectionId: genInfo.ContextID);

        string separated = g.CreateNode("LTXVSeparateAVLatent", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(baseSampler, 0)
        });
        JArray baseVideoLatent = [separated, 0];
        JArray baseAudioLatent = [separated, 1];

        string cropGuides = g.CreateNode("LTXVCropGuides", new JObject()
        {
            ["positive"] = genInfo.PosCond,
            ["negative"] = genInfo.NegCond,
            ["latent"] = baseVideoLatent
        });
        JArray cropPosCond = [cropGuides, 0];
        JArray cropNegCond = [cropGuides, 1];
        JArray cropLatent = [cropGuides, 2];

        string latentModelLoader = g.CreateNode("LatentUpscaleModelLoader", new JObject()
        {
            ["model_name"] = upscaleMethod.After("latentmodel-")
        });
        string latentUpsampler = g.CreateNode("LTXVLatentUpsampler", new JObject()
        {
            ["vae"] = genInfo.Vae.Path,
            ["samples"] = cropLatent,
            ["upscale_model"] = WorkflowGenerator.NodePath(latentModelLoader, 0)
        });

        string preproc = g.CreateNode("LTXVPreprocess", new JObject()
        {
            ["image"] = scaledImageOut,
            ["img_compression"] = 32
        });

        string upscaledImgToVideo = g.CreateNode("LTXVImgToVideoInplace", new JObject()
        {
            ["vae"] = genInfo.Vae.Path,
            ["image"] = WorkflowGenerator.NodePath(preproc, 0),
            ["latent"] = WorkflowGenerator.NodePath(latentUpsampler, 0),
            ["strength"] = 1.0,
            ["bypass"] = false
        });

        string reconcat = g.CreateNode("LTXVConcatAVLatent", new JObject()
        {
            ["video_latent"] = WorkflowGenerator.NodePath(upscaledImgToVideo, 0),
            ["audio_latent"] = baseAudioLatent
        });

        JArray refineModel = genInfo.Model.Path;
        if (g.UserInput.TryGet(ComfyUIBackendExtension.RefinerHyperTile, out int tileSize))
        {
            string hyperTileNode = g.CreateNode("HyperTile", new JObject()
            {
                ["model"] = refineModel,
                ["tile_size"] = tileSize,
                ["swap_size"] = 2,
                ["max_depth"] = 0,
                ["scale_depth"] = false
            });
            refineModel = [hyperTileNode, 0];
        }

        int upscaleSteps = g.UserInput.Get(T2IParamTypes.RefinerSteps, genInfo.Steps, sectionId: T2IParamInput.SectionID_Refiner);
        double upscaleCfg = g.UserInput.Get(T2IParamTypes.RefinerCFGScale, genInfo.VideoCFG.Value, sectionId: T2IParamInput.SectionID_Refiner);
        int upscaleStartStep = (int)Math.Round(upscaleSteps * (1 - refinerControl));
        if (upscaleStartStep < 0)
        {
            upscaleStartStep = 0;
        }
        else if (upscaleStartStep > upscaleSteps)
        {
            upscaleStartStep = upscaleSteps;
        }

        string refinerMethod = g.UserInput.Get(T2IParamTypes.RefinerMethod, "PostApply");
        bool addNoise = refinerMethod != "StepSwapNoisy";
        bool doTiled = g.UserInput.Get(T2IParamTypes.RefinerDoTiling, false);

        string explicitSamplerRef = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false)
            ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSamplerParam, null);
        string explicitSchedulerRef = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false)
            ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSchedulerParam, null);

        string upscaleSampler = g.CreateKSampler(refineModel, cropPosCond, cropNegCond, [reconcat, 0],
            upscaleCfg, upscaleSteps, upscaleStartStep, 10000, genInfo.Seed + 1, false, addNoise,
            sigmin: 0.002, sigmax: 1000, previews: previewType, doTiled: doTiled,
            hadSpecialCond: true, explicitSampler: explicitSamplerRef, explicitScheduler: explicitSchedulerRef,
            sectionId: T2IParamInput.SectionID_Refiner);

        g.CurrentMedia = new WGNodeData([upscaleSampler, 0], g, WGNodeData.DT_LATENT_AUDIOVIDEO, g.CurrentCompat());
        g.CurrentMedia = g.CurrentMedia.AsRawImage(genInfo.Vae);
        int outputFps = genInfo.VideoFPS ?? 24;
        g.CurrentMedia.FPS = outputFps;
        if (g.UserInput.TryGet(T2IParamTypes.TrimVideoStartFrames, out _) || g.UserInput.TryGet(T2IParamTypes.TrimVideoEndFrames, out _))
        {
            string trimNode = g.CreateNode("SwarmTrimFrames", new JObject()
            {
                ["image"] = g.CurrentMedia.Path,
                ["trim_start"] = g.UserInput.Get(T2IParamTypes.TrimVideoStartFrames, 0),
                ["trim_end"] = g.UserInput.Get(T2IParamTypes.TrimVideoEndFrames, 0)
            });
            g.CurrentMedia = g.CurrentMedia.WithPath([trimNode, 0]);
        }

        bool hasExtend = prompt.Contains("<extend:");
        if (!hasExtend && g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMethod, out string vfiMethod)
            && g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, out int mult) && mult > 1)
        {
            if (g.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false))
            {
                g.CurrentMedia.SaveOutput(genInfo.Vae, g.CurrentAudioVae, g.GetStableDynamicID(50000, 0));
            }
            g.CurrentMedia = g.CurrentMedia.WithPath(g.DoInterpolation(g.CurrentMedia.Path, vfiMethod, mult));
            outputFps *= mult;
            g.CurrentMedia.FPS = outputFps;
        }
        string nodeId = hasExtend ? $"{g.GetStableDynamicID(50000, 0)}" : "9";
        g.CurrentMedia.SaveOutput(genInfo.Vae, g.CurrentAudioVae, nodeId);

        RemovePreVideoSaveNode(g);

        g.IsImageToVideo = false;
        Logs.Info("LTXV2 I2V latent upscale completed successfully");
    }

    private static void RemovePreVideoSaveNode(WorkflowGenerator g)
    {
        if (g.Workflow is null || !g.Workflow.TryGetValue("30", out JToken nodeToken))
        {
            return;
        }
        if (nodeToken is not JObject nodeObj)
        {
            return;
        }
        if ($"{nodeObj["class_type"]}" != "SwarmSaveAnimationWS")
        {
            return;
        }
        g.Workflow.Remove("30");
        Logs.Info("Removed pre-video save node 30 for LTXV2 I2V upscale.");
    }
}
