using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace FurkanGozukara.SwarmExtensions.MiniMaxH3References;

/// <summary>Adds complete MiniMax H3 image, video, and audio reference inputs to SwarmUI.</summary>
public class MiniMaxH3ReferencesExtension : Extension
{
    private static bool _initialized;
    private static T2IRegisteredParam<bool> Enabled;
    private static T2IRegisteredParam<string> ReferenceImageSize;
    private static T2IRegisteredParam<double> ReferenceMaxSeconds;
    private static T2IRegisteredParam<string> ReferenceVideoTrims;
    private static List<T2IRegisteredParam<VideoFile>> ReferenceVideos = [];
    private static List<T2IRegisteredParam<AudioFile>> ReferenceAudios = [];
    private static T2IRegisteredParam<bool> SpeedOptimize;
    private static T2IRegisteredParam<double> SpeedCacheThreshold;
    private static T2IRegisteredParam<string> SpeedSparseAttention;
    private static T2IRegisteredParam<bool> AudioOnly;
    private static T2IRegisteredParam<bool> LowVram;
    private static T2IRegisteredParam<bool> LowVramMaxSaving;
    // Video Face Inpainting group (second-pass face refinement, MiniMax H3 today, other video models later)
    private static T2IRegisteredParam<bool> FaceInpaint, FaceTracking, FaceSizeScaling, FaceSizeAwareStitch, FaceGeometryLock;
    private static T2IRegisteredParam<double> FaceDenoise, FaceConfidence, FaceCropFactor, FaceScaleStart, FaceScaleEnd;
    private static T2IRegisteredParam<int> FaceSteps;
    private static T2IRegisteredParam<string> FaceSampler, FaceScheduler, FaceDetector, FaceCanvasMode, FaceFaces;
    // Init Audio group (an optional soundtrack the video must follow; MiniMax H3 today, other audio-video architectures later)
    private static T2IRegisteredParam<AudioFile> InitAudio;
    private static T2IRegisteredParam<bool> InitAudioMatchDuration;

    /// <summary>Feature id advertised when the ComfyUI backend has the SECoursesMiniMaxH3InitAudio node (shipped by FurkanGozukara/FoleyExtension).</summary>
    public const string InitAudioFeatureId = "minimax_h3_init_audio";

    /// <summary>The audio_conditioning mode of SECoursesMiniMaxH3InitAudio: exact soundtrack plus a clean t=1.0 audio guide (same default as the ComfyUI presets).</summary>
    public const string InitAudioConditioning = "lock soundtrack + guide";

    /// <summary>Per-generation record of the init audio nodes, so the output soundtrack swap and the face pass can find them.</summary>
    private sealed class InitAudioState
    {
        public string ConditioningNode;
        public string FramesNode;
    }

    private static readonly ConditionalWeakTable<WorkflowGenerator, InitAudioState> InitAudioStates = new();

    /// <summary>Feature id advertised when the ComfyUI backend has the MiniMaxH3SpeedOptimizer node (shipped by FurkanGozukara/ComfyUI-TeaCache).</summary>
    public const string SpeedFeatureId = "minimax_h3_speed";

    /// <summary>Feature id advertised when the ComfyUI backend has the MiniMaxH3LowVRAM node (shipped by FurkanGozukara/ComfyUI-TeaCache).</summary>
    public const string LowVramFeatureId = "minimax_h3_low_vram";

    /// <summary>Feature id advertised when the ComfyUI backend has the MiniMax H3 face inpaint nodes (shipped by FurkanGozukara/ComfyUI-TeaCache).</summary>
    public const string FaceInpaintFeatureId = "minimax_h3_face_inpaint";

    /// <summary>Tested default YOLO face model of the face pass (the same default as the SECourses ComfyUI presets).</summary>
    public const string DefaultFaceDetector = "yolov9e-face-lindevs.pt";

    /// <summary>Identity-preserving detail clause appended to the face-pass prompt (same text as the ComfyUI presets).</summary>
    public const string FaceRefinementPrompt = "Preserve the exact same identity, expression, head pose, and facial proportions. Resolve natural coherent eyes, skin texture, beard strands, and hair detail. No identity change, beautification, or facial reshaping.";

    /// <summary>This extension is installed by the SECourses updater rather than a git clone, so metadata is set directly instead of read from git.</summary>
    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Adds the complete MiniMax H3 reference workflow, a unified prompt uploader for up to nine images, three videos, and three audio files (with colored @image1 / @video1 / @audio1 prompt tokens and autocomplete), a single-reference trim uploader with an exact start/end window, audio-only generation on a 32x32 video canvas, the NVlabs Sana sol-engine 4x speed optimizations, an exact-math low VRAM mode, and an optional Video Face Inpainting pass (YOLO face tracking of one or several ranked faces, H3 img2img face regeneration with locked audio, geometry-locked and hallucination-guarded stitching), each with a one-click parameter, plus an Init Audio group: an optional soundtrack the generated video follows exactly (lipsync, timing) for text-only, reference, and image-to-video MiniMax H3 generation, and a live token meter beside the prompt (estimated packed-sequence tokens vs the model's documented budget, updated as resolution, duration, references, init image / audio change).";
        License = "MIT";
        Version = "1.13.2";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions";
    }

    public override void OnInit()
    {
        if (_initialized)
        {
            Logs.Info("MiniMax H3 References extension is already initialized.");
            return;
        }
        _initialized = true;

        // The shared MiniMax H3 token model (identical to FoleyExtension/web/js/minimax_h3_tokens.js) must load before the UI script.
        ScriptFiles.Add("Assets/minimax_h3_tokens.js");
        ScriptFiles.Add("Assets/minimax_h3_prompt_references.js");
        StyleSheetFiles.Add("Assets/minimax_h3_prompt_references.css");
        // Advertise these features only when the backend actually has the matching node.
        ComfyUIBackendExtension.NodeToFeatureMap["MiniMaxH3SpeedOptimizer"] = SpeedFeatureId;
        ComfyUIBackendExtension.NodeToFeatureMap["MiniMaxH3LowVRAM"] = LowVramFeatureId;
        ComfyUIBackendExtension.NodeToFeatureMap["MiniMaxH3FaceStitch"] = FaceInpaintFeatureId;
        ComfyUIBackendExtension.NodeToFeatureMap["SECoursesMiniMaxH3InitAudio"] = InitAudioFeatureId;
        RegisterParameters();
        RegisterFaceInpaintParameters();
        RegisterInitAudioParameters();
        // Audio-only H3 intentionally uses a 32px disposable video stream and
        // supports the native H3 frame range beyond SwarmUI's generic video cap.
        // Provide those relaxed types only while Audio Only is enabled, and parse
        // that switch before the dependent parameters.
        T2IAPI.AlwaysTopKeys.Add(AudioOnly.Type.ID);
        T2IParamTypes.FakeTypeProviders.Add(AudioOnlyParamType);
        WorkflowGenerator.AddStep(ApplyReferences, -7.9);
        WorkflowGenerator.AddStep(ApplyAudioOnlyCanvas, -7.8);
        WorkflowGenerator.AddModelGenStep(ApplyAudioOnlyModelRouting, -3.6);
        WorkflowGenerator.AddModelGenStep(ApplySpeedOptimizations, -3.5);
        // after the speed nodes, so the low VRAM patches wrap the already-optimized model
        WorkflowGenerator.AddModelGenStep(ApplyLowVramOptimizations, -3.4);
        // right before the base sampler (-5): condition a MiniMax H3 text/reference generation on the init audio
        WorkflowGenerator.AddStep(ApplyInitAudioTextToVideo, -5.1);
        // the Image To Video pass builds its own conditioning inside CreateImageToVideo, hook it there
        WorkflowGenerator.AltImageToVideoPostHandlers.Add(ApplyInitAudioImageToVideo);
        WorkflowGenerator.AddStep(ExtractAudioOnly, 0.9);
        // after the video decode (1), before segmentation/save (5, 10): refine faces on the decoded frames
        WorkflowGenerator.AddStep(ApplyVideoFaceInpaint, 4.5);
        WorkflowGenerator.AddStep(SaveAudioOnlyLossless, 9.9);
        // after both the base (10) and Image To Video (11) saves: put the user's own audio on the file
        WorkflowGenerator.AddStep(UseInitAudioAsOutputSoundtrack, 11.5);
        WorkflowGenerator.AddStep(ReplaceLegacyBatchImages, 199);
        Logs.Info("MiniMax H3 complete image, video, and audio reference support initialized.");
    }

    private static T2IParamType AudioOnlyParamType(string name, T2IParamInput input)
    {
        if (input is null || !input.Get(AudioOnly, false))
        {
            return null;
        }
        T2IParamType coreType = name switch
        {
            "width" => T2IParamTypes.Width.Type,
            "height" => T2IParamTypes.Height.Type,
            "sidelength" => T2IParamTypes.SideLength.Type,
            "text2videoframes" => T2IParamTypes.Text2VideoFrames.Type,
            _ => null
        };
        if (coreType is null)
        {
            return null;
        }
        if (name == "text2videoframes")
        {
            return coreType with
            {
                Max = 3600,
                ViewMax = 720,
                Description = "MiniMax H3 frame count at 24 FPS. Values are rounded up to the required 17k+5 grid. About 4-15 seconds is quality-tested; longer generation is allowed but experimental."
            };
        }
        return coreType with { Min = 32, ViewMin = 32 };
    }

    private static void RegisterParameters()
    {
        SpeedOptimize = T2IParamTypes.Register<bool>(new(
            "MiniMax H3 4x Speed",
            "Enable the NVlabs Sana sol-engine MiniMax H3 speed optimizations: FirstBlockCache step skipping, Sol-Attn sparse attention, and batched VAE tile decoding.\nEach technique is verified on your GPU at runtime and anything that does not work or does not win there falls back to the normal path automatically, so this is safe to leave enabled on any GPU (RTX 30xx and newer).\nExpect roughly 2x-4x faster video generation with a small quality tradeoff from the cache and sparse attention.",
            "false", IgnoreIf: "false", FeatureFlag: SpeedFeatureId, Group: T2IParamTypes.GroupCore,
            OrderPriority: -17, ChangeWeight: 2));
        SpeedCacheThreshold = T2IParamTypes.Register<double>(new(
            "MiniMax H3 Speed Cache Threshold",
            "FirstBlockCache skip threshold for the MiniMax H3 4x Speed parameter.\n0.08 is the NVlabs sol-engine advertised near-lossless policy.\nHigher skips more aggressively (faster, lower quality), eg 0.15-0.20 for maximum speed.",
            "0.08", Min: 0, Max: 1, Step: 0.01, ViewMax: 0.5, Toggleable: true,
            FeatureFlag: SpeedFeatureId, Group: T2IParamTypes.GroupCore, OrderPriority: -16.9,
            DependNonDefault: SpeedOptimize.Type.ID));
        SpeedSparseAttention = T2IParamTypes.Register<string>(new(
            "MiniMax H3 Speed Sparse Attention",
            "Sol-Attn sparse attention mode for the MiniMax H3 4x Speed parameter.\n'auto' benchmarks against your current attention backend on this GPU and keeps whichever is faster (recommended). 'enabled' forces it, 'disabled' turns it off.",
            "auto", GetValues: _ => ["auto", "enabled", "disabled"], IsAdvanced: true,
            FeatureFlag: SpeedFeatureId, Group: T2IParamTypes.GroupAdvancedSampling, OrderPriority: 16.5));
        // Sits at the bottom of the Core Parameters group (everything else there is negative).
        LowVram = T2IParamTypes.Register<bool>(new(
            "MiniMax H3 Low VRAM",
            "Reduce the peak VRAM of the MiniMax H3 transformer, so a resolution or duration that runs out of memory can still generate.\nYour video does not change. The big attention buffers are released at their last use and the feedforward runs in token chunks; rows are independent and the INT8 quantizer works per row, so the result is bit-for-bit identical, verified end-to-end.\nIt does not cost speed either: the smaller working set keeps more of each matmul in cache, which offsets the extra kernel launches.\nStacks with MiniMax H3 4x Speed. Leave it off if your generations already fit.",
            "false", IgnoreIf: "false", FeatureFlag: LowVramFeatureId, Group: T2IParamTypes.GroupCore,
            OrderPriority: 20, ChangeWeight: 2));
        LowVramMaxSaving = T2IParamTypes.Register<bool>(new(
            "MiniMax H3 Low VRAM Max Saving",
            "Also split MiniMax H3's attention into head groups, taking the peak VRAM reduction to roughly 40% instead of around 15%.\nThis part is not output-preserving. Heads are mathematically independent, but an attention kernel picks its tiling and quantization scales from the tensor it is handed, so a head group can round about one bf16 ulp differently than those heads do inside the whole tensor, and the sampler amplifies that into a different (not worse) video. Whether it happens depends on your attention backend and on the sequence length, so it is offered as a choice rather than guessed at.\nLeave it off to keep the exact same video you get without Low VRAM.",
            "false", IgnoreIf: "false", FeatureFlag: LowVramFeatureId, Group: T2IParamTypes.GroupCore,
            OrderPriority: 20.1, DependNonDefault: LowVram.Type.ID));
        AudioOnly = T2IParamTypes.Register<bool>(new(
            "MiniMax H3 Audio Only",
            "Generate only MiniMax H3's synchronized audio stream. The extension forces the otherwise-discarded video canvas to 32x32, skips video VAE decoding and video saving, and returns one lossless FLAC audio file. Text-only generation and optional image, video, or audio references are supported; in this mode an input video's soundtrack is decoded directly and its frames are never used.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", Group: T2IParamTypes.GroupCore,
            OrderPriority: -15.8, ChangeWeight: 8));

        T2IParamGroup group = new("MiniMax H3 References", Open: true, OrderPriority: 8,
            Description: "Add every image, video, and audio reference directly beside the main prompt. Video soundtracks are paired automatically. Type '@' in the prompt to reference attachments, eg '@image1' or '@video2' (legacy '<Picture 1>' labels still work).");
        Enabled = T2IParamTypes.Register<bool>(new(
            "MiniMax H3 References",
            "Enable the complete MiniMax H3 reference workflow. Select any MiniMax H3 model and supply at least one image, video, or audio reference.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", Group: group, OrderPriority: -10, ChangeWeight: 8));
        ReferenceImageSize = T2IParamTypes.Register<string>(new(
            "MiniMax H3 Reference Image Size",
            "Match limits each image reference to the output pixel area. Max preserves more reference detail and uses more memory and time.",
            "match", GetValues: _ => ["match", "max"], FeatureFlag: "comfyui", Group: group,
            OrderPriority: -9, DependNonDefault: Enabled.Type.ID));
        ReferenceMaxSeconds = T2IParamTypes.Register<double>(new(
            "MiniMax H3 Reference Max Seconds",
            "Maximum duration used from each reference video or audio file. Clean 2-15 second clips are the quality-tested recommendation, but longer references are allowed and may use substantially more memory and time.",
            "15", Min: 1, Max: 3600, Step: 0.5, ViewMax: 60, FeatureFlag: "comfyui", Group: group,
            OrderPriority: -8.9, DependNonDefault: Enabled.Type.ID));
        ReferenceVideoTrims = T2IParamTypes.Register<string>(new(
            "MiniMax H3 Reference Video Trims",
            "Internal slot populated by the 'Add A Reference With Trim' uploader: comma-separated '<video slot>:<start>-<end>' second windows, eg '1:2.5-8'. Videos without an entry start from the file beginning. Trimmed audio references are already cut in the browser and never appear here.",
            "", IgnoreIf: "", FeatureFlag: "comfyui", Group: group,
            OrderPriority: -8.85, DependNonDefault: Enabled.Type.ID));

        ReferenceVideos =
        [
            RegisterVideo("One", -8),
            RegisterVideo("Two", -7),
            RegisterVideo("Three", -6)
        ];
        ReferenceAudios =
        [
            RegisterAudio("One", -5),
            RegisterAudio("Two", -4),
            RegisterAudio("Three", -3)
        ];

        T2IRegisteredParam<VideoFile> RegisterVideo(string ordinal, double priority)
        {
            return T2IParamTypes.Register<VideoFile>(new(
                $"MiniMax H3 Reference Video {ordinal}",
                "Internal slot populated by the MiniMax H3 prompt reference uploader. The selected Reference Max Seconds value is used. Normal video mode resamples frames to 24 FPS and pairs the soundtrack; Audio Only mode extracts only the soundtrack and never decodes the frames.",
                null, FeatureFlag: "comfyui", Group: group, OrderPriority: priority,
                DependNonDefault: Enabled.Type.ID, DoNotPreview: true));
        }

        T2IRegisteredParam<AudioFile> RegisterAudio(string ordinal, double priority)
        {
            return T2IParamTypes.Register<AudioFile>(new(
                $"MiniMax H3 Reference Audio {ordinal}",
                "Internal slot populated by the MiniMax H3 prompt reference uploader. Refer to it with the @audio token shown on its prompt attachment.",
                null, FeatureFlag: "comfyui", Group: group, OrderPriority: priority,
                DependNonDefault: Enabled.Type.ID, DoNotPreview: true));
        }
    }

    /// <summary>"Video Face Inpainting" group: sits between Core Parameters (-50) and Text To Video (-30). The JS side shows it
    /// only while a MiniMax H3 model is selected; the feature flag hides it when the backend lacks the face nodes.</summary>
    private static void RegisterFaceInpaintParameters()
    {
        T2IParamGroup group = new("Video Face Inpainting", Open: true, OrderPriority: -40,
            Description: "Optional second pass that fixes small or blurry faces in generated videos: a YOLO face model tracks the subject's face in every frame, the crops are regenerated at high resolution with the same MiniMax H3 model as img2img (the generated audio stays locked, so speech and lipsync are untouched), the regenerated face is geometry-locked to the source face and stitched back with feathered colour-matched blending. Same defaults as the SECourses ComfyUI presets. Off by default; when off it costs nothing.");
        string dep = null;
        T2IRegisteredParam<T> Reg<T>(string name, string desc, string def, double prio, double min = 0, double max = 0, double step = 1, double viewMax = 0, Func<Session, List<string>> values = null, bool validateValues = true)
        {
            return T2IParamTypes.Register<T>(new(name, desc, def, Min: min, Max: max, Step: step, ViewMax: viewMax, GetValues: values, ValidateValues: validateValues,
                IgnoreIf: def == "false" ? "false" : null, FeatureFlag: FaceInpaintFeatureId, Group: group, OrderPriority: prio,
                DependNonDefault: dep, ChangeWeight: 2));
        }
        FaceInpaint = Reg<bool>("Video Face Inpainting", "Enable the face refinement pass. Requires a MiniMax H3 model and the ComfyUI-TeaCache face nodes on the backend. Adds roughly the cost of a second (face-sized) H3 generation.", "false", -10);
        dep = FaceInpaint.Type.ID;
        FaceDenoise = Reg<double>("Face Inpaint Denoise", "Face-pass denoise. 0.55 is the tested default: enough to add real skin/eye/beard detail while keeping identity. Lower (0.35-0.45) stays closer to the source, higher (0.60+) drifts more.", "0.55", -9, 0, 1, 0.01);
        FaceFaces = Reg<string>("Face Inpaint Faces", "Which face(s) to refine. Faces are ranked by size: 1 = the biggest face in the clip, 2 = the second biggest, 3 = the third, and so on. The rank is the average detected face height across the whole clip (ties go to the face that is on screen longer), so it does not change from frame to frame.\n'1' (default) = the main subject, exactly as before.  '2' = only the second biggest face.  '1,3' = faces 1 and 3.  'all' = every detected face.\nSpaces, ';' and upper/lower case do not matter (' 1, 3 ', 'ALL', 'All'); other text is ignored, and a rank that does not exist in the clip is skipped (a clip with two faces and '1,3' refines face 1 only; if none of the requested faces exist the generation stops with a clear message).\nEvery selected face is refined in its own pass, so time grows with the count. Only the selected faces change; a hallucination guard keeps neighbours' faces and any face H3 invents at their original pixels.", "1", -8.95);
        FaceGeometryLock = Reg<bool>("Face Inpaint Geometry Lock", "Re-align every regenerated face crop onto the source face with dense optical flow before pasting, so eyes/nose/mouth stay exactly where the source video has them. Removes the slight per-frame shaking / tilting the face pass otherwise introduces (about 60% less relative face motion measured at 0.55 denoise) while keeping all regenerated detail. Turn off to paste the raw regenerated crop.", "true", -8.9);
        FaceSizeAwareStitch = Reg<bool>("Face Inpaint Size Aware Stitch", "Use the regenerated face fully for faces up to 60 px tall, fade toward the source between 60-180 px, and keep the original pixels at 180 px and above (a detailed close-up already has more real detail than a VAE round trip). Turn off to keep the regenerated face at full strength regardless of face size.", "true", -8.8);
        FaceSizeScaling = Reg<bool>("Face Inpaint Size Scaled Denoise", "Off (default): every frame uses the constant face-pass denoise. On: the effective denoise is scaled by detected face size, from the start multiplier for faces at/below 60 px to the end multiplier for faces at/above 150 px, so close-ups change less.", "false", -8.7);
        FaceScaleStart = Reg<double>("Face Inpaint Scaling Start Multiplier", "Denoise multiplier for small faces when size scaling is on. 1.00 keeps the full face-pass denoise for distant faces.", "1", -8.6, 0, 1, 0.05);
        FaceScaleEnd = Reg<double>("Face Inpaint Scaling End Multiplier", "Denoise multiplier for large faces when size scaling is on. 0.25 x 0.55 gives about 0.14 effective denoise on close-ups; 0.50 gives 0.275.", "0.25", -8.5, 0, 1, 0.05);
        FaceSteps = Reg<int>("Face Inpaint Steps", "Sampling steps of the face pass. With denoise 0.55, 20 steps runs 11 of them.", "20", -8.4, 1, 100, 1, 50);
        FaceSampler = Reg<string>("Face Inpaint Sampler", "Sampler for the face pass. res_multistep matches the ComfyUI presets.", "res_multistep", -8.3, values: _ => ComfyUIBackendExtension.Samplers);
        FaceScheduler = Reg<string>("Face Inpaint Scheduler", "Scheduler for the face pass. simple matches the ComfyUI presets.", "simple", -8.2, values: _ => ComfyUIBackendExtension.Schedulers);
        // The value list is never validated by the core: the UI sends this parameter even while Video Face Inpainting is off
        // (the core's DependNonDefault cannot drop it for a boolean master), and a user without any YOLO model would otherwise
        // get "Invalid value for param Face Inpaint Detector - '' - must be one of: ``" on every generation. The face pass
        // resolves the real model itself (ResolveFaceDetector) only when it actually runs.
        FaceDetector = Reg<string>("Face Inpaint Detector", $"YOLO face model from the yolov8 models folder. {DefaultFaceDetector} is the tested default (place it in Models/yolov8). If the selected model is missing, another available face model is used automatically.", DefaultFaceDetector, -8.1, values: FaceDetectorChoices, validateValues: false);
        FaceConfidence = Reg<double>("Face Inpaint Detection Confidence", "Minimum face detection confidence. Lower finds more distant/blurry faces at the cost of false positives.", "0.35", -8, 0.05, 0.95, 0.05);
        FaceCropFactor = Reg<double>("Face Inpaint Crop Factor", "Crop side as a multiple of the detected face height. 2.2 puts the face at about 45% of the regenerated crop; bigger gives more context but less magnification.", "2.2", -7.9, 1.2, 8, 0.1, 4);
        FaceCanvasMode = Reg<string>("Face Inpaint Canvas Mode", "auto_capped_768 (recommended): size the H3 face canvas from the largest crop so no frame is downscaled, clamped to 384-768 px. auto_no_downscale: same but uncapped above (expensive on close-ups). manual: fixed 768x768.", "auto_capped_768", -7.8, values: _ => ["auto_capped_768", "auto_no_downscale", "manual"]);
        FaceTracking = Reg<bool>("Face Inpaint Identity Tracking", "Hold one subject through crowds: continuity picks most frames and a face-identity embedding (InsightFace, when installed) resolves ambiguous ones. Off tracks the largest face only.", "true", -7.7);
    }

    /// <summary>Dropdown values of "Face Inpaint Detector": every YOLO model SwarmUI knows, with the tested default always present
    /// (first, and labelled when it is not downloaded yet) so the list is never empty and the UI never sends an empty value.</summary>
    private static List<string> FaceDetectorChoices(Session _)
    {
        List<string> result = [];
        HashSet<string> seen = [];
        foreach (string model in ComfyUIBackendExtension.YoloModels ?? [])
        {
            string raw = model?.Before("///")?.Trim();
            if (!string.IsNullOrEmpty(raw) && seen.Add(raw))
            {
                result.Add(model);
            }
        }
        if (seen.Contains(DefaultFaceDetector))
        {
            int index = result.FindIndex(m => m.Before("///").Trim() == DefaultFaceDetector);
            string preferred = result[index];
            result.RemoveAt(index);
            result.Insert(0, preferred);
        }
        else
        {
            result.Insert(0, $"{DefaultFaceDetector}///{DefaultFaceDetector} (not downloaded yet - place it in Models/yolov8)");
        }
        return result;
    }

    /// <summary>Known YOLO model names (display labels stripped).</summary>
    private static List<string> KnownYoloModels()
    {
        return [.. (ComfyUIBackendExtension.YoloModels ?? []).Select(m => m?.Before("///")?.Trim()).Where(m => !string.IsNullOrEmpty(m)).Distinct()];
    }

    /// <summary>Picks the YOLO face model the face pass sends to the backend. Tolerates an empty, stale, or mistyped selection:
    /// exact / case-insensitive / fuzzy match first, then any other face model SwarmUI knows, otherwise the requested name is
    /// passed through so the ComfyUI node (which also scans the ultralytics folders) can load it or report a clear message.</summary>
    private static string ResolveFaceDetector(WorkflowGenerator g)
    {
        string requested = (g.UserInput.Get(FaceDetector, DefaultFaceDetector) ?? "").Before("///").Trim();
        if (string.IsNullOrEmpty(requested))
        {
            requested = DefaultFaceDetector;
        }
        List<string> known = KnownYoloModels();
        if (known.Count == 0)
        {
            return requested;
        }
        string match = known.FirstOrDefault(m => m == requested)
            ?? known.FirstOrDefault(m => string.Equals(m, requested, StringComparison.OrdinalIgnoreCase))
            ?? known.FirstOrDefault(m => string.Equals(m.Replace('\\', '/'), requested.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            ?? known.FirstOrDefault(m => string.Equals(m.BeforeLast('.'), requested.BeforeLast('.'), StringComparison.OrdinalIgnoreCase))
            ?? T2IParamTypes.GetBestInList(requested, known);
        if (match is not null)
        {
            return match;
        }
        string faceModel = known.FirstOrDefault(m => m.Contains("face", StringComparison.OrdinalIgnoreCase));
        if (faceModel is not null)
        {
            Logs.Warning($"[MiniMaxH3References] Face Inpaint Detector '{requested}' is not available, using the face model '{faceModel}' instead. Place {DefaultFaceDetector} in Models/yolov8 to use the tested default.");
            return faceModel;
        }
        Logs.Warning($"[MiniMaxH3References] Face Inpaint Detector '{requested}' is not in SwarmUI's YOLO model list ({string.Join(", ", known.Take(8))}{(known.Count > 8 ? ", ..." : "")}); passing it to the backend as-is. Place {DefaultFaceDetector} in Models/yolov8 if the face pass reports it missing.");
        return requested;
    }

    /// <summary>"Init Audio" group: sits directly above "Init Image" (-5). One optional soundtrack the generated video must follow.
    /// MiniMax H3 is the first architecture behind it; other audio-video models can be added to the same parameters later.</summary>
    private static void RegisterInitAudioParameters()
    {
        T2IParamGroup group = new("Init Audio", Open: false, OrderPriority: -5.5,
            Description: "Optional init audio: a soundtrack the generated video must follow. MiniMax H3 keeps this audio exactly as the video's audio track and generates the picture to match it (lipsync, action timing, ambience). Works with text-only prompts, MiniMax H3 References, and Init Image / Image To Video with a MiniMax H3 video model. This is not an audio reference: nothing needs to be mentioned in the prompt, just describe who speaks and how.");
        InitAudio = T2IParamTypes.Register<AudioFile>(new(
            "Init Audio",
            "Optional soundtrack the video must follow (wav, mp3, flac, ogg, aac). MiniMax H3 keeps it exactly as the output audio and generates the video to match it: lipsync, action timing, ambience.\nWorks with a text-only prompt, with MiniMax H3 References, and with Init Image + a MiniMax H3 Video Model. Describe who speaks and how in the prompt (eg 'the woman speaks the words we hear, natural lip movements'); the words themselves come from the audio. Do not also attach the same file as an @audio reference.",
            null, FeatureFlag: InitAudioFeatureId, Group: group, OrderPriority: -10, ChangeWeight: 8));
        InitAudioMatchDuration = T2IParamTypes.Register<bool>(new(
            "Init Audio Match Duration",
            "On (default): the video is as long as the init audio, rounded up to MiniMax H3's frame grid (17k+5 frames at 24 FPS); Text2Video Frames / Video Frames are ignored while an init audio is set.\nOff: keep your frame count; longer audio is cut and shorter audio is padded with silence.",
            "true", IgnoreIf: "true", FeatureFlag: InitAudioFeatureId, Group: group, OrderPriority: -9, DependNonDefault: InitAudio.Type.ID));
    }

    private static bool IsMiniMaxH3Model(T2IModel model)
    {
        return model?.ModelClass?.CompatClass?.ID == T2IModelClassSorter.CompatMiniMaxH3.ID;
    }

    /// <summary>Base generation: condition a MiniMax H3 text / reference generation on the init audio (the Image To Video pass has its own hook).</summary>
    private static void ApplyInitAudioTextToVideo(WorkflowGenerator g)
    {
        if (!g.UserInput.TryGet(InitAudio, out AudioFile audio))
        {
            return;
        }
        if (g.UserInput.Get(AudioOnly, false))
        {
            throw new SwarmUserErrorException("Init Audio cannot be combined with MiniMax H3 Audio Only (the output would just be the init audio). Remove one of them.");
        }
        bool videoModelIsH3 = g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel) && IsMiniMaxH3Model(videoModel);
        if (!g.IsMiniMaxH3())
        {
            if (videoModelIsH3)
            {
                return; // applied inside the Image To Video pass
            }
            throw new SwarmUserErrorException("Init Audio currently supports MiniMax H3. Select a MiniMax H3 model (or a MiniMax H3 Video Model in the Image To Video group), or remove the Init Audio.");
        }
        if (g.CurrentMedia is null || g.CurrentMedia.DataType != WGNodeData.DT_LATENT_AUDIOVIDEO)
        {
            if (videoModelIsH3)
            {
                return; // eg Init Image with creativity 0 feeding a MiniMax H3 Video Model: the Image To Video pass applies it
            }
            throw new SwarmUserErrorException("Init Audio with an Init Image needs the Image To Video group: set Init Image Creativity to 0 and choose a MiniMax H3 Video Model there. For text-only generation remove the Init Image.");
        }
        if (g.CurrentAudioVae is null)
        {
            throw new SwarmReadableErrorException("Init Audio needs the MiniMax H3 audio VAE, but none was loaded.");
        }
        int fallbackFrames = WorkflowGenerator.MiniMaxH3AlignFrames(g.UserInput.Get(T2IParamTypes.Text2VideoFrames, 124));
        (JArray positive, JArray latent) = ConditionOnInitAudio(g, audio, g.FinalPrompt, g.CurrentMedia.Path, fallbackFrames);
        g.FinalPrompt = positive;
        g.CurrentMedia = g.CurrentMedia.WithPath(latent);
        if (InitAudioStates.TryGetValue(g, out InitAudioState state) && state.FramesNode is not null)
        {
            g.CurrentMedia.Frames = null; // the frame count is now decided by the audio on the backend
        }
    }

    /// <summary>Image To Video pass with a MiniMax H3 video model: condition it on the init audio before its sampler is created.</summary>
    private static void ApplyInitAudioImageToVideo(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo.Generator;
        if (!g.UserInput.TryGet(InitAudio, out AudioFile audio) || !IsMiniMaxH3Model(genInfo.VideoModel))
        {
            return;
        }
        if (g.CurrentMedia is null || g.CurrentMedia.DataType != WGNodeData.DT_LATENT_AUDIOVIDEO)
        {
            throw new SwarmReadableErrorException($"Init Audio expected the MiniMax H3 audio/video latent of the Image To Video pass but found {g.CurrentMedia?.DataType}.");
        }
        if (g.CurrentAudioVae is null)
        {
            throw new SwarmReadableErrorException("Init Audio needs the MiniMax H3 audio VAE, but none was loaded for the Image To Video pass.");
        }
        int fallbackFrames = WorkflowGenerator.MiniMaxH3AlignFrames(genInfo.Frames ?? 124);
        (JArray positive, JArray latent) = ConditionOnInitAudio(g, audio, genInfo.PosCond, g.CurrentMedia.Path, fallbackFrames);
        genInfo.PosCond = positive;
        g.CurrentMedia = g.CurrentMedia.WithPath(latent);
        if (InitAudioStates.TryGetValue(g, out InitAudioState state) && state.FramesNode is not null)
        {
            g.CurrentMedia.Frames = null;
        }
    }

    /// <summary>Creates the init audio nodes: the audio loader, optionally the frame count that follows the audio (retargeting every H3
    /// length input), and SECoursesMiniMaxH3InitAudio between the given conditioning/latent and the sampler. Returns the new (positive, latent) paths.</summary>
    private static (JArray Positive, JArray Latent) ConditionOnInitAudio(WorkflowGenerator g, AudioFile audio, JArray positive, JArray latent, int fallbackFrames)
    {
        if (!g.Features.Contains(InitAudioFeatureId))
        {
            throw new SwarmUserErrorException("Init Audio needs the SECoursesMiniMaxH3InitAudio node on the ComfyUI backend. Update the FoleyExtension node package.");
        }
        InitAudioState state = InitAudioStates.GetOrCreateValue(g);
        string loaded = g.CreateAudioLoadNode(audio, "${initaudio}");
        bool matchDuration = g.UserInput.Get(InitAudioMatchDuration, true);
        if (matchDuration)
        {
            state.FramesNode = g.CreateNode("SECoursesMiniMaxH3AudioFrames", new JObject()
            {
                ["fallback_frames"] = fallbackFrames,
                ["init_audio"] = WorkflowGenerator.NodePath(loaded, 0),
                ["match_audio"] = true
            });
            int retargeted = 0;
            foreach (string nodeClass in new[] { "EmptyMiniMaxH3LatentAV", "MiniMaxH3ImageToVideo", "MiniMaxH3ReferenceToVideo" })
            {
                g.RunOnNodesOfClass(nodeClass, (_, node) =>
                {
                    if (node["inputs"] is JObject inputs && inputs.ContainsKey("length"))
                    {
                        inputs["length"] = WorkflowGenerator.NodePath(state.FramesNode, 0);
                        retargeted++;
                    }
                });
            }
            Logs.Debug($"Init Audio Match Duration retargeted {retargeted} MiniMax H3 length input(s) to the audio-driven frame count.");
        }
        state.ConditioningNode = g.CreateNode("SECoursesMiniMaxH3InitAudio", new JObject()
        {
            ["positive"] = positive,
            ["latent"] = latent,
            ["audio_vae"] = g.CurrentAudioVae.Path,
            ["init_audio"] = WorkflowGenerator.NodePath(loaded, 0),
            ["audio_conditioning"] = InitAudioConditioning
        });
        Logs.Info($"MiniMax H3 Init Audio attached ({(matchDuration ? "video length follows the audio" : $"{fallbackFrames} frames, audio cut or silence-padded")}, {InitAudioConditioning}).");
        return (WorkflowGenerator.NodePath(state.ConditioningNode, 0), WorkflowGenerator.NodePath(state.ConditioningNode, 1));
    }

    /// <summary>The sampled audio latent is the init audio locked in place; put the user's own audio (normalized, cut to the video) on the file instead of a VAE round trip.</summary>
    private static void UseInitAudioAsOutputSoundtrack(WorkflowGenerator g)
    {
        if (!InitAudioStates.TryGetValue(g, out InitAudioState state) || state.ConditioningNode is null)
        {
            return;
        }
        int replaced = 0;
        g.RunOnNodesOfClass("SwarmSaveAnimationWS", (_, node) =>
        {
            if (node["inputs"] is not JObject inputs || inputs["audio"] is not JArray audioPath || !AudioComesFromInitAudioSampling(g, audioPath, state.ConditioningNode))
            {
                return;
            }
            inputs["audio"] = WorkflowGenerator.NodePath(state.ConditioningNode, 2);
            replaced++;
        });
        if (replaced > 0)
        {
            Logs.Info($"MiniMax H3 Init Audio: {replaced} output video(s) carry the original init audio.");
        }
    }

    /// <summary>True when an audio path is the decoded audio stream of a sampler that ran on the init audio latent
    /// (SwarmSaveAnimationWS.audio &lt;- VAEDecodeAudio &lt;- LTXVSeparateAVLatent &lt;- sampler &lt;- SECoursesMiniMaxH3InitAudio latent).</summary>
    private static bool AudioComesFromInitAudioSampling(WorkflowGenerator g, JArray audioPath, string conditioningNode)
    {
        JObject decode = g.Workflow[$"{audioPath[0]}"] as JObject;
        if ($"{decode?["class_type"]}" != "VAEDecodeAudio" || decode["inputs"]?["samples"] is not JArray samples)
        {
            return false;
        }
        JObject separated = g.Workflow[$"{samples[0]}"] as JObject;
        if ($"{separated?["class_type"]}" != "LTXVSeparateAVLatent" || separated["inputs"]?["av_latent"] is not JArray avLatent)
        {
            return false;
        }
        JObject sampler = g.Workflow[$"{avLatent[0]}"] as JObject;
        return sampler?["inputs"]?["latent_image"] is JArray latentImage && latentImage.Count > 1 && $"{latentImage[0]}" == conditioningNode && $"{latentImage[1]}" == "1";
    }

    /// <summary>Follow a node path upstream through image/latent links until a node of one of the wanted classes is found.</summary>
    private static (JObject Node, JArray Path) FindUpstream(WorkflowGenerator g, JArray path, params string[] classes)
    {
        for (int hop = 0; hop < 12 && path is not null && path.Count >= 1 && g.Workflow[$"{path[0]}"] is JObject node; hop++)
        {
            if (classes.Contains($"{node["class_type"]}"))
            {
                return (node, path);
            }
            JObject inputs = node["inputs"] as JObject;
            path = null;
            foreach (string key in new[] { "images", "image", "samples", "video_latent", "conditioning", "positive", "latent" })
            {
                if (inputs?[key] is JArray next)
                {
                    path = next;
                    break;
                }
            }
        }
        return (null, null);
    }

    /// <summary>Second-pass face refinement on the decoded MiniMax H3 video (mirrors the SECourses ComfyUI preset subgraph).</summary>
    private static void ApplyVideoFaceInpaint(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(FaceInpaint, false) || g.UserInput.Get(AudioOnly, false))
        {
            return;
        }
        if (!g.IsMiniMaxH3())
        {
            throw new SwarmUserErrorException("Video Face Inpainting currently supports MiniMax H3 models only. Select a MiniMax H3 model or turn the parameter off.");
        }
        if (!g.Features.Contains(FaceInpaintFeatureId))
        {
            throw new SwarmUserErrorException("Video Face Inpainting needs the MiniMax H3 face nodes on the ComfyUI backend. Update the ComfyUI-TeaCache node package.");
        }
        if (g.CurrentMedia is null || !g.CurrentMedia.IsRawMedia || g.CurrentMedia.DataType == WGNodeData.DT_AUDIO)
        {
            throw new SwarmReadableErrorException($"Video Face Inpainting expected decoded video frames but received {g.CurrentMedia?.DataType}.");
        }
        // The audio lock needs the sampled AV latent: walk back from the decoded frames to the AV split node.
        (JObject splitNode, JArray _) = FindUpstream(g, g.CurrentMedia.Path, "LTXVSeparateAVLatent");
        JArray sampledLatent = splitNode?["inputs"]?["av_latent"] as JArray;
        if (sampledLatent is null)
        {
            throw new SwarmReadableErrorException("Video Face Inpainting could not find the sampled MiniMax H3 audio/video latent in the workflow.");
        }
        // Prompt text and (optional) reference conditioning of the main pass.
        (JObject condNode, JArray _) = FindUpstream(g, g.FinalPrompt, "MiniMaxH3ReferenceToVideo", "MiniMaxH3ImageToVideo", "CLIPTextEncode");
        string condClass = $"{condNode?["class_type"]}";
        string prompt = condClass == "CLIPTextEncode" ? $"{condNode["inputs"]["text"]}" : condNode?["inputs"]?["prompt"] is JValue p ? $"{p}" : g.UserInput.Get(T2IParamTypes.Prompt, "");
        int frames = Math.Max(5, WorkflowGenerator.MiniMaxH3AlignFrames(g.UserInput.Get(T2IParamTypes.Text2VideoFrames, 124)));
        // with Init Audio Match Duration the main pass length comes from the audio, follow the same node
        JToken length = InitAudioStates.TryGetValue(g, out InitAudioState initAudioState) && initAudioState.FramesNode is not null
            ? WorkflowGenerator.NodePath(initAudioState.FramesNode, 0) : frames;
        JArray images = g.CurrentMedia.Path;
        JArray model = g.CurrentModel.Path, vae = g.CurrentVae.Path;

        string track = g.CreateNode("MiniMaxH3FaceTrackCrop", new JObject()
        {
            ["images"] = images,
            ["detector"] = ResolveFaceDetector(g),
            ["confidence"] = g.UserInput.Get(FaceConfidence, 0.35),
            ["crop_factor"] = g.UserInput.Get(FaceCropFactor, 2.2),
            ["canvas_mode"] = g.UserInput.Get(FaceCanvasMode, "auto_capped_768"),
            ["canvas_width"] = 768,
            ["canvas_height"] = 768,
            ["face_tracking"] = g.UserInput.Get(FaceTracking, true),
            ["smooth_window"] = 21,
            ["size_smooth_window"] = 51,
            ["smooth_method"] = "gaussian",
            ["size_mode"] = "per_frame",
            ["identity_threshold"] = 0.28,
            ["select"] = "largest",
            ["fallback_detector"] = "none",
            ["fallback_head_frac"] = 0.5,
            ["faces"] = g.UserInput.Get(FaceFaces, "1")
        });
        string facePrompt = g.CreateNode("MiniMaxH3FacePromptEnhance", new JObject()
        {
            ["prompt"] = prompt,
            ["refinement_prompt"] = FaceRefinementPrompt
        });
        string cond;
        if (condClass == "MiniMaxH3ReferenceToVideo")
        {
            // same references as the main pass, re-encoded for the face canvas
            JObject refInputs = (JObject)condNode["inputs"].DeepClone();
            refInputs["prompt"] = WorkflowGenerator.NodePath(facePrompt, 0);
            refInputs["width"] = WorkflowGenerator.NodePath(track, 4);
            refInputs["height"] = WorkflowGenerator.NodePath(track, 5);
            refInputs["length"] = length;
            cond = g.CreateNode("MiniMaxH3ReferenceToVideo", refInputs);
        }
        else
        {
            cond = g.CreateNode("MiniMaxH3ImageToVideo", new JObject()
            {
                ["clip"] = g.CurrentTextEnc.Path,
                ["vae"] = vae,
                ["prompt"] = WorkflowGenerator.NodePath(facePrompt, 0),
                ["width"] = WorkflowGenerator.NodePath(track, 4),
                ["height"] = WorkflowGenerator.NodePath(track, 5),
                ["length"] = length
            });
        }
        string inject = g.CreateNode("MiniMaxH3FaceInjectVideoLatent", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(cond, 1),
            ["images"] = WorkflowGenerator.NodePath(track, 0),
            ["vae"] = vae
        });
        string audioLock = g.CreateNode("MiniMaxH3FaceAudioLock", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(inject, 0),
            ["source_latent"] = sampledLatent,
            ["lock_audio"] = true
        });
        string perFrame = g.CreateNode("MiniMaxH3FacePerFrameDenoise", new JObject()
        {
            ["av_latent"] = WorkflowGenerator.NodePath(audioLock, 0),
            ["transform"] = WorkflowGenerator.NodePath(track, 1),
            ["strength_small_face"] = g.UserInput.Get(FaceScaleStart, 1.0),
            ["strength_large_face"] = g.UserInput.Get(FaceScaleEnd, 0.25),
            ["scale_mode"] = "absolute_px",
            ["face_px_small"] = 60.0,
            ["face_px_large"] = 150.0,
            ["gamma"] = 1.0,
            ["smooth_frames"] = 9,
            ["enable_size_scaling"] = g.UserInput.Get(FaceSizeScaling, false)
        });
        string noise = g.CreateNode("RandomNoise", new JObject() { ["noise_seed"] = 7 });
        string sampler = g.CreateNode("MiniMaxH3FaceSamplerSelect", new JObject() { ["sampler_name"] = g.UserInput.Get(FaceSampler, "res_multistep") });
        string sigmas = g.CreateNode("MiniMaxH3FaceScheduler", new JObject()
        {
            ["model"] = model,
            ["scheduler"] = g.UserInput.Get(FaceScheduler, "simple"),
            ["steps"] = g.UserInput.Get(FaceSteps, 20),
            ["denoise"] = g.UserInput.Get(FaceDenoise, 0.55)
        });
        string guider = g.CreateNode("BasicGuider", new JObject()
        {
            ["model"] = model,
            ["conditioning"] = WorkflowGenerator.NodePath(cond, 0)
        });
        string sampled = g.CreateNode("SamplerCustomAdvanced", new JObject()
        {
            ["noise"] = WorkflowGenerator.NodePath(noise, 0),
            ["guider"] = WorkflowGenerator.NodePath(guider, 0),
            ["sampler"] = WorkflowGenerator.NodePath(sampler, 0),
            ["sigmas"] = WorkflowGenerator.NodePath(sigmas, 0),
            ["latent_image"] = WorkflowGenerator.NodePath(perFrame, 0)
        });
        string decoded = g.CreateNode("VAEDecode", new JObject()
        {
            ["samples"] = WorkflowGenerator.NodePath(sampled, 0),
            ["vae"] = vae
        });
        string stitch = g.CreateNode("MiniMaxH3FaceStitch", new JObject()
        {
            ["base_images"] = images,
            ["refined_crops"] = WorkflowGenerator.NodePath(decoded, 0),
            ["transform"] = WorkflowGenerator.NodePath(track, 1),
            ["paste_region"] = "face_only",
            ["mask_dilation"] = 24,
            ["feather"] = 16,
            ["colour_match"] = 1.0,
            ["blend"] = 1.0,
            ["undetected_frames"] = "fade_out",
            ["feather_scales_with_crop"] = false,
            ["size_aware_blend"] = g.UserInput.Get(FaceSizeAwareStitch, true),
            ["full_refine_face_px"] = 60.0,
            ["passthrough_face_px"] = 180.0,
            ["geometry_lock"] = g.UserInput.Get(FaceGeometryLock, true),
            ["geometry_lock_strength"] = 1.0,
            ["suppress_other_faces"] = true
        });
        g.CurrentMedia = g.CurrentMedia.WithPath(WorkflowGenerator.NodePath(stitch, 0));
        Logs.Info($"MiniMax H3 Video Face Inpainting added (faces '{g.UserInput.Get(FaceFaces, "1")}', denoise {g.UserInput.Get(FaceDenoise, 0.55)}, geometry lock {g.UserInput.Get(FaceGeometryLock, true)}, size-aware stitch {g.UserInput.Get(FaceSizeAwareStitch, true)}, size scaling {g.UserInput.Get(FaceSizeScaling, false)}, {(condClass == "MiniMaxH3ReferenceToVideo" ? "reference" : "plain")} conditioning).");
    }

    /// <summary>Use FL2VA for text-only audio and Ref2VA only when this request has attachments.</summary>
    private static void ApplyAudioOnlyModelRouting(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(AudioOnly, false) || !g.IsMiniMaxH3())
        {
            return;
        }
        bool hasReferences = HasAnyReferences(g);
        string desiredVariant = hasReferences ? "ref2va" : "fl2va";
        int matched = 0;
        int changed = 0;
        foreach (string nodeClass in new[] { "UNETLoader", "UnetLoaderGGUF", "UNETLoaderNF4" })
        {
            g.RunOnNodesOfClass(nodeClass, (_, node) =>
            {
                if (node["inputs"] is not JObject inputs || inputs["unet_name"] is not JValue nameValue)
                {
                    return;
                }
                string currentName = $"{nameValue}";
                if (!currentName.Contains("fl2va", StringComparison.OrdinalIgnoreCase)
                    && !currentName.Contains("ref2va", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                matched++;
                string routedName = Regex.Replace(
                    currentName, "fl2va|ref2va", desiredVariant, RegexOptions.IgnoreCase);
                if (routedName != currentName)
                {
                    inputs["unet_name"] = routedName;
                    changed++;
                }
            });
        }
        if (matched == 0)
        {
            Logs.Warning("MiniMax H3 Audio Only could not identify an FL2VA/Ref2VA model filename for automatic routing.");
            return;
        }
        Logs.Info(
            $"MiniMax H3 Audio Only selected {desiredVariant.ToUpperInvariant()} "
            + $"for {(hasReferences ? "reference" : "text-only")} conditioning ({changed} loader change(s)).");
    }

    /// <summary>Model-gen step: wrap the loaded MiniMax H3 model (and video VAE) with the sol-engine speed nodes.</summary>
    private static void ApplySpeedOptimizations(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(SpeedOptimize, false) || !g.IsMiniMaxH3())
        {
            return;
        }
        if (!g.Features.Contains(SpeedFeatureId))
        {
            Logs.Warning("MiniMax H3 4x Speed was requested but the backend does not have the MiniMaxH3SpeedOptimizer node. Update the ComfyUI-TeaCache node package.");
            return;
        }
        double threshold = g.UserInput.Get(SpeedCacheThreshold, 0.08);
        string sparse = g.UserInput.Get(SpeedSparseAttention, "auto");
        string optimizer = g.CreateNode("MiniMaxH3SpeedOptimizer", new JObject()
        {
            ["model"] = g.LoadingModel,
            ["first_block_cache"] = true,
            ["fbc_threshold"] = threshold,
            ["fbc_start_percent"] = 0.15,
            ["fbc_end_percent"] = 0.95,
            ["fbc_max_consecutive"] = 3,
            ["sparse_attention"] = sparse,
            ["sparse_dense_steps_pct"] = 0.20,
            ["sparse_dense_layers"] = 2,
            ["sparse_tau"] = 1.0,
            ["sparse_min_video_rows"] = 4096,
            ["fbc_cache_device"] = "gpu",
            ["verbose"] = true
        });
        g.LoadingModel = [optimizer, 0];
        if (g.LoadingVAE is not null)
        {
            string vaeSpeed = g.CreateNode("MiniMaxH3VAESpeedup", new JObject()
            {
                ["vae"] = g.LoadingVAE,
                ["tile_batch_size"] = 0
            });
            g.LoadingVAE = [vaeSpeed, 0];
        }
        Logs.Info($"MiniMax H3 4x Speed enabled (cache threshold {threshold}, sparse attention {sparse}).");
    }

    /// <summary>Model-gen step: wrap the loaded MiniMax H3 model with the exact-math low VRAM node.</summary>
    private static void ApplyLowVramOptimizations(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(LowVram, false) || !g.IsMiniMaxH3())
        {
            return;
        }
        if (!g.Features.Contains(LowVramFeatureId))
        {
            Logs.Warning("MiniMax H3 Low VRAM was requested but the backend does not have the MiniMaxH3LowVRAM node. Update the ComfyUI-TeaCache node package.");
            return;
        }
        bool exact = !g.UserInput.Get(LowVramMaxSaving, false);
        string lowVram = g.CreateNode("MiniMaxH3LowVRAM", new JObject()
        {
            ["model"] = g.LoadingModel,
            ["enable_low_vram"] = true,
            ["exact_output"] = exact,
            ["attention_head_groups"] = 8,
            ["feedforward_chunks"] = 4,
            ["min_tokens"] = 4096
        });
        g.LoadingModel = [lowVram, 0];
        Logs.Info($"MiniMax H3 Low VRAM enabled ({(exact ? "exact output only" : "maximum saving, attention head grouping allowed to change the result")}).");
    }

    private static void ApplyReferences(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(Enabled, false))
        {
            return;
        }
        if (!g.IsMiniMaxH3())
        {
            throw new SwarmUserErrorException("MiniMax H3 References requires a MiniMax H3 model.");
        }
        if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image _))
        {
            throw new SwarmUserErrorException("MiniMax H3 References uses the unified prompt reference uploader. Remove the Init Image before generating.");
        }
        if (g.CurrentTextEnc is null || g.CurrentVae is null || g.CurrentAudioVae is null)
        {
            throw new SwarmUserErrorException("MiniMax H3 References requires the MiniMax H3 text encoder, video VAE, and audio VAE.");
        }

        List<Image> images = g.UserInput.Get(T2IParamTypes.PromptImages, new List<Image>());
        if (images.Count > 9)
        {
            throw new SwarmUserErrorException("MiniMax H3 supports at most nine image references. Remove extra Prompt Images.");
        }
        List<VideoFile> videos = GetReferenceMediaValues(g, "promptvideos", ReferenceVideos);
        List<AudioFile> audios = GetReferenceMediaValues(g, "promptaudios", ReferenceAudios);
        bool audioOnly = g.UserInput.Get(AudioOnly, false);
        double referenceMaxSeconds = g.UserInput.Get(ReferenceMaxSeconds, 15.0);
        int standaloneAudioCount = audios.Count;
        // The core 'Video Audio Reference' param no longer exists in newer SwarmUI versions,
        // so resolve it by ID at runtime instead of a compile-time field reference. On old
        // SwarmUI this reads the exact same param; on new SwarmUI it is simply absent.
        AudioFile legacyAudio = null;
        if (T2IParamTypes.Types.TryGetValue("videoaudioreference", out T2IParamType legacyAudioType)
            && g.UserInput.TryGetRaw(legacyAudioType, out object legacyAudioValue)
            && legacyAudioValue is AudioFile legacyAudioFile)
        {
            legacyAudio = legacyAudioFile;
        }
        bool hasLegacyAudio = legacyAudio is not null;
        if (hasLegacyAudio)
        {
            audios.Insert(0, legacyAudio);
        }
        if (audios.Count > 3)
        {
            throw new SwarmUserErrorException("MiniMax H3 supports at most three standalone audio references. Remove one reference audio input.");
        }
        if (images.Count + videos.Count + audios.Count == 0)
        {
            if (audioOnly)
            {
                Logs.Info("MiniMax H3 Audio Only has no attachments; using text-only conditioning.");
                return;
            }
            throw new SwarmUserErrorException("MiniMax H3 References needs at least one Prompt Image, reference video, or reference audio file.");
        }
        string prompt = TranslatePromptReferenceTokens(g.UserInput.Get(T2IParamTypes.Prompt, ""),
            images.Count, videos.Count, standaloneAudioCount, videos.Count + (hasLegacyAudio ? 1 : 0), audioOnly);

        int frameCount = WorkflowGenerator.MiniMaxH3AlignFrames(g.UserInput.Get(T2IParamTypes.Text2VideoFrames, 124));
        JObject inputs = new()
        {
            ["clip"] = g.CurrentTextEnc.Path,
            ["vae"] = g.CurrentVae.Path,
            ["audio_vae"] = g.CurrentAudioVae.Path,
            ["prompt"] = prompt,
            ["width"] = g.UserInput.GetImageWidth(),
            ["height"] = g.UserInput.GetImageHeight(),
            ["length"] = frameCount,
            ["ref_image_size"] = g.UserInput.Get(ReferenceImageSize, "match")
        };

        string priorPromptId = $"{g.FinalPrompt[0]}";
        JObject priorPrompt = g.Workflow[priorPromptId] as JObject;
        JObject priorInputs = priorPrompt?["inputs"] as JObject;
        for (int i = 0; i < images.Count; i++)
        {
            string inputName = $"ref_images.ref_image_{i}";
            if (priorInputs is not null && priorInputs.TryGetValue(inputName, out JToken existingImage))
            {
                inputs[inputName] = existingImage.DeepClone();
            }
            else
            {
                WGNodeData image = g.LoadImage(images[i], "${promptimages." + i + "}", false);
                inputs[inputName] = image.Path;
            }
        }

        int audioReferenceIndex = 0;
        Dictionary<int, (double Start, double End)> videoTrims = ParseVideoTrims(g.UserInput.Get(ReferenceVideoTrims, ""));
        for (int i = 0; i < videos.Count; i++)
        {
            // The 'Add A Reference With Trim' uploader sends the untouched video plus a
            // per-slot window; the exact cut happens here on the backend instead of a
            // lossy client-side re-encode. Untrimmed slots keep the old 0-start behavior.
            double trimStart = 0;
            double trimSeconds = referenceMaxSeconds;
            if (videoTrims.TryGetValue(i + 1, out (double Start, double End) trim))
            {
                trimStart = trim.Start;
                trimSeconds = Math.Min(trim.End - trim.Start, referenceMaxSeconds);
            }
            if (audioOnly)
            {
                string soundtrack = g.CreateNode("SECoursesLoadVideoAudioB64", new JObject()
                {
                    ["video_base64"] = videos[i].AsBase64,
                    ["max_seconds"] = trimSeconds,
                    ["start_seconds"] = trimStart
                });
                inputs[$"ref_audios.ref_audio_{audioReferenceIndex++}"] = WorkflowGenerator.NodePath(soundtrack, 0);
                continue;
            }
            string loaded = g.CreateNode("SwarmLoadVideoB64", new JObject()
            {
                ["video_base64"] = videos[i].AsBase64
            });
            string trimmed = g.CreateNode("Video Slice", new JObject()
            {
                ["video"] = WorkflowGenerator.NodePath(loaded, 0),
                ["start_time"] = trimStart,
                ["duration"] = trimSeconds,
                ["strict_duration"] = false
            });
            string components = g.CreateNode("GetVideoComponents", new JObject()
            {
                ["video"] = WorkflowGenerator.NodePath(trimmed, 0)
            });
            string resampled = g.CreateNode("SwarmVideoResampleFPS", new JObject()
            {
                ["images"] = WorkflowGenerator.NodePath(components, 0),
                ["fps_in"] = WorkflowGenerator.NodePath(components, 2),
                ["fps_out"] = 24.0,
                ["method"] = "linear"
            });
            inputs[$"ref_videos.ref_video_{i}"] = WorkflowGenerator.NodePath(resampled, 0);
            inputs[$"ref_video_audios.ref_video_audio_{i}"] = WorkflowGenerator.NodePath(components, 1);
        }

        for (int i = 0; i < audios.Count; i++)
        {
            string loaded = g.CreateAudioLoadNode(audios[i], "${minimaxhreferenceaudio." + i + "}");
            string trimmed = g.CreateNode("SECoursesTrimAudio", new JObject()
            {
                ["audio"] = WorkflowGenerator.NodePath(loaded, 0),
                ["max_seconds"] = referenceMaxSeconds
            });
            int index = audioOnly ? audioReferenceIndex++ : i;
            inputs[$"ref_audios.ref_audio_{index}"] = WorkflowGenerator.NodePath(trimmed, 0);
        }

        List<string> replacedReferenceNodes = [];
        foreach (JProperty property in g.Workflow.Properties())
        {
            if ($"{property.Value["class_type"]}" == "MiniMaxH3ReferenceToVideo")
            {
                replacedReferenceNodes.Add(property.Name);
            }
        }
        foreach (string nodeId in replacedReferenceNodes)
        {
            g.Workflow.Remove(nodeId);
        }
        g.CreateNode("MiniMaxH3ReferenceToVideo", inputs, "6");
        g.FinalPrompt = ["6", 0];
        string videoMode = audioOnly ? "soundtrack-only video" : "full video";
        string trimNote = videoTrims.Count > 0 ? $" {videoTrims.Count} video(s) use a custom trim window." : "";
        Logs.Info($"Created MiniMax H3 reference workflow with {images.Count} image, {videos.Count} {videoMode}, and {audios.Count} standalone audio reference(s); reference maximum {referenceMaxSeconds:0.###} seconds each.{trimNote}");
    }

    /// <summary>Force the disposable video stream to MiniMax H3's smallest valid canvas.</summary>
    private static void ApplyAudioOnlyCanvas(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(AudioOnly, false))
        {
            return;
        }
        if (!g.IsMiniMaxH3())
        {
            throw new SwarmUserErrorException("MiniMax H3 Audio Only requires a MiniMax H3 model.");
        }

        int changed = 0;
        foreach (string nodeClass in new[] { "EmptyMiniMaxH3LatentAV", "MiniMaxH3ImageToVideo", "MiniMaxH3ReferenceToVideo" })
        {
            g.RunOnNodesOfClass(nodeClass, (_, node) =>
            {
                if (node["inputs"] is not JObject inputs)
                {
                    return;
                }
                inputs["width"] = 32;
                inputs["height"] = 32;
                changed++;
            });
        }
        if (changed == 0)
        {
            throw new SwarmReadableErrorException("MiniMax H3 Audio Only could not find an H3 canvas node in the generated workflow.");
        }
        Logs.Info($"MiniMax H3 Audio Only forced {changed} generation canvas node(s) to 32x32.");
    }

    /// <summary>Separate and decode only the audio latent before SwarmUI's normal video decode stage.</summary>
    private static void ExtractAudioOnly(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(AudioOnly, false))
        {
            return;
        }
        if (!g.IsMiniMaxH3() || g.CurrentMedia is null || g.CurrentAudioVae is null)
        {
            throw new SwarmReadableErrorException("MiniMax H3 Audio Only could not access the sampled H3 audio latent and audio VAE.");
        }
        if (g.CurrentMedia.DataType != WGNodeData.DT_LATENT_AUDIOVIDEO)
        {
            throw new SwarmReadableErrorException(
                $"MiniMax H3 Audio Only expected a joint audio/video latent but received {g.CurrentMedia.DataType}.");
        }

        g.CurrentMedia = g.CurrentMedia.DecodeLatents(g.CurrentAudioVae, true, "8");
        Logs.Info("MiniMax H3 Audio Only extracted the audio latent and skipped video decoding.");
    }

    /// <summary>Save the decoded stream as FLAC before SwarmUI's generic MP3 output step.</summary>
    private static void SaveAudioOnlyLossless(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(AudioOnly, false))
        {
            return;
        }
        if (g.CurrentMedia is null || g.CurrentMedia.DataType != WGNodeData.DT_AUDIO)
        {
            throw new SwarmReadableErrorException("MiniMax H3 Audio Only could not access decoded audio for lossless saving.");
        }
        g.CreateNode("SaveAudio", new JObject()
        {
            ["audio"] = g.CurrentMedia.Path,
            ["filename_prefix"] = "audio/SwarmUI_MiniMax_H3_Audio_Only"
        }, "9");
        g.SkipFurtherSteps = true;
        Logs.Info("MiniMax H3 Audio Only will return one lossless FLAC and no video output.");
    }

    private static readonly Regex VideoTrimMatcher = new(
        @"^(\d{1,2}):(\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)$", RegexOptions.Compiled);

    /// <summary>Parses the internal per-slot video trim string, eg "1:2.5-8,3:0-4.25", into
    /// 1-based slot -> (start, end) seconds. Malformed or empty windows are ignored.</summary>
    public static Dictionary<int, (double Start, double End)> ParseVideoTrims(string trims)
    {
        Dictionary<int, (double Start, double End)> result = [];
        foreach (string part in (trims ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match match = VideoTrimMatcher.Match(part);
            if (!match.Success)
            {
                continue;
            }
            double start = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            double end = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (end > start)
            {
                result[int.Parse(match.Groups[1].Value)] = (start, end);
            }
        }
        return result;
    }

    private static readonly Regex PromptReferenceTokenMatcher = new(
        @"(?<![\w@])@(?<type>image|img|picture|pic|video|vid|audio|aud|sound)#?(?<num>\d{1,2})(?![0-9A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Translates prompt-bar "@image1" / "@video2" / "@audio3" reference tokens into the
    /// "&lt;Picture 1&gt;" / "&lt;Video 2&gt;" / "&lt;Audio n&gt;" labels the MiniMax H3 node expects.
    /// Audio labels index video soundtracks first, so standalone audio tokens are offset by the
    /// video count (and by the legacy audio reference, when present). Legacy labels typed directly
    /// in the prompt pass through unchanged. In audio-only mode video tokens map to the corresponding
    /// audio labels because only the soundtrack is conditioned.
    /// Tokens that point at a missing reference (eg '@image3'
    /// with two images attached) are silently omitted, together with one adjacent space, so a stale
    /// token left in the prompt never blocks generation.
    /// </summary>
    public static string TranslatePromptReferenceTokens(string prompt, int imageCount, int videoCount, int standaloneAudioCount, int audioLabelOffset, bool audioOnly = false)
    {
        if (string.IsNullOrEmpty(prompt) || !prompt.Contains('@'))
        {
            return prompt;
        }
        StringBuilder result = new(prompt.Length);
        List<string> omitted = [];
        int last = 0;
        foreach (Match match in PromptReferenceTokenMatcher.Matches(prompt))
        {
            result.Append(prompt, last, match.Index - last);
            last = match.Index + match.Length;
            string type = match.Groups["type"].Value.ToLowerInvariant();
            int number = int.Parse(match.Groups["num"].Value);
            (string label, int count, int offset) = type switch
            {
                "image" or "img" or "picture" or "pic" => ("Picture", imageCount, 0),
                "video" or "vid" => (audioOnly ? "Audio" : "Video", videoCount, 0),
                _ => ("Audio", standaloneAudioCount, audioLabelOffset),
            };
            if (number >= 1 && number <= count)
            {
                result.Append($"<{label} {offset + number}>");
                continue;
            }
            omitted.Add(match.Value);
            if (last < prompt.Length && prompt[last] == ' ')
            {
                last++;
            }
            else if (result.Length > 0 && result[^1] == ' ')
            {
                result.Length--;
            }
        }
        result.Append(prompt, last, prompt.Length - last);
        if (omitted.Count > 0)
        {
            Logs.Info($"MiniMax H3 References is ignoring prompt reference token(s) with no matching attachment: {string.Join(", ", omitted)}");
        }
        return result.ToString();
    }

    /// <summary>
    /// Current SwarmUI still emits ComfyUI's removed BatchImages node when a
    /// MiniMax H3 workflow has both first and last frames. Translate that node
    /// to the current autogrow API without changing SwarmUI or ComfyUI core.
    /// </summary>
    private static void ReplaceLegacyBatchImages(WorkflowGenerator g)
    {
        if (!g.IsMiniMaxH3())
        {
            return;
        }

        int replacements = 0;
        g.RunOnNodesOfClass("BatchImages", (_, node) =>
        {
            JObject oldInputs = node["inputs"] as JObject;
            if (oldInputs is null)
            {
                return;
            }

            JObject newInputs = new();
            int index = 0;
            foreach (JProperty input in oldInputs.Properties())
            {
                if (input.Name.StartsWith("image", StringComparison.Ordinal))
                {
                    newInputs[$"images.image{index++}"] = input.Value.DeepClone();
                }
            }
            if (index == 0)
            {
                return;
            }

            node["class_type"] = "BatchImagesNode";
            node["inputs"] = newInputs;
            replacements++;
        });
        if (replacements > 0)
        {
            Logs.Info($"Updated {replacements} MiniMax H3 image batch node(s) for the current ComfyUI API.");
        }
    }

    private static bool HasAnyReferences(WorkflowGenerator g)
    {
        if (g.UserInput.Get(T2IParamTypes.PromptImages, new List<Image>()).Count > 0)
        {
            return true;
        }
        if (HasPromptMediaValues<VideoFile>(g, "promptvideos") || HasPromptMediaValues<AudioFile>(g, "promptaudios"))
        {
            return true;
        }
        foreach (T2IRegisteredParam<VideoFile> parameter in ReferenceVideos)
        {
            if (g.UserInput.TryGet(parameter, out VideoFile _))
            {
                return true;
            }
        }
        foreach (T2IRegisteredParam<AudioFile> parameter in ReferenceAudios)
        {
            if (g.UserInput.TryGet(parameter, out AudioFile _))
            {
                return true;
            }
        }
        return T2IParamTypes.Types.TryGetValue("videoaudioreference", out T2IParamType legacyAudioType)
            && g.UserInput.TryGetRaw(legacyAudioType, out object legacyAudioValue)
            && legacyAudioValue is AudioFile;
    }

    /// <summary>Returns whether a current SwarmUI prompt-media list contains at least one item.
    /// The runtime lookup keeps this extension compatible with SwarmUI builds from before prompt video/audio lists existed.</summary>
    private static bool HasPromptMediaValues<T>(WorkflowGenerator g, string paramId)
    {
        return T2IParamTypes.Types.TryGetValue(paramId, out T2IParamType type)
            && g.UserInput.TryGetRaw(type, out object rawValue)
            && rawValue is List<T> values
            && values.Count > 0;
    }

    /// <summary>Uses the extension's ordered hidden slots when populated, then falls back to SwarmUI's unified prompt-media list.</summary>
    private static List<T> GetReferenceMediaValues<T>(WorkflowGenerator g, string paramId, List<T2IRegisteredParam<T>> slotParameters)
    {
        List<T> slotValues = GetValues(g, slotParameters);
        if (slotValues.Count > 0)
        {
            return slotValues;
        }
        if (T2IParamTypes.Types.TryGetValue(paramId, out T2IParamType type)
            && g.UserInput.TryGetRaw(type, out object rawValue)
            && rawValue is List<T> values
            && values.Count > 0)
        {
            return values;
        }
        return [];
    }

    private static List<T> GetValues<T>(WorkflowGenerator g, List<T2IRegisteredParam<T>> parameters)
    {
        List<T> values = [];
        foreach (T2IRegisteredParam<T> parameter in parameters)
        {
            if (g.UserInput.TryGet(parameter, out T value))
            {
                values.Add(value);
            }
        }
        return values;
    }
}
