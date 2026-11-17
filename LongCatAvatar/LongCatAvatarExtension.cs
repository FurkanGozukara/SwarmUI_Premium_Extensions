using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.LongCatAvatar;

/// <summary>LongCat-Video-Avatar 1.6 in SwarmUI: the attached audio, the optional Init Image and the prompt become a long
/// lip-synced talking or singing video through the SECoursesAudioTools LongCat Avatar sampler, which runs the whole official
/// recipe (8 distilled DMD/OPSD steps, 93-frame segments that continue from each other) and saves the MP4 with the original audio.</summary>
public class LongCatAvatarExtension : Extension
{
    private const string Feature = "secourses_longcat_avatar";
    private const string ModelMarker = "longcat_video_avatar";
    private const string WhisperDefault = "LongCat_Whisper_Large_V3_fp16.safetensors";
    private const string DefaultScene = "The person in the image talks to the camera with natural facial expressions, clear lip movements, subtle head movements and natural hand gestures.";
    // The official negative prompt (SECoursesAudioTools longcat_avatar_nodes.NEGATIVE); only used by the 50-step base mode.
    private const string OfficialNegative = "Close-up, Bright tones, overexposed, static, blurred details, subtitles, style, works, paintings, images, static, overall gray, worst quality, low quality, JPEG compression residue, ugly, incomplete, extra fingers, poorly drawn hands, poorly drawn faces, deformed, disfigured, misshapen limbs, fused fingers, still picture, messy background, three legs, many people in the background, walking backwards";
    private static readonly string[] SizeModes = ["480p bucket (official)", "720p bucket (official)", "custom width x height"];
    private static readonly string[] EncodingPresets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    private static T2IRegisteredParam<bool> Enabled, MouthEnhancement, Distilled;
    private static T2IRegisteredParam<T2IModel> TextEncoder, Vae, DmdLora, OpsdLora;
    private static T2IRegisteredParam<string> Whisper, WeightDtype, PromptList;
    private static readonly List<T2IParamType> AllParams = [];
    // Sampler inputs keyed by the Python input names of SELongCatAvatarSampler, so they map 1:1 onto the node.
    private static readonly Dictionary<string, T2IRegisteredParam<bool>> Toggles = [];
    private static readonly Dictionary<string, T2IRegisteredParam<int>> Integers = [];
    private static readonly Dictionary<string, T2IRegisteredParam<double>> Numbers = [];
    private static readonly Dictionary<string, T2IRegisteredParam<string>> Strings = [];

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "LongCat-Video-Avatar 1.6: attached audio + optional Init Image + prompt to a long lip-synced talking or singing video, with the official 8-step recipe, mouth enhancement and prompt batches.";
        Version = "1.0.0";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/LongCatAvatar";
    }

    public override void OnPreInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["SELongCatAvatarSampler"] = Feature;
    }

    public override void OnInit()
    {
        T2IParamGroup group = new("LongCat Avatar", Open: true, OrderPriority: 8.5,
            Description: "Select a LongCat Video Avatar model, attach the voice or song to the prompt, and optionally set an Init Image as the person. The video is as long as the audio. Prompt, Negative Prompt, Seed, Width and Height (custom size) and ordinary LoRAs apply; Steps, CFG, Video FPS and refiner controls do not.");
        Enabled = T2IParamTypes.Register<bool>(new("LongCat Avatar Enabled", "Use the dedicated LongCat Video Avatar workflow (audio + optional image + text to a lip-synced video). Requires the updated SECoursesAudioTools node pack on the ComfyUI backend. Applies only when the main model is a LongCat Video Avatar file. No model downloads are performed.",
            "false", IgnoreIf: "false", Group: group, FeatureFlag: Feature, OrderPriority: -20));
        AllParams.Add(Enabled.Type);
        Text("size_mode", "Size Mode", SizeModes[0], "Official buckets pick the size nearest to the Init Image aspect (or the Width x Height aspect without an image). Custom uses Width x Height, rounded to 16; the image is center-cropped to it.", SizeModes);
        Distilled = Toggle("distilled_8_steps", "Distilled 8 Steps", true, "Official LongCat Avatar 1.6 recipe: 8 distilled steps with the DMD + OPSD LoRAs, guidance 1. Off runs the base model with Base Steps and the guidance values (about 20x slower).");
        Integer("mix_steps", "Mix Steps", 4, 0, 8, "Official 4: steps 1-4 use the DMD LoRA (x Mix Alpha), steps 5-8 the OPSD LoRA.", 1, true);
        Number("mix_alpha", "Mix Alpha", 1, 0, 1, "DMD weight during the mixed steps (official 1.0). More DMD = steadier structure.", 0.05, true);
        Toggle("vocal_separation", "Vocal Separation", true, "Official Kim_Vocal_2 vocal separation before the Whisper features, so music and noise do not move the mouth. The saved video keeps the original audio.");
        Number("audio_start_seconds", "Audio Start Seconds", 0, 0, 100000, "Skip this much of the attached audio.");
        Number("duration_seconds", "Duration Seconds", 0, 0, 100000, "0 = the whole audio, otherwise this many seconds from Audio Start. The video length follows the audio.");
        Toggle("torch_compile", "Torch Compile", false, "Compiles each transformer block: the first run is slower, later runs are faster. Needs Triton on the backend.");
        Toggle("condition_cache", "Condition Cache", true, "Computes the reference and previous-frame tokens once per LoRA phase instead of every step. Same result, faster long videos.", true);
        Text("attention", "Attention", "auto", "auto uses ComfyUI's attention (SageAttention with our launchers). sage forces SageAttention if installed; pytorch forces PyTorch attention.", ["auto", "sage", "pytorch"], true);
        MouthEnhancement = Toggle("mouth_enhancement", "Mouth Enhancement", false, "Aligned CodeFormer mouth-only restoration of every frame before export. Needs facerestore_models/codeformer.pth and insightface/models/buffalo_l/det_10g.onnx on the backend.");
        Number("mouth_fidelity", "Mouth Fidelity", .9, 0, 1, "CodeFormer fidelity (0.9 recommended).", 0.01, false, MouthEnhancement);
        Number("mouth_blend", "Mouth Blend", .7, 0, 1, "Feathered mouth-only blend; 0 leaves the frames unchanged.", 0.01, false, MouthEnhancement);
        Text("mouth_model", "Mouth Model", "codeformer.pth", "Existing checkpoint under models/facerestore_models. No automatic download.", null, true, MouthEnhancement);
        Text("mouth_detector", "Mouth Detector", "models/buffalo_l/det_10g.onnx", "Existing SCRFD detector under models/insightface. No automatic download.", null, true, MouthEnhancement);
        Toggle("save_original_too", "Save Original Too", false, "With Mouth Enhancement on, also saves the video without it next to the result in the ComfyUI output folder (for comparisons).", false, MouthEnhancement);
        Integer("steps", "Base Steps", 50, 4, 100, "Only with Distilled 8 Steps off.", 1, false, Distilled);
        Number("text_guidance", "Text Guidance", 4, 1, 10, "Only with Distilled 8 Steps off (official 4).", 0.1, false, Distilled);
        Number("audio_guidance", "Audio Guidance", 4, 1, 10, "Only with Distilled 8 Steps off (official 4). Higher may exaggerate the mouth.", 0.1, false, Distilled);
        Integer("frames_per_segment", "Frames Per Segment", 93, 33, 189, "Official 93 frames (3.72 s) per segment. Must be 4n+1.", 4, true);
        Integer("context_frames", "Context Frames", 13, 5, 45, "Official 13 frames carried into the next segment. Must be 4n+1 and fewer than the segment frames.", 4, true);
        Integer("ref_img_index", "Reference Index", 10, -64, 64, "Official 10: time position of the identity reference in later segments.", 1, true);
        Integer("mask_frame_range", "Mask Frame Range", 0, 0, 16, "Official 0. Above 0 stops frames near the reference position from copying it (fewer repeated gestures).", 1, true);
        Text("live_preview", "Live Preview", "latent", "Live previews while sampling: latent (fast Wan latent preview of the newest frame), auto, or off.", ["auto", "latent", "off"], true);
        Integer("crf", "CRF", 17, 0, 51, "H.264 quality; lower means larger files and higher quality.", 1, true);
        Text("encoding_preset", "Encoding Preset", "fast", "H.264 encoding speed/compression tradeoff.", EncodingPresets, true);
        WeightDtype = T2IParamTypes.Register<string>(new("LongCat Avatar Weight Dtype", "Compute type for the BF16 model file. Quantized files (INT8 ConvRot) always use their stored format.",
            "default", GetValues: _ => ["default", "bf16", "fp16"], Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, IsAdvanced: true, OrderPriority: 40));
        AllParams.Add(WeightDtype.Type);
        TextEncoder = Model("LongCat Avatar Text Encoder", "UMT5-XXL text encoder (the SwarmUI default name umt5_xxl_fp8_e4m3fn_scaled; our downloader saves the FP16 encoder under that name).", "umt5_xxl_fp8_e4m3fn_scaled", "Clip", 41);
        Vae = Model("LongCat Avatar VAE", "Wan 2.1 VAE.", "Wan/wan_2.1_vae", "VAE", 42);
        Whisper = T2IParamTypes.Register<string>(new("LongCat Avatar Whisper Model", "Whisper large-v3 encoder file under models/audio_encoders, for the audio features. Existing local model file. No automatic download.",
            WhisperDefault, GetValues: WhisperFiles, ValidateValues: false, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 43));
        AllParams.Add(Whisper.Type);
        DmdLora = Model("LongCat Avatar DMD LoRA", "Official DMD LoRA of LongCat Avatar 1.6. Runs the first Mix Steps of the distilled recipe. Not a normal LoRA: keep it out of the LoRA list.", "LongCat_Video_Avatar_1.6_DMD_LoRA", "LoRA", 44);
        OpsdLora = Model("LongCat Avatar OPSD LoRA", "Official OPSD LoRA of LongCat Avatar 1.6. Runs the remaining steps of the distilled recipe. Not a normal LoRA: keep it out of the LoRA list.", "LongCat_Video_Avatar_1.6_OPSD_LoRA", "LoRA", 45);
        PromptList = T2IParamTypes.Register<string>(new("LongCat Avatar Prompt List", "Extra prompts for Generate prompt batch. Add prompts or import .txt files or a folder; each file is one complete prompt. The main prompt runs first, then the list, one video per prompt, with the same captured settings, audio and Init Image.",
            "[]", ViewType: ParamViewType.BIG, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 60, IntentionalUnused: true));
        AllParams.Add(PromptList.Type);
        T2IParamInput.SpecialParameterHandlers.Add(input =>
        {
            // Presets only change their own keys: a leftover switch must never affect another model, and options sent
            // while the extension is off must neither require its backend node nor land in the metadata.
            if (!input.Get(Enabled, false) || !IsLongCatModel(input.Get(T2IParamTypes.Model)))
            {
                foreach (T2IParamType param in AllParams)
                {
                    input.Remove(param);
                }
                input.RequiredFlags.Remove(Feature);
                return;
            }
            // Swarm's preliminary loader does not copy extension parameters and does not know this architecture;
            // this graph loads the model and its companions itself.
            input.Set(T2IParamTypes.NoLoadModels, true);
        });
        // Before the normal model loader (-15): it would load the checkpoint as an unknown architecture with default companions.
        WorkflowGenerator.AddStep(Generate, -16);
        ScriptFiles.Add("Assets/longcat_avatar.js");
        StyleSheetFiles.Add("Assets/longcat_avatar.css");

        T2IRegisteredParam<T2IModel> Model(string name, string help, string value, string subtype, double order)
        {
            T2IRegisteredParam<T2IModel> param = T2IParamTypes.Register<T2IModel>(new(name, help + " Existing local model file. No automatic download.", value, Subtype: subtype,
                GetValues: subtype == "LoRA" ? s => T2IParamTypes.CleanModelList(Program.T2IModelSets["LoRA"].ListModelNamesFor(s)) : null,
                Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: order));
            AllParams.Add(param.Type);
            return param;
        }
        T2IRegisteredParam<bool> Toggle(string key, string name, bool value, string help, bool advanced = false, T2IRegisteredParam<bool> master = null)
        {
            T2IRegisteredParam<bool> param = T2IParamTypes.Register<bool>(new("LongCat Avatar " + name, help, value ? "true" : "false", Group: group, FeatureFlag: Feature,
                DependNonDefault: (master ?? Enabled).Type.ID, IsAdvanced: advanced, OrderPriority: AllParams.Count));
            AllParams.Add(param.Type);
            return Toggles[key] = param;
        }
        void Integer(string key, string name, int value, int min, int max, string help, int step = 1, bool advanced = false, T2IRegisteredParam<bool> master = null)
        {
            Integers[key] = T2IParamTypes.Register<int>(new("LongCat Avatar " + name, help, value.ToString(CultureInfo.InvariantCulture), Min: min, Max: max, Step: step, Group: group, FeatureFlag: Feature,
                DependNonDefault: (master ?? Enabled).Type.ID, IsAdvanced: advanced, OrderPriority: AllParams.Count));
            AllParams.Add(Integers[key].Type);
        }
        void Number(string key, string name, double value, double min, double max, string help, double step = 0.01, bool advanced = false, T2IRegisteredParam<bool> master = null)
        {
            Numbers[key] = T2IParamTypes.Register<double>(new("LongCat Avatar " + name, help, value.ToString(CultureInfo.InvariantCulture), Min: min, Max: max, Step: step, Group: group, FeatureFlag: Feature,
                DependNonDefault: (master ?? Enabled).Type.ID, IsAdvanced: advanced, OrderPriority: AllParams.Count));
            AllParams.Add(Numbers[key].Type);
        }
        void Text(string key, string name, string value, string help, string[] choices = null, bool advanced = false, T2IRegisteredParam<bool> master = null)
        {
            Strings[key] = T2IParamTypes.Register<string>(new("LongCat Avatar " + name, help, value, GetValues: choices is null ? null : _ => [.. choices], Group: group, FeatureFlag: Feature,
                DependNonDefault: (master ?? Enabled).Type.ID, IsAdvanced: advanced, OrderPriority: AllParams.Count));
            AllParams.Add(Strings[key].Type);
        }
    }

    /// <summary>Swarm does not know this architecture; the downloader's file names (LongCat_Video_Avatar_1.6_INT8_ConvRot, _BF16) identify it.</summary>
    private static bool IsLongCatModel(T2IModel model) =>
        model?.Name?.Replace('-', '_').Replace(' ', '_').Contains(ModelMarker, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Whisper files in every model root's audio_encoders folder (the folder SwarmUI forwards to its ComfyUI), the default first.</summary>
    private static List<string> WhisperFiles(Session session)
    {
        List<string> files = [];
        try
        {
            foreach (string root in Program.ServerSettings.Paths.ActualModelRoots)
            {
                string folder = Path.Combine(root.Trim(), "audio_encoders");
                if (Directory.Exists(folder))
                {
                    files.AddRange(Directory.EnumerateFiles(folder, "*.safetensors", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')));
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"LongCat Avatar: could not list audio_encoders: {ex.Message}");
        }
        files = [.. files.Where(f => f != WhisperDefault).Distinct().OrderBy(f => f.Contains("whisper", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)];
        files.Insert(0, WhisperDefault);
        return files;
    }

    private static void Generate(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(Enabled, false))
        {
            return;
        }
        if (!g.Features.Contains(Feature))
        {
            throw new SwarmUserErrorException("LongCat Avatar needs the updated SECoursesAudioTools nodes on the ComfyUI backend. Run Windows_Install_or_Update_SwarmUI.bat and restart SwarmUI and its ComfyUI backend.");
        }
        AudioFile audio = null;
        if (g.UserInput.TryGet(T2IParamTypes.PromptAudios, out List<AudioFile> attached) && attached.Count > 0)
        {
            audio = attached[0];
        }
        else if (g.UserInput.TryGet(T2IParamTypes.VideoAudioInput, out AudioFile videoAudio))
        {
            audio = videoAudio;
        }
        if (audio is null)
        {
            throw new SwarmUserErrorException("LongCat Avatar needs a voice or song: attach audio to the prompt (or set Video Audio Input).");
        }
        g.FinalLoadedModel = g.UserInput.Get(T2IParamTypes.Model);
        g.FinalLoadedModelList = [g.FinalLoadedModel];
        string loader = g.CreateNode("SELongCatAvatarLoader", new JObject()
        {
            ["unet_name"] = g.FinalLoadedModel.ToString(g.ModelFolderFormat),
            ["weight_dtype"] = g.UserInput.Get(WeightDtype)
        });
        string textLoader = g.CreateNode("CLIPLoader", new JObject()
        {
            ["clip_name"] = File(TextEncoder),
            ["type"] = "wan",
            ["device"] = "default"
        });
        JArray model = WorkflowGenerator.NodePath(loader, 0);
        JArray clip = WorkflowGenerator.NodePath(textLoader, 0);
        // Like the core loader: LoRAs without a section (API requests) and global LoRAs (the LoRA list), on the LongCat MODEL.
        (model, clip) = g.LoadLorasForConfinement(-1, model, clip);
        (model, clip) = g.LoadLorasForConfinement(0, model, clip);
        string text = g.UserInput.Get(T2IParamTypes.Prompt, "").Trim();
        string positive = g.CreateNode("CLIPTextEncode", new JObject()
        {
            ["clip"] = clip,
            ["text"] = text.Length > 0 ? text : DefaultScene
        });
        string whisper = g.UserInput.Get(Whisper, WhisperDefault).Replace('\\', '/').Replace("/", g.ModelFolderFormat ?? "/");
        string audioEncoder = g.CreateNode("SELongCatWhisperLoader", new JObject() { ["audio_encoder_name"] = whisper });
        string audioLoad = g.CreateAudioLoadNode(audio, "${promptaudios.0}");
        int width = Math.Clamp(g.UserInput.GetImageWidth(), 256, 4096);
        int height = Math.Clamp(g.UserInput.GetImageHeight(), 256, 4096);
        bool distilled = g.UserInput.Get(Distilled);
        JObject inputs = new()
        {
            ["model"] = model,
            ["positive"] = WorkflowGenerator.NodePath(positive, 0),
            ["vae"] = g.CreateVAELoader(File(Vae)),
            ["audio_encoder"] = WorkflowGenerator.NodePath(audioEncoder, 0),
            ["audio"] = WorkflowGenerator.NodePath(audioLoad, 0),
            ["width"] = width,
            ["height"] = height,
            ["seed"] = g.UserInput.Get(T2IParamTypes.Seed),
            ["dmd_lora"] = File(DmdLora),
            ["opsd_lora"] = File(OpsdLora),
            ["filename_prefix"] = "video/LongCat_Avatar/Swarm"
        };
        foreach ((string key, T2IRegisteredParam<bool> param) in Toggles) inputs[key] = g.UserInput.Get(param);
        foreach ((string key, T2IRegisteredParam<int> param) in Integers) inputs[key] = g.UserInput.Get(param);
        foreach ((string key, T2IRegisteredParam<double> param) in Numbers) inputs[key] = g.UserInput.Get(param);
        foreach ((string key, T2IRegisteredParam<string> param) in Strings) inputs[key] = g.UserInput.Get(param);
        if (!distilled)
        {
            // The 50-step base mode runs guidance against the negative prompt; empty = the official negative prompt.
            string negativeText = g.UserInput.Get(T2IParamTypes.NegativePrompt, "").Trim();
            string negative = g.CreateNode("CLIPTextEncode", new JObject()
            {
                ["clip"] = clip,
                ["text"] = negativeText.Length > 0 ? negativeText : OfficialNegative
            });
            inputs["negative"] = WorkflowGenerator.NodePath(negative, 0);
        }
        if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image image))
        {
            if (image.Type.MetaType == MediaMetaType.Video)
            {
                throw new SwarmUserErrorException("LongCat Avatar Init Image must be a still image.");
            }
            inputs["image"] = g.LoadImage(image, "${initimage}", false).Path;
        }
        // The sampler saves the MP4 with the original audio itself; its UI output is what Swarm shows and stores in History.
        g.CreateNode("SELongCatAvatarSampler", inputs);
        g.SkipFurtherSteps = true;

        string File(T2IRegisteredParam<T2IModel> param) => g.UserInput.Get(param)?.ToString(g.ModelFolderFormat)
            ?? throw new SwarmUserErrorException($"Select {param.Type.Name} (an existing local file).");
    }
}
