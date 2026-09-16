using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>Feature flag reported by backends that loaded the bundled 'SwarmLtx25AudioToVideo' ComfyUI nodes.</summary>
    public const string A2VFeatureId = "swarm_ltx25_audio_to_video";
    private const string A2VDefaultLatentUpscaler = "ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors";
    private const string A2VDefaultStage1Sigmas = "1.0, 0.99375, 0.9875, 0.98125, 0.975, 0.909375, 0.725, 0.421875, 0.0";
    private const string A2VDefaultStage2Sigmas = "0.85, 0.725, 0.4219, 0.0";
    private const string A2VEndAnchorFullValue = "full (return to the input pose at the end)";
    private static bool _preInitialized;

    private static T2IRegisteredParam<bool> A2VEnabled;
    private static T2IRegisteredParam<double> A2VAudioStartSeconds;
    private static T2IRegisteredParam<double> A2VAudioDurationSeconds;
    private static T2IRegisteredParam<double> A2VMaxDurationSeconds;
    private static T2IRegisteredParam<double> A2VLeadInSilenceSeconds;
    private static T2IRegisteredParam<bool> A2VAutoResolutionFromImage;
    private static T2IRegisteredParam<double> A2VImageStrengthStage1;
    private static T2IRegisteredParam<int> A2VImageCompressionStage1;
    private static T2IRegisteredParam<double> A2VImageStrengthStage2;
    private static T2IRegisteredParam<int> A2VImageCompressionStage2;
    private static T2IRegisteredParam<double> A2VAnchorEverySeconds;
    private static T2IRegisteredParam<double> A2VAnchorStrength;
    private static T2IRegisteredParam<bool> A2VProtectMouth;
    private static T2IRegisteredParam<bool> A2VSnapAnchorsToQuiet;
    private static T2IRegisteredParam<string> A2VEndAnchor;
    private static T2IRegisteredParam<string> A2VStage1Sigmas;
    private static T2IRegisteredParam<string> A2VStage2Sigmas;
    private static T2IRegisteredParam<bool> A2VTorchCompile;
    private static T2IRegisteredParam<string> A2VTorchCompileBackend;

    /// <summary>This extension is installed by the SECourses updater rather than a git clone, so metadata is set directly instead of read from git.</summary>
    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Adds LTXV2 latent upscaling, native LTX 2.3 Foley video-to-audio generation, and LTX 2.5 audio-to-video with an optional image (frozen source audio, identity anchors, optional auto resolution).";
        License = "MIT";
        Version = "0.9.1";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions";
    }

    public override void OnPreInit()
    {
        if (_preInitialized)
        {
            return;
        }
        _preInitialized = true;
        // Bundled ComfyUI nodes for LTX 2.5 Audio To Video (audio prepare, image conditioning, identity anchors, exact-size crop, optional torch compile).
        string customNodeRoot = Path.GetFullPath(Path.Combine(FilePath, "ComfyNodes"));
        if (Directory.Exists(customNodeRoot) && !ComfyUISelfStartBackend.CustomNodePaths.Contains(customNodeRoot))
        {
            ComfyUISelfStartBackend.CustomNodePaths.Add(customNodeRoot);
        }
        ComfyUIBackendExtension.NodeToFeatureMap["SwarmLTX25AudioPrepare"] = A2VFeatureId;
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
        RegisterA2VParameters();
        PatchWorkflowSteps();
        Logs.Info("LTXV2 extension initialized with latent upscaling, LTX 2.3 Foley V2A and LTX 2.5 Audio To Video support.");
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

    private static void RegisterA2VParameters()
    {
        T2IParamGroup group = new("LTX 2.5 Audio To Video", Open: true, OrderPriority: 8.5,
            Description: "Generate a video that follows a source audio file exactly (official LTX-2.5 two-stage distilled audio-to-video), with an optional first-frame image.");
        A2VEnabled = T2IParamTypes.Register<bool>(new("LTX 2.5 Audio To Video", "The video follows the attached audio exactly: the source audio is encoded, frozen and kept in both stages, the video length is derived from the audio, and the untouched source waveform is muxed into the output.\nAttach the audio to the prompt (drag it onto the prompt box or use the attach button) or set Video Audio Input.\nLeave Init Image empty for audio + text to video. Put a still image in Init Image for image + audio to video: the first frame locks to the image and identity anchors keep the same face over long clips.\nWidth/Height are the final video size as usual; set them to match your image's aspect ratio (or turn on 'LTX 2.5 A2V Auto Resolution From Image' to size the video from the image instead).\nRequires an LTX 2.5 model as the main Model. Official recipe: 8 distilled steps at half resolution, 2x latent spatial upscale, 3 refine steps, CFG 1. Sampler = stage 1, Refiner Sampler = stage 2.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", Group: group, OrderPriority: -10, ChangeWeight: 8));
        A2VAudioStartSeconds = T2IParamTypes.Register<double>(new("LTX 2.5 A2V Audio Start Seconds", "Skip this many seconds from the start of the source audio.",
            "0", Min: 0, Max: 100000, Step: 0.01, ViewMax: 60, FeatureFlag: "comfyui", Group: group, OrderPriority: -9, DependNonDefault: A2VEnabled.Type.ID));
        A2VAudioDurationSeconds = T2IParamTypes.Register<double>(new("LTX 2.5 A2V Audio Duration Seconds", "How many seconds of audio to use; the video length follows it. 0 = everything after the start time.",
            "0", Min: 0, Max: 100000, Step: 0.01, ViewMax: 60, FeatureFlag: "comfyui", Group: group, OrderPriority: -8.9, DependNonDefault: A2VEnabled.Type.ID));
        A2VMaxDurationSeconds = T2IParamTypes.Register<double>(new("LTX 2.5 A2V Max Duration Seconds", "Safety cap on the video length in seconds (0 = no cap). LTX-2.5 is tuned for clips up to about 20 seconds; longer clips need much more VRAM and time.",
            "0", Min: 0, Max: 100000, Step: 0.5, ViewMax: 60, FeatureFlag: "comfyui", Group: group, OrderPriority: -8.8, DependNonDefault: A2VEnabled.Type.ID));
        A2VLeadInSilenceSeconds = T2IParamTypes.Register<double>(new("LTX 2.5 A2V Lead In Silence Seconds", "Silence prepended before the audio so the first frame (the input image) does not land mid-word. 0.25 to 0.4 s is typical for talking heads. The muxed audio gets the same lead-in, so sync is preserved.",
            "0.25", Min: 0, Max: 5, Step: 0.05, ViewMax: 2, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -8.7, DependNonDefault: A2VEnabled.Type.ID));
        A2VAutoResolutionFromImage = T2IParamTypes.Register<bool>(new("LTX 2.5 A2V Auto Resolution From Image", "Off (default): Width/Height are the final video size, the same as every other workflow. With an Init Image the image is center-cropped to that aspect.\nOn: with an Init Image the final size is taken from the image's aspect ratio at the LTX-2.5 1080p pixel budget (16:9 = 1920x1080, 9:16 = 1080x1920, 1:1 = 1408x1408, 2:3 = 1152x1728, 4:3 = 1600x1216) and Width/Height are ignored.\nWithout an Init Image, Width/Height are always the final size.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", Group: group, OrderPriority: -8, DependNonDefault: A2VEnabled.Type.ID));
        A2VImageStrengthStage1 = T2IParamTypes.Register<double>(new("LTX 2.5 A2V Base Image Strength", "How hard the first frame is locked to the Init Image in the half-resolution stage (official: 0.7).",
            "0.7", Min: 0, Max: 1, Step: 0.01, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -7, IsAdvanced: true, DependNonDefault: A2VEnabled.Type.ID));
        A2VImageCompressionStage1 = T2IParamTypes.Register<int>(new("LTX 2.5 A2V Base Image Compression", "LTXV Preprocess compression applied to the Init Image before encoding in the half-resolution stage (official: 18). 0 = off.",
            "18", Min: 0, Max: 100, Step: 1, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -6.9, IsAdvanced: true, DependNonDefault: A2VEnabled.Type.ID));
        A2VImageStrengthStage2 = T2IParamTypes.Register<double>(new("LTX 2.5 A2V Refine Image Strength", "How hard the first frame is locked to the Init Image in the refine stage after the 2x upscale (official: 1.0).",
            "1", Min: 0, Max: 1, Step: 0.01, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -6.8, IsAdvanced: true, DependNonDefault: A2VEnabled.Type.ID));
        A2VImageCompressionStage2 = T2IParamTypes.Register<int>(new("LTX 2.5 A2V Refine Image Compression", "LTXV Preprocess compression applied to the Init Image in the refine stage (official: 0 = off).",
            "0", Min: 0, Max: 100, Step: 1, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -6.7, IsAdvanced: true, DependNonDefault: A2VEnabled.Type.ID));
        A2VAnchorEverySeconds = T2IParamTypes.Register<double>(new("LTX 2.5 A2V Anchor Every Seconds", "Identity anchors: re-inject the Init Image as a keyframe guide this often so the person stays the same person over long clips. Anchors are placed only mid-clip, never in the last second. 0 = no anchors. Only used with an Init Image.",
            "4", Min: 0, Max: 60, Step: 0.5, ViewMax: 15, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -6, DependNonDefault: A2VEnabled.Type.ID));
        A2VAnchorStrength = T2IParamTypes.Register<double>(new("LTX 2.5 A2V Anchor Strength", "How hard each mid-clip anchor pulls back to the input face. 0.3 to 0.5 keeps motion natural.",
            "0.4", Min: 0.05, Max: 1, Step: 0.05, FeatureFlag: "comfyui", Group: group, ViewType: ParamViewType.SLIDER, OrderPriority: -5.9, DependNonDefault: A2VEnabled.Type.ID));
        A2VProtectMouth = T2IParamTypes.Register<bool>(new("LTX 2.5 A2V Protect Mouth", "Exclude the mouth and jaw region from the identity anchors so lip sync is not pinned to the input pose. The face is found with insightface when it is installed, otherwise with the bundled Haar cascade.",
            "true", IgnoreIf: "true", FeatureFlag: "comfyui", Group: group, OrderPriority: -5.8, DependNonDefault: A2VEnabled.Type.ID));
        A2VSnapAnchorsToQuiet = T2IParamTypes.Register<bool>(new("LTX 2.5 A2V Snap Anchors To Quiet", "Move each anchor to the quietest moment within 0.75 s, so anchors land in pauses rather than mid-word.",
            "true", IgnoreIf: "true", FeatureFlag: "comfyui", Group: group, OrderPriority: -5.7, DependNonDefault: A2VEnabled.Type.ID));
        A2VEndAnchor = T2IParamTypes.Register<string>(new("LTX 2.5 A2V End Anchor", "Full: a full-strength anchor on the last frame makes the clip end on the input pose. Off: the clip ends freely. Partial end anchors are never used because they ghost the last frame.",
            "off", IgnoreIf: "off", GetValues: (_) => ["off///Off", "full///Full (return to the input pose at the end)"], FeatureFlag: "comfyui", Group: group, OrderPriority: -5.6, DependNonDefault: A2VEnabled.Type.ID));
        A2VStage1Sigmas = T2IParamTypes.Register<string>(new("LTX 2.5 A2V Base Sigmas", "Sigma schedule of the half-resolution stage: comma separated, ending in 0. Default is the official LTX-2.5 distilled 8-step schedule; Steps is ignored.",
            A2VDefaultStage1Sigmas, FeatureFlag: "comfyui", Group: group, OrderPriority: -4, IsAdvanced: true, DependNonDefault: A2VEnabled.Type.ID));
        A2VStage2Sigmas = T2IParamTypes.Register<string>(new("LTX 2.5 A2V Refine Sigmas", "Sigma schedule of the refine stage after the 2x latent upscale: comma separated, ending in 0. Default is the official 3-step refinement; Refiner Steps and Refiner Control Percentage are ignored.",
            A2VDefaultStage2Sigmas, FeatureFlag: "comfyui", Group: group, OrderPriority: -3.9, IsAdvanced: true, DependNonDefault: A2VEnabled.Type.ID));
        A2VTorchCompile = T2IParamTypes.Register<bool>(new("LTX 2.5 A2V Torch Compile", "torch.compile the transformer (same mechanism as the core TorchCompileModel node). The first run compiles for several minutes; later runs with the same resolution and length reuse it. Only worth it for many repeated runs.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", Group: group, OrderPriority: -3, IsAdvanced: true, DependNonDefault: A2VEnabled.Type.ID));
        A2VTorchCompileBackend = T2IParamTypes.Register<string>(new("LTX 2.5 A2V Torch Compile Backend", "torch.compile backend used when LTX 2.5 A2V Torch Compile is enabled.",
            "inductor", IgnoreIf: "inductor", GetValues: (_) => ["inductor", "cudagraphs"], FeatureFlag: "comfyui", Group: group, OrderPriority: -2.9, IsAdvanced: true, DependNonDefault: A2VTorchCompile.Type.ID));
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
                if (g.UserInput.Get(A2VEnabled, false))
                {
                    ApplyLtx25AudioToVideo(g);
                    return;
                }
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

        // The core prompt-audio step (priority -6) feeds a Prompt Audio into LTXVReferenceAudio (ID-LoRA voice identity transfer).
        // Audio To Video freezes the source audio itself, so hide the attachment from that step and put it back afterwards.
        int promptAudioIndex = steps.FindIndex(step => Math.Abs(step.Priority - (-6)) < 0.0001);
        if (promptAudioIndex >= 0)
        {
            var originalPromptAudioAction = steps[promptAudioIndex].Action;
            steps[promptAudioIndex] = new WorkflowGenerator.WorkflowGenStep(g =>
            {
                if (g.UserInput.Get(A2VEnabled, false) && g.UserInput.TryGet(T2IParamTypes.PromptAudios, out List<AudioFile> promptAudios) && promptAudios.Count > 0)
                {
                    g.UserInput.Remove(T2IParamTypes.PromptAudios);
                    try
                    {
                        originalPromptAudioAction(g);
                    }
                    finally
                    {
                        g.UserInput.Set(T2IParamTypes.PromptAudios, promptAudios);
                    }
                    return;
                }
                originalPromptAudioAction(g);
            }, -6);
            Logs.Debug("Wrapped prompt-audio conditioning step (priority -6) for LTX 2.5 Audio To Video");
        }
        else
        {
            Logs.Warning("Could not find the prompt-audio conditioning step to patch for LTX 2.5 Audio To Video");
        }

        WorkflowGenerator.Steps = [.. steps.OrderBy(step => step.Priority)];
    }

    /// <summary>Final video size for an image aspect ratio at the LTX-2.5 1080p pixel budget. Mirrors resolution_from_aspect() in the bundled nodes / ComfyUI preset.</summary>
    private static (int, int) A2VResolutionFromAspect(int width, int height)
    {
        const double pixelBudget = 1920.0 * 1080.0;
        const int multiple = 64;
        if (width <= 0 || height <= 0)
        {
            return (1920, 1080);
        }
        double aspect = (double)width / height;
        if (Math.Abs(aspect / (16.0 / 9.0) - 1.0) < 0.02)
        {
            return (1920, 1080);
        }
        if (Math.Abs(aspect / (9.0 / 16.0) - 1.0) < 0.02)
        {
            return (1080, 1920);
        }
        double idealWidth = Math.Sqrt(pixelBudget * aspect);
        (double, double, int)? bestKey = null;
        int bestWidth = 1920, bestHeight = 1080;
        foreach (int w in new[] { (int)Math.Floor(idealWidth / multiple) * multiple, (int)Math.Ceiling(idealWidth / multiple) * multiple }.Distinct().OrderBy(v => v))
        {
            if (w < multiple)
            {
                continue;
            }
            double idealHeight = w / aspect;
            foreach (int h in new[] { (int)Math.Floor(idealHeight / multiple) * multiple, (int)Math.Ceiling(idealHeight / multiple) * multiple }.Distinct().OrderBy(v => v))
            {
                if (h < multiple)
                {
                    continue;
                }
                double aspectError = Math.Round(Math.Abs(Math.Log(((double)w / h) / aspect)), 6);
                double pixelError = Math.Abs((double)w * h - pixelBudget);
                (double, double, int) key = (aspectError, pixelError, w);
                if (bestKey is null || key.CompareTo(bestKey.Value) < 0)
                {
                    bestKey = key;
                    bestWidth = w;
                    bestHeight = h;
                }
            }
        }
        return (bestWidth, bestHeight);
    }

    /// <summary>Half-resolution stage-1 canvas (32 px latent grid) that covers the target after the 2x latent upscale. Mirrors stage1_size() in the bundled nodes.</summary>
    private static (int, int) A2VStage1Size(int targetWidth, int targetHeight)
    {
        const int spatial = 32;
        int width = (int)Math.Ceiling(targetWidth / 2.0 / spatial) * spatial;
        int height = (int)Math.Ceiling(targetHeight / 2.0 / spatial) * spatial;
        return (Math.Max(spatial * 2, width), Math.Max(spatial * 2, height));
    }

    /// <summary>One SamplerCustomAdvanced stage with a manual sigma schedule and a CFG guider (the official LTX-2.5 distilled graph).</summary>
    private static string A2VSampleStage(WorkflowGenerator g, JArray model, JArray positive, JArray negative, JArray latent, string samplerName, string sigmas, double cfg, long seed)
    {
        string noise = g.CreateNode("RandomNoise", new JObject() { ["noise_seed"] = seed });
        string guider = g.CreateNode("CFGGuider", new JObject()
        {
            ["model"] = model,
            ["positive"] = positive,
            ["negative"] = negative,
            ["cfg"] = cfg
        });
        string sampler = g.CreateNode("KSamplerSelect", new JObject() { ["sampler_name"] = samplerName });
        string schedule = g.CreateNode("ManualSigmas", new JObject() { ["sigmas"] = sigmas });
        return g.CreateNode("SamplerCustomAdvanced", new JObject()
        {
            ["noise"] = WorkflowGenerator.NodePath(noise, 0),
            ["guider"] = WorkflowGenerator.NodePath(guider, 0),
            ["sampler"] = WorkflowGenerator.NodePath(sampler, 0),
            ["sigmas"] = WorkflowGenerator.NodePath(schedule, 0),
            ["latent_image"] = latent
        });
    }

    /// <summary>LTX 2.5 audio-to-video: frozen source audio in both stages, optional first-frame image with identity anchors, 2x latent upscale, exact-size crop, source waveform muxed.
    /// Same graph as the SECourses ComfyUI preset "LTX2.5 Audio To Video - Optional Image - 2-Stage 8+3 Steps".</summary>
    private static void ApplyLtx25AudioToVideo(WorkflowGenerator g)
    {
        if (!g.Features.Contains(A2VFeatureId))
        {
            throw new SwarmUserErrorException("LTX 2.5 Audio To Video needs the bundled 'SwarmLtx25AudioToVideo' ComfyUI nodes, and the backend did not report them. Restart SwarmUI so its ComfyUI backend loads the Ltxv2LatentUpscale extension's ComfyNodes folder (for a remote ComfyUI backend, copy that folder into its custom_nodes).");
        }
        if (g.FinalLoadedModel?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID || !g.IsLTXV25())
        {
            throw new SwarmUserErrorException($"LTX 2.5 Audio To Video requires an LTX 2.5 model as the main Model (the selected model was detected as '{g.FinalLoadedModel?.ModelClass?.ID ?? "unknown"}').");
        }
        if (g.CurrentVae is null || g.CurrentAudioVae is null)
        {
            throw new SwarmUserErrorException("The selected LTX model did not provide both the video VAE and the audio VAE required for audio-to-video.");
        }
        if (g.UserInput.Get(T2IParamTypes.BatchSize, 1) > 1)
        {
            throw new SwarmUserErrorException("LTX 2.5 Audio To Video generates one video per run. Set Batch Size to 1 (use Images for several runs).");
        }
        AudioFile sourceAudio = null;
        if (g.UserInput.TryGet(T2IParamTypes.PromptAudios, out List<AudioFile> promptAudios) && promptAudios.Count > 0)
        {
            sourceAudio = promptAudios[0];
            if (promptAudios.Count > 1)
            {
                Logs.Warning("LTX 2.5 Audio To Video uses only the first attached Prompt Audio.");
            }
        }
        else if (g.UserInput.TryGet(T2IParamTypes.VideoAudioInput, out AudioFile videoAudio))
        {
            sourceAudio = videoAudio;
        }
        if (sourceAudio is null)
        {
            throw new SwarmUserErrorException("LTX 2.5 Audio To Video needs a source audio file: attach it to the prompt (Prompt Audio) or set Video Audio Input.");
        }
        Image initImage = null;
        if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image candidate))
        {
            if (candidate.Type.MetaType == MediaMetaType.Video)
            {
                throw new SwarmUserErrorException("LTX 2.5 Audio To Video takes a still image in Init Image (or no Init Image). Videos are not supported as the optional image.");
            }
            initImage = candidate;
        }

        int fps = g.UserInput.Get(T2IParamTypes.VideoFPS, 24);
        long seed = g.UserInput.Get(T2IParamTypes.Seed);
        double cfg = g.UserInput.Get(T2IParamTypes.CFGScale, 1);
        string stage1Sampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler_ancestral");
        string stage2Sampler = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false)
            ?? g.UserInput.Get(ComfyUIBackendExtension.RefinerSamplerParam, null) ?? "euler";
        double audioStart = g.UserInput.Get(A2VAudioStartSeconds, 0);
        double audioDuration = g.UserInput.Get(A2VAudioDurationSeconds, 0);
        double maxDuration = g.UserInput.Get(A2VMaxDurationSeconds, 0);
        double leadIn = g.UserInput.Get(A2VLeadInSilenceSeconds, 0.25);
        bool autoResolution = g.UserInput.Get(A2VAutoResolutionFromImage, false);
        double imageStrength1 = g.UserInput.Get(A2VImageStrengthStage1, 0.7);
        int imageCompression1 = g.UserInput.Get(A2VImageCompressionStage1, 18);
        double imageStrength2 = g.UserInput.Get(A2VImageStrengthStage2, 1.0);
        int imageCompression2 = g.UserInput.Get(A2VImageCompressionStage2, 0);
        double anchorEvery = g.UserInput.Get(A2VAnchorEverySeconds, 4);
        double anchorStrength = g.UserInput.Get(A2VAnchorStrength, 0.4);
        bool protectMouth = g.UserInput.Get(A2VProtectMouth, true);
        bool snapToQuiet = g.UserInput.Get(A2VSnapAnchorsToQuiet, true);
        string endAnchor = g.UserInput.Get(A2VEndAnchor, "off") == "full" ? A2VEndAnchorFullValue : "off";
        string stage1Sigmas = g.UserInput.Get(A2VStage1Sigmas, A2VDefaultStage1Sigmas);
        string stage2Sigmas = g.UserInput.Get(A2VStage2Sigmas, A2VDefaultStage2Sigmas);
        if (string.IsNullOrWhiteSpace(stage1Sigmas))
        {
            stage1Sigmas = A2VDefaultStage1Sigmas;
        }
        if (string.IsNullOrWhiteSpace(stage2Sigmas))
        {
            stage2Sigmas = A2VDefaultStage2Sigmas;
        }
        bool torchCompile = g.UserInput.Get(A2VTorchCompile, false);
        string torchCompileBackend = g.UserInput.Get(A2VTorchCompileBackend, "inductor");
        string upscaleMethod = g.UserInput.Get(ComfyUIBackendExtension.RefinerUpscaleMethod, "") ?? "";
        string latentUpscaler = upscaleMethod.StartsWith("latentmodel-") ? upscaleMethod.After("latentmodel-") : A2VDefaultLatentUpscaler;

        // Resolution plan: final target -> half-resolution stage 1 on the 32 px grid -> 2x latent upscale -> center crop to the exact target.
        int targetWidth, targetHeight;
        string resolutionSource;
        if (initImage is not null && autoResolution)
        {
            (int imageWidth, int imageHeight) = initImage.GetResolution();
            (targetWidth, targetHeight) = A2VResolutionFromAspect(imageWidth, imageHeight);
            resolutionSource = $"auto from the {imageWidth}x{imageHeight} Init Image";
        }
        else
        {
            targetWidth = g.UserInput.GetImageWidth();
            targetHeight = g.UserInput.GetImageHeight();
            resolutionSource = "Width/Height";
        }
        targetWidth = Math.Clamp(targetWidth - (targetWidth % 2), 128, 8192);
        targetHeight = Math.Clamp(targetHeight - (targetHeight % 2), 128, 8192);
        (int stage1Width, int stage1Height) = A2VStage1Size(targetWidth, targetHeight);
        int upscaledWidth = stage1Width * 2;
        int upscaledHeight = stage1Height * 2;

        // Source audio -> frozen audio latent (noise mask 0), frame count, and the trimmed/padded waveform for muxing.
        string audioLoad = g.CreateAudioLoadNode(sourceAudio, "${promptaudios.0}");
        string audioPrepare = g.CreateNode("SwarmLTX25AudioPrepare", new JObject()
        {
            ["audio"] = WorkflowGenerator.NodePath(audioLoad, 0),
            ["audio_vae"] = g.CurrentAudioVae.Path,
            ["fps"] = (double)fps,
            ["start_seconds"] = audioStart,
            ["duration_seconds"] = audioDuration,
            ["max_duration_seconds"] = maxDuration,
            ["lead_in_silence_seconds"] = leadIn
        });
        JArray frozenAudio = WorkflowGenerator.NodePath(audioPrepare, 0);
        JArray muxAudio = WorkflowGenerator.NodePath(audioPrepare, 1);
        JArray frames = WorkflowGenerator.NodePath(audioPrepare, 2);

        string conditioning = g.CreateNode("LTXVConditioning", new JObject()
        {
            ["positive"] = g.FinalPrompt,
            ["negative"] = g.FinalNegativePrompt,
            ["frame_rate"] = (double)fps
        });
        JArray positive = WorkflowGenerator.NodePath(conditioning, 0);
        JArray negative = WorkflowGenerator.NodePath(conditioning, 1);
        JArray model = g.CurrentModel.Path;
        if (torchCompile)
        {
            string compiled = g.CreateNode("SwarmLTX25TorchCompileOptional", new JObject()
            {
                ["model"] = model,
                ["enabled"] = true,
                ["backend"] = torchCompileBackend
            });
            model = WorkflowGenerator.NodePath(compiled, 0);
        }
        JArray image = null;
        if (initImage is not null)
        {
            // Full-resolution copy: the core init-image node is resized to Width/Height, the A2V nodes crop/scale the image themselves.
            image = g.LoadImage(initImage, "${initimage}", false).Path;
        }

        // Stage 1: half resolution, frames from the audio, optional first-frame lock + identity anchors, frozen audio concatenated.
        string emptyLatent = g.CreateNode("EmptyLTXVLatentVideo", new JObject()
        {
            ["width"] = stage1Width,
            ["height"] = stage1Height,
            ["length"] = frames,
            ["batch_size"] = 1
        });
        JArray stage1Latent = WorkflowGenerator.NodePath(emptyLatent, 0);
        if (image is not null)
        {
            string conditioned = g.CreateNode("SwarmLTX25ImageCondition", new JObject()
            {
                ["vae"] = g.CurrentVae.Path,
                ["latent"] = stage1Latent,
                ["strength"] = imageStrength1,
                ["img_compression"] = imageCompression1,
                ["image"] = image
            });
            stage1Latent = WorkflowGenerator.NodePath(conditioned, 0);
            string anchors = g.CreateNode("SwarmLTX25IdentityAnchors", new JObject()
            {
                ["positive"] = positive,
                ["negative"] = negative,
                ["vae"] = g.CurrentVae.Path,
                ["latent"] = stage1Latent,
                ["frames"] = frames,
                ["fps"] = (double)fps,
                ["anchor_every_seconds"] = anchorEvery,
                ["anchor_strength"] = anchorStrength,
                ["protect_mouth"] = protectMouth,
                ["snap_to_quiet"] = snapToQuiet,
                ["end_anchor"] = endAnchor,
                ["image"] = image,
                ["audio"] = muxAudio
            });
            positive = WorkflowGenerator.NodePath(anchors, 0);
            negative = WorkflowGenerator.NodePath(anchors, 1);
            stage1Latent = WorkflowGenerator.NodePath(anchors, 2);
        }
        string stage1Concat = g.CreateNode("LTXVConcatAVLatent", new JObject()
        {
            ["video_latent"] = stage1Latent,
            ["audio_latent"] = frozenAudio
        });
        string stage1Sampled = A2VSampleStage(g, model, positive, negative, WorkflowGenerator.NodePath(stage1Concat, 0), stage1Sampler, stage1Sigmas, cfg, seed);
        string stage1Separated = g.CreateNode("LTXVSeparateAVLatent", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(stage1Sampled, 0)
        });
        // Guides appended by the anchors are latent frames: crop them before the upscale (a no-op without anchors).
        string cropGuides = g.CreateNode("LTXVCropGuides", new JObject()
        {
            ["positive"] = positive,
            ["negative"] = negative,
            ["latent"] = WorkflowGenerator.NodePath(stage1Separated, 0)
        });
        positive = WorkflowGenerator.NodePath(cropGuides, 0);
        negative = WorkflowGenerator.NodePath(cropGuides, 1);

        // Stage 2: 2x latent spatial upscale, optional full-strength first-frame lock, frozen audio again, short refine schedule.
        string upscalerLoader = g.CreateNode("LatentUpscaleModelLoader", new JObject()
        {
            ["model_name"] = latentUpscaler
        });
        string upsampled = g.CreateNode("LTXVLatentUpsampler", new JObject()
        {
            ["vae"] = g.CurrentVae.Path,
            ["samples"] = WorkflowGenerator.NodePath(cropGuides, 2),
            ["upscale_model"] = WorkflowGenerator.NodePath(upscalerLoader, 0)
        });
        JArray stage2Latent = WorkflowGenerator.NodePath(upsampled, 0);
        if (image is not null)
        {
            string conditioned = g.CreateNode("SwarmLTX25ImageCondition", new JObject()
            {
                ["vae"] = g.CurrentVae.Path,
                ["latent"] = stage2Latent,
                ["strength"] = imageStrength2,
                ["img_compression"] = imageCompression2,
                ["image"] = image
            });
            stage2Latent = WorkflowGenerator.NodePath(conditioned, 0);
        }
        string stage2Concat = g.CreateNode("LTXVConcatAVLatent", new JObject()
        {
            ["video_latent"] = stage2Latent,
            ["audio_latent"] = frozenAudio
        });
        string stage2Sampled = A2VSampleStage(g, model, positive, negative, WorkflowGenerator.NodePath(stage2Concat, 0), stage2Sampler, stage2Sigmas, cfg, seed);
        string stage2Separated = g.CreateNode("LTXVSeparateAVLatent", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(stage2Sampled, 0)
        });

        // Video-only tiled decode (the audio latent is never decoded), crop to the exact target, mux the source waveform.
        int tileSize = g.UserInput.TryGet(T2IParamTypes.VAETileSize, out int tileSizeRaw) && tileSizeRaw > 0 ? tileSizeRaw : 512;
        int tileOverlap = g.UserInput.TryGet(T2IParamTypes.VAETileOverlap, out int tileOverlapRaw) && tileOverlapRaw > 0 ? tileOverlapRaw : 64;
        int temporalSize = g.UserInput.TryGet(T2IParamTypes.VAETemporalTileSize, out int temporalSizeRaw) && temporalSizeRaw > 0 ? temporalSizeRaw : 128;
        int temporalOverlap = g.UserInput.TryGet(T2IParamTypes.VAETemporalTileOverlap, out int temporalOverlapRaw) && temporalOverlapRaw > 0 ? temporalOverlapRaw : 32;
        string decoded = g.CreateNode("VAEDecodeTiled", new JObject()
        {
            ["samples"] = WorkflowGenerator.NodePath(stage2Separated, 0),
            ["vae"] = g.CurrentVae.Path,
            ["tile_size"] = tileSize,
            ["overlap"] = tileOverlap,
            ["temporal_size"] = temporalSize,
            ["temporal_overlap"] = temporalOverlap
        });
        string fitted = g.CreateNode("SwarmImageFitToSize", new JObject()
        {
            ["images"] = WorkflowGenerator.NodePath(decoded, 0),
            ["width"] = targetWidth,
            ["height"] = targetHeight
        });
        int outputFps = fps;
        g.CurrentMedia = new WGNodeData([fitted, 0], g, WGNodeData.DT_VIDEO, g.CurrentCompat())
        {
            FPS = outputFps,
            Width = targetWidth,
            Height = targetHeight,
            AttachedAudio = new WGNodeData(muxAudio, g, WGNodeData.DT_AUDIO, g.CurrentCompat())
        };
        if (g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMethod, out string vfiMethod)
            && g.UserInput.TryGet(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, out int multiplier) && multiplier > 1)
        {
            g.CurrentMedia = g.CurrentMedia.WithPath(g.DoInterpolation(g.CurrentMedia.Path, vfiMethod, multiplier));
            outputFps *= multiplier;
            g.CurrentMedia.FPS = outputFps;
        }
        g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, "9");
        g.SkipFurtherSteps = true;
        string cropNote = upscaledWidth == targetWidth && upscaledHeight == targetHeight ? "no crop" : $"center crop to {targetWidth}x{targetHeight}";
        Logs.Info($"Created LTX 2.5 Audio To Video workflow: {(image is null ? "audio + text" : "image + audio")} to video, target {targetWidth}x{targetHeight} ({resolutionSource}), stage 1 {stage1Width}x{stage1Height} -> 2x latent upscale {upscaledWidth}x{upscaledHeight} -> {cropNote}, {fps} FPS, samplers {stage1Sampler}/{stage2Sampler}, CFG {cfg}, upscaler {latentUpscaler}{(image is null ? "" : $", anchors every {anchorEvery}s at {anchorStrength}")}{(torchCompile ? ", torch.compile " + torchCompileBackend : "")}.");
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
            VideoEndImage = g.UserInput.Get(T2IParamTypes.VideoEndImage, null)
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
