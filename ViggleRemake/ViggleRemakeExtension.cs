using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.ViggleRemake;

/// <summary>Viggle Remake in SwarmUI: the attached video, up to four photos and the prompt become a remade video through the
/// SECoursesAudioTools Viggle Remake nodes (Plan -> Paint -> Prompt -> Render). The Render node joins the windows, scales to
/// 1080p, keeps the source soundtrack and saves the MP4 itself.</summary>
public class ViggleRemakeExtension : Extension
{
    private const string Feature = "secourses_viggle_remake";
    private const string ModelMarker = "viggle_remake";
    // The flags MiniMaxH3References maps these ComfyUI-TeaCache nodes to (one flag per node class: keep the same ids).
    private const string LowVramFeature = "minimax_h3_low_vram";
    private const string SpeedFeature = "minimax_h3_speed";
    private const string VaeSpeedFeature = "minimax_h3_vae_speedup";
    private const int MaxPhotos = 4;
    // Same strings as SECoursesAudioTools viggle_remake_nodes.py (MODES, TIERS, REF_MODES, OUTFITS, PROMPT_MODES, OUTPUT_SIZES)
    // and minimax_h3_image_nodes.ENHANCE_MODES; the nodes validate them again.
    private static readonly string[] Modes = ["Character swap (people become your characters)", "Video edit (change objects, background or style; people stay)"];
    private const string ModeAuto = "Auto: from the model file (Swap or Edit)";
    private static readonly string[] Tiers = ["1280x736 class (best quality)", "832x480 class (fast draft)"];
    private static readonly string[] WindowChoices = ["Auto (longest my GPU handles)", "14.4 s (345 frames)", "10.1 s (243 frames)", "7.3 s (175 frames)", "5.2 s (124 frames)"];
    private static readonly string[] RefModes = ["Auto: paint the keyframes from my photos and text (recommended)", "My gallery images are finished reference frames (no painting)"];
    private static readonly string[] Outfits = ["Keep each person's original clothes (best for recasting real people)", "Whole person from the photo or text: face, hair, body and outfit"];
    private static readonly string[] EnhanceModes = ["On: official enhancer with reasoning (best)", "On: fast enhancer without reasoning", "Off: use my prompt exactly as written"];
    private static readonly string[] PromptModes = ["Auto: Qwen3.5 9B looks at each window and writes its prompt (best)", "Template: built-in prompt from my text (fast)", "My text is the complete six-section prompt (advanced)"];
    private static readonly string[] OutputSizes = ["1080p: 1920 long edge at the source aspect (recommended)", "Render size (no upscale)", "Source resolution"];
    private static readonly Regex TrimMatcher = new(@"^1:(\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)$", RegexOptions.Compiled);

    private static T2IRegisteredParam<bool> Enabled, LowVram, Optimizations, VaeSpeedup;
    private static T2IRegisteredParam<string> Mode, RefMode, SparseAttention, PromptList, People;
    private static T2IRegisteredParam<double> CacheThreshold, StartSeconds, MaxSeconds;
    private static T2IRegisteredParam<T2IModel> PainterModel, ImageVae, TextEncoder, VideoVae, AudioVae, WriterModel, SamModel;
    private static readonly List<T2IParamType> AllParams = [];
    // Node inputs keyed by node class, then by the Python input name, so every control maps 1:1 onto its node input.
    private static readonly Dictionary<string, Dictionary<string, Func<T2IParamInput, JToken>>> NodeInputs = new()
    {
        ["SEViggleRemakePlan"] = [],
        ["SEViggleRemakePaint"] = [],
        ["SEViggleRemakePrompt"] = [],
        ["SEViggleRemakeRender"] = []
    };

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Viggle Remake: one click from your video, up to four photos and a sentence to a remade video (character swap or video edit), with automatically painted keyframes, written prompts, 1080p output and the original soundtrack.";
        Version = "1.1.0";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/ViggleRemake";
    }

    public override void OnPreInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["SEViggleRemakeRender"] = Feature;
        // Also mapped here so this extension works without MiniMaxH3References.
        ComfyUIBackendExtension.NodeToFeatureMap["MiniMaxH3LowVRAM"] = LowVramFeature;
        ComfyUIBackendExtension.NodeToFeatureMap["MiniMaxH3SpeedOptimizer"] = SpeedFeature;
        ComfyUIBackendExtension.NodeToFeatureMap["MiniMaxH3VAESpeedup"] = VaeSpeedFeature;
    }

    public override void OnInit()
    {
        T2IParamGroup group = new("Viggle Remake", Open: true, OrderPriority: 8.6,
            Description: "Select a Viggle Remake model (Swap or Edit), switch Viggle Remake on, attach your video and up to four photos to the prompt, and write who becomes whom, e.g. 'Elon Musk (@image1) replaces the man in the dark blazer on the right'. Seed and ordinary LoRAs apply; Width, Height, Steps, CFG and Video FPS do not (the canvas follows your video).");
        Enabled = T2IParamTypes.Register<bool>(new("Viggle Remake Enabled", "Use the dedicated Viggle Remake workflow (video + photos + text to a remade video). Requires the updated SECoursesAudioTools node pack on the ComfyUI backend. Applies only when the main model is a Viggle Remake file. No model downloads are performed.",
            "false", IgnoreIf: "false", Group: group, FeatureFlag: Feature, OrderPriority: -20));
        AllParams.Add(Enabled.Type);

        // Plan
        Mode = Text("SEViggleRemakePlan", "mode", "Mode", ModeAuto, "Character swap: the people you name become your characters (face, hair, body; clothes optional). Video edit: an object is removed or replaced, the background or the weather changes, the people stay. Auto: Swap for viggle_remake_swap files, Edit for viggle_remake_edit files.", [ModeAuto, .. Modes]);
        Text("SEViggleRemakePlan", "quality", "Quality", Tiers[0], "1280x736 class (the canvas follows your video's aspect): the published high-resolution setting. 832x480 class: about 3x faster drafts.", Tiers);
        Text("SEViggleRemakePlan", "max_window_seconds", "Max Window Seconds", WindowChoices[0], "Longest single render. Longer clips are split at their cuts; a long shot continues seamlessly in hops. Auto: 345 frames on 32 GB+ cards, 175 on 24 GB, 124 below (with Low VRAM on).", WindowChoices);
        RefMode = Text("SEViggleRemakePlan", "references_mode", "References Mode", RefModes[0], "Auto: one keyframe per shot is chosen and painted by the local MiniMax H3 Image model from your photos and text. Finished reference frames: your attached images are frames of your video that you already edited; nothing is painted.", RefModes);
        Text("SEViggleRemakePlan", "outfit", "Outfit", Outfits[0], "Swap only. Keep the original clothes: the new person takes the role in the costume of the scene. Whole person: face, hair, build and outfit come from your photo or text (mascots, animals, invented characters).", Outfits);
        People = Text("SEViggleRemakePlan", "people", "People", "", "Who becomes whom by face, set with the 'Find the people in my video' button: every actor of your video (found by face) and the attached photo that replaces them. With it the prompt text is optional. Empty: your text decides.");
        Integer("SEViggleRemakePlan", "max_cuts_per_window", "Max Cuts Per Window", 3, 0, 12, "Windows with many cuts drop or reorder short shots; at most this many cuts inside one render where the cuts allow.", true);
        StartSeconds = Number("SEViggleRemakePlan", "start_seconds", "Start Seconds", 0, 0, 36000, 0.01, "Skip the start of the video (added to the trim of the attached video).");
        MaxSeconds = Number("SEViggleRemakePlan", "max_seconds", "Max Seconds", 0, 0, 36000, 0.1, "0 = the whole video (after trimming). Each 14 s window takes several minutes: start with a short clip.");
        // Paint
        Text("SEViggleRemakePaint", "enhance", "Paint Enhance", EnhanceModes[2], "Off (default): the painter gets the keyframe instruction as written; it keeps each frame's composition, about 40 s per keyframe. The official MiniMax H3 Image edit enhancer (Qwen3.5 9B) rewrites it as a full caption: in our tests it painted the whole cast into close-ups that show one person (with reasoning, 3 minutes per keyframe) and misread the photos (fast).", EnhanceModes);
        Integer("SEViggleRemakePaint", "steps", "Paint Steps", 6, 1, 50, "MiniMax H3 Image is distilled for 6 steps.", true);
        Number("SEViggleRemakePaint", "paint_megapixels", "Paint Megapixels", 2.0, 0.5, 8.0, 0.1, "Painting size; the result is scaled to the render canvas. 2 MP keeps faces detailed.", true);
        Text("SEViggleRemakePaint", "extra_instruction", "Extra Instruction", "", "Optional words added to every keyframe's instruction, e.g. 'Elon Musk wears a black t-shirt'.");
        // Prompt
        Text("SEViggleRemakePrompt", "prompt_mode", "Prompt Mode", PromptModes[0], "Auto: Qwen3.5 9B watches each window and the painted pictures and writes the six-section prompt with the official guide (a malformed answer falls back to the template). Template: no language model, a sound generic prompt. My text: your prompt is used verbatim as the six-section prompt of every window.", PromptModes);
        // Render
        Text("SEViggleRemakeRender", "output_size", "Output Size", OutputSizes[0], "1080p: the render is scaled (Lanczos) to 1920 on the long edge at your video's aspect. Render size: the canvas itself. Source resolution: your video's own size.", OutputSizes);
        Number("SEViggleRemakeRender", "keyframe_until", "Keyframe Until", 0.9, 0, 1, 0.01, "Official 0.9: the pinned keyframes condition the first four of the six steps, then only the references remain.", true);
        Integer("SEViggleRemakeRender", "video_crf", "Video CRF", 16, 0, 40, "H.264 quality of the saved MP4; lower means larger files and higher quality.", true);

        // Speed and memory (ComfyUI-TeaCache MiniMax H3 nodes, wired like MiniMaxH3References).
        LowVram = Toggle("Low VRAM", true, "Exact-math low VRAM for the MiniMax H3 transformer (MiniMaxH3LowVRAM, exact output, feedforward in 4 chunks): same result, less memory. Needs ComfyUI-TeaCache on the backend.");
        Optimizations = Toggle("Optimizations", false, "MiniMax H3 speed optimizer (First Block Cache and sparse attention). Faster, approximate: the result changes slightly. Needs ComfyUI-TeaCache on the backend.");
        SparseAttention = T2IParamTypes.Register<string>(new("Viggle Remake Sparse Attention", "Sparse attention mode of the speed optimizer (auto keeps dense attention unless it is at least 5% faster).",
            "auto", GetValues: _ => ["auto", "enabled", "disabled", "vsa"], Group: group, FeatureFlag: Feature, DependNonDefault: Optimizations.Type.ID, IsAdvanced: true, OrderPriority: AllParams.Count));
        AllParams.Add(SparseAttention.Type);
        CacheThreshold = T2IParamTypes.Register<double>(new("Viggle Remake Cache Threshold", "First Block Cache threshold (0.08 conservative default; higher skips more steps and changes the result more).",
            "0.08", Min: 0, Max: 1, Step: 0.005, Group: group, FeatureFlag: Feature, DependNonDefault: Optimizations.Type.ID, IsAdvanced: true, OrderPriority: AllParams.Count));
        AllParams.Add(CacheThreshold.Type);
        VaeSpeedup = Toggle("VAE Speedup", true, "Faster tiled decode of the MiniMax H3 video VAE (MiniMaxH3VAESpeedup, tile batch auto from free VRAM). Needs ComfyUI-TeaCache on the backend.");

        // Companion models: existing local files only.
        PainterModel = Model("Viggle Remake Painter Model", "MiniMax H3 Image model (diffusion_models) that paints the keyframes.", "minimax_h3_image_pruned_int8_convrot", "Stable-Diffusion", 100);
        ImageVae = Model("Viggle Remake Image VAE", "MiniMax H3 Image VAE for the painter.", "MiniMaxH3/minimax_h3_image_vae_fp16", "VAE", 101);
        TextEncoder = Model("Viggle Remake Text Encoder", "MiniMax H3 Qwen3-VL 32B encoder (loaded as type minimax), shared by the painter and the video model.", "qwen3vl_32b_minimax_h3_int8_convrot", "Clip", 102);
        VideoVae = Model("Viggle Remake Video VAE", "MiniMax H3 video VAE.", "MiniMaxH3/minimax_h3_video_vae_int8_convrot", "VAE", 103);
        AudioVae = Model("Viggle Remake Audio VAE", "MiniMax H3 audio VAE.", "MiniMaxH3/minimax_h3_audio_vae_fp32", "VAE", 104);
        WriterModel = Model("Viggle Remake Writer Model", "Qwen3.5 9B (loaded as type minimax) for the prompt writer, the optional paint enhancer and for checking which of your named people a keyframe shows. Loaded only when one of them needs it.", "qwen3.5_9b_int8_convrot", "Clip", 105);
        SamModel = Model("Viggle Remake SAM Model", "SAM 3.1 checkpoint (Models/Stable-Diffusion, loaded with Load Checkpoint): segments each repainted person so only that person is pasted back into the keyframe.", "sam3.1_multiplex_fp16", "Stable-Diffusion", 106);

        PromptList = T2IParamTypes.Register<string>(new("Viggle Remake Prompt List", "Extra prompts for Generate prompt batch. Add prompts or import .txt files or a folder; each file is one complete prompt. The main prompt runs first, then the list, one video per prompt, with the same captured settings, video and photos.",
            "[]", ViewType: ParamViewType.BIG, Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, OrderPriority: 120, IntentionalUnused: true));
        AllParams.Add(PromptList.Type);

        T2IParamInput.SpecialParameterHandlers.Add(input =>
        {
            // Presets only change their own keys: a leftover switch must never affect another model, and options sent
            // while the extension is off must neither require its backend nodes nor land in the metadata.
            if (!input.Get(Enabled, false) || !IsViggleModel(input.Get(T2IParamTypes.Model)))
            {
                foreach (T2IParamType param in AllParams)
                {
                    input.Remove(param);
                }
                input.RequiredFlags.Remove(Feature);
                return;
            }
            // Swarm's preliminary loader does not copy extension parameters and would load the main model with the
            // MiniMax H3 default companions; this graph loads the model and its companions itself.
            input.Set(T2IParamTypes.NoLoadModels, true);
        });
        // Before the normal model loader (-15).
        WorkflowGenerator.AddStep(Generate, -16);
        ScriptFiles.Add("Assets/viggle_remake.js");
        StyleSheetFiles.Add("Assets/viggle_remake.css");

        T2IRegisteredParam<T2IModel> Model(string name, string help, string value, string subtype, double order)
        {
            T2IRegisteredParam<T2IModel> param = T2IParamTypes.Register<T2IModel>(new(name, help + " Existing local model file. No automatic download.", value, Subtype: subtype,
                GetValues: subtype == "Stable-Diffusion" ? s => T2IParamTypes.CleanModelList(Program.MainSDModels.ListModelNamesFor(s)) : null,
                Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, IsAdvanced: true, OrderPriority: order));
            AllParams.Add(param.Type);
            return param;
        }
        T2IRegisteredParam<bool> Toggle(string name, bool value, string help)
        {
            T2IRegisteredParam<bool> param = T2IParamTypes.Register<bool>(new("Viggle Remake " + name, help, value ? "true" : "false", Group: group, FeatureFlag: Feature,
                DependNonDefault: Enabled.Type.ID, OrderPriority: AllParams.Count));
            AllParams.Add(param.Type);
            return param;
        }
        T2IRegisteredParam<string> Text(string node, string key, string name, string value, string help, string[] choices = null, bool advanced = false)
        {
            T2IRegisteredParam<string> param = T2IParamTypes.Register<string>(new("Viggle Remake " + name, help, value, GetValues: choices is null ? null : _ => [.. choices],
                ViewType: choices is null ? ParamViewType.BIG : ParamViewType.NORMAL, Group: group, FeatureFlag: Feature,
                DependNonDefault: Enabled.Type.ID, IsAdvanced: advanced, OrderPriority: AllParams.Count));
            AllParams.Add(param.Type);
            NodeInputs[node][key] = input => input.Get(param, value);
            return param;
        }
        void Integer(string node, string key, string name, int value, int min, int max, string help, bool advanced = false)
        {
            T2IRegisteredParam<int> param = T2IParamTypes.Register<int>(new("Viggle Remake " + name, help, value.ToString(CultureInfo.InvariantCulture), Min: min, Max: max, Step: 1,
                Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, IsAdvanced: advanced, OrderPriority: AllParams.Count));
            AllParams.Add(param.Type);
            NodeInputs[node][key] = input => input.Get(param, value);
        }
        T2IRegisteredParam<double> Number(string node, string key, string name, double value, double min, double max, double step, string help, bool advanced = false)
        {
            T2IRegisteredParam<double> param = T2IParamTypes.Register<double>(new("Viggle Remake " + name, help, value.ToString(CultureInfo.InvariantCulture), Min: min, Max: max, Step: step,
                Group: group, FeatureFlag: Feature, DependNonDefault: Enabled.Type.ID, IsAdvanced: advanced, OrderPriority: AllParams.Count));
            AllParams.Add(param.Type);
            NodeInputs[node][key] = input => input.Get(param, value);
            return param;
        }
    }

    /// <summary>The downloader's merged files (viggle_remake_swap_pruned_int8_convrot, viggle_remake_edit_pruned_int8_convrot) are named for it;
    /// Swarm may class them as MiniMax H3, so only the name identifies them.</summary>
    private static bool IsViggleModel(T2IModel model) =>
        model?.Name?.Replace('-', '_').Replace(' ', '_').Contains(ModelMarker, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The source video: the first video of the MiniMax H3 reference uploader (with its trim), else the first prompt
    /// video, else an Init Image that is a video. Returns null when none is attached.</summary>
    private static (string Base64, double TrimStart, double TrimEnd)? SourceVideo(T2IParamInput input)
    {
        if (T2IParamTypes.Types.TryGetValue("minimaxhreferencevideoone", out T2IParamType slot) && input.TryGetRaw(slot, out object raw) && raw is VideoFile slotVideo)
        {
            double start = 0, end = 0;
            if (T2IParamTypes.Types.TryGetValue("minimaxhreferencevideotrims", out T2IParamType trimsType) && input.TryGetRaw(trimsType, out object trims))
            {
                foreach (string part in $"{trims}".Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Match match = TrimMatcher.Match(part);
                    if (match.Success)
                    {
                        start = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                        end = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    }
                }
            }
            return (slotVideo.AsBase64, start, end > start ? end : 0);
        }
        if (input.TryGet(T2IParamTypes.PromptVideos, out List<VideoFile> videos) && videos.Count > 0)
        {
            return (videos[0].AsBase64, 0, 0);
        }
        if (input.TryGet(T2IParamTypes.InitImage, out Image init) && init.Type.MetaType == MediaMetaType.Video)
        {
            return (init.AsBase64, 0, 0);
        }
        return null;
    }

    /// <summary>The highest photo number the Find people JSON assigns to an actor ({"people":[{"photo":N,"embedding":[...]},...]}), 0 for none.</summary>
    private static int HighestCastPhoto(string people)
    {
        if (string.IsNullOrWhiteSpace(people))
        {
            return 0;
        }
        try
        {
            return (JObject.Parse(people)["people"] as JArray ?? []).Where(p => p["embedding"] is JArray { Count: > 0 }).Select(p => p.Value<int?>("photo") ?? 0).DefaultIfEmpty(0).Max();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            throw new SwarmUserErrorException("Viggle Remake People is not valid: run 'Find the people in my video' again (or clear it).");
        }
    }

    private static void Generate(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(Enabled, false))
        {
            return;
        }
        if (!g.Features.Contains(Feature))
        {
            throw new SwarmUserErrorException("Viggle Remake needs the updated SECoursesAudioTools nodes on the ComfyUI backend. Update SECoursesAudioTools (Windows_Install_or_Update_SwarmUI.bat) and restart the ComfyUI backend.");
        }
        (string Base64, double TrimStart, double TrimEnd) source = SourceVideo(g.UserInput)
            ?? throw new SwarmUserErrorException("Viggle Remake needs your source video: attach it to the prompt (drag it into the prompt box) or set it as the Init Image.");
        List<Image> photos = [.. g.UserInput.Get(T2IParamTypes.PromptImages, [])];
        List<string> photoParams = [.. photos.Select((_, i) => "${promptimages." + i + "}")];
        if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image init) && init.Type.MetaType == MediaMetaType.Image)
        {
            photos.Add(init);
            photoParams.Add("${initimage}");
        }
        if (photos.Count > MaxPhotos)
        {
            throw new SwarmUserErrorException($"Viggle Remake takes at most {MaxPhotos} photos (prompt images plus a still Init Image); {photos.Count} are attached.");
        }
        string text = g.UserInput.Get(T2IParamTypes.Prompt, "").Trim();
        bool ownRefs = g.UserInput.Get(RefMode, RefModes[0]) == RefModes[1];
        T2IModel mainModel = g.UserInput.Get(T2IParamTypes.Model);
        string mode = g.UserInput.Get(Mode, ModeAuto);
        if (mode == ModeAuto)
        {
            mode = mainModel.Name.Contains("edit", StringComparison.OrdinalIgnoreCase) ? Modes[1] : Modes[0];
        }
        int castPhoto = mode == Modes[0] && !ownRefs ? HighestCastPhoto(g.UserInput.Get(People, "")) : 0;
        if (castPhoto > photos.Count)
        {
            throw new SwarmUserErrorException($"Viggle Remake People assigns @image{castPhoto}, but only {photos.Count} photo(s) are attached. Attach the photos again or run 'Find the people in my video' again.");
        }
        if (text.Length == 0 && !ownRefs && castPhoto == 0)
        {
            throw new SwarmUserErrorException("Write who becomes whom in the prompt, e.g. 'Elon Musk (@image1) replaces the man on the right' (swap), or the change, e.g. 'remove the wind turbines' (edit). For a swap you can instead press 'Find the people in my video' and pick a photo for each person.");
        }
        if (ownRefs && photos.Count == 0)
        {
            throw new SwarmUserErrorException("References Mode 'finished reference frames' needs your finished reference frames attached to the prompt as images.");
        }

        g.FinalLoadedModel = mainModel;
        g.FinalLoadedModelList = [mainModel];
        long seed = g.UserInput.Get(T2IParamTypes.Seed);
        string unet = g.CreateNode("UNETLoader", new JObject()
        {
            ["unet_name"] = mainModel.ToString(g.ModelFolderFormat),
            ["weight_dtype"] = "default"
        });
        string encoder = g.CreateNode("CLIPLoader", new JObject()
        {
            ["clip_name"] = File(TextEncoder),
            ["type"] = "minimax",
            ["device"] = "default"
        });
        JArray clip = WorkflowGenerator.NodePath(encoder, 0);
        JArray model = WorkflowGenerator.NodePath(unet, 0);
        // Like the core loader: LoRAs without a section (API requests) and global LoRAs (the LoRA list), on the Viggle MODEL.
        JArray renderClip = clip;
        (model, renderClip) = g.LoadLorasForConfinement(-1, model, renderClip);
        (model, renderClip) = g.LoadLorasForConfinement(0, model, renderClip);
        // Same order and settings as MiniMaxH3References' model-gen steps: speed optimizer (-3.5), then low VRAM (-3.4).
        bool speed = g.UserInput.Get(Optimizations, false);
        if (speed && RequireNode(SpeedFeature, "Viggle Remake Optimizations", "MiniMaxH3SpeedOptimizer"))
        {
            string optimizer = g.CreateNode("MiniMaxH3SpeedOptimizer", new JObject()
            {
                ["model"] = model,
                ["first_block_cache"] = true,
                ["fbc_threshold"] = g.UserInput.Get(CacheThreshold, 0.08),
                ["fbc_start_percent"] = 0.15,
                ["fbc_end_percent"] = 0.95,
                ["fbc_max_consecutive"] = 3,
                ["sparse_attention"] = g.UserInput.Get(SparseAttention, "auto"),
                ["sparse_backend"] = "native",
                ["sparse_extra_tokens"] = 256,
                ["sparse_dense_last_steps"] = 1,
                ["sparse_dense_steps_pct"] = 0.20,
                ["sparse_dense_layers"] = 2,
                ["sparse_tau"] = 1.0,
                ["sparse_min_video_rows"] = 4096,
                ["fbc_cache_device"] = "gpu",
                ["verbose"] = true
            });
            model = WorkflowGenerator.NodePath(optimizer, 0);
        }
        if (g.UserInput.Get(LowVram, true) && RequireNode(LowVramFeature, "Viggle Remake Low VRAM", "MiniMaxH3LowVRAM"))
        {
            string lowVram = g.CreateNode("MiniMaxH3LowVRAM", new JObject()
            {
                ["model"] = model,
                ["enable_low_vram"] = true,
                ["exact_output"] = true,
                ["attention_head_groups"] = 8,
                ["feedforward_chunks"] = 4,
                ["min_tokens"] = 4096
            });
            model = WorkflowGenerator.NodePath(lowVram, 0);
        }
        JArray videoVae = g.CreateVAELoader(File(VideoVae));
        if (g.UserInput.Get(VaeSpeedup, true) && RequireNode(VaeSpeedFeature, "Viggle Remake VAE Speedup", "MiniMaxH3VAESpeedup"))
        {
            string vaeSpeed = g.CreateNode("MiniMaxH3VAESpeedup", new JObject()
            {
                ["vae"] = videoVae,
                ["tile_batch_size"] = 0,
                ["enable_speedup"] = true
            });
            videoVae = WorkflowGenerator.NodePath(vaeSpeed, 0);
        }
        // Lazy inputs on Paint and Prompt: Qwen3.5 is loaded only when the enhancer or the Auto prompt writer asks for it.
        string writer = g.CreateNode("CLIPLoader", new JObject()
        {
            ["clip_name"] = File(WriterModel),
            ["type"] = "minimax",
            ["device"] = "default"
        });
        string painter = g.CreateNode("UNETLoader", new JObject()
        {
            ["unet_name"] = File(PainterModel),
            ["weight_dtype"] = "default"
        });

        // Plan: the source video as a ComfyUI VIDEO (file-backed, full size; the node decodes it at the render canvas itself),
        // the photos and the prompt text, used because no gallery is connected.
        string video = g.CreateNode("SwarmLoadVideoB64", new JObject() { ["video_base64"] = source.Base64 });
        JObject planInputs = Inputs("SEViggleRemakePlan");
        planInputs["video"] = WorkflowGenerator.NodePath(video, 0);
        planInputs["text"] = text;
        planInputs["mode"] = mode;
        double startSeconds = g.UserInput.Get(StartSeconds, 0.0), maxSeconds = g.UserInput.Get(MaxSeconds, 0.0);
        planInputs["start_seconds"] = source.TrimStart + startSeconds;
        if (source.TrimEnd > 0)
        {
            double remaining = Math.Max(0.1, source.TrimEnd - source.TrimStart - startSeconds);
            planInputs["max_seconds"] = maxSeconds > 0 ? Math.Min(maxSeconds, remaining) : remaining;
        }
        for (int i = 0; i < photos.Count; i++)
        {
            planInputs[$"photo_{i + 1}"] = g.LoadImage(photos[i], photoParams[i], false).Path;
        }
        string plan = g.CreateNode("SEViggleRemakePlan", planInputs);

        JObject paintInputs = Inputs("SEViggleRemakePaint");
        paintInputs["plan"] = WorkflowGenerator.NodePath(plan, 0);
        paintInputs["painter_model"] = WorkflowGenerator.NodePath(painter, 0);
        paintInputs["clip"] = clip;
        paintInputs["image_vae"] = g.CreateVAELoader(File(ImageVae));
        paintInputs["enhancer_clip"] = WorkflowGenerator.NodePath(writer, 0);
        string sam = g.CreateNode("CheckpointLoaderSimple", new JObject() { ["ckpt_name"] = File(SamModel) });
        paintInputs["sam_model"] = WorkflowGenerator.NodePath(sam, 0);
        paintInputs["seed"] = seed;
        string paint = g.CreateNode("SEViggleRemakePaint", paintInputs);

        JObject promptInputs = Inputs("SEViggleRemakePrompt");
        promptInputs["plan"] = WorkflowGenerator.NodePath(paint, 0);
        promptInputs["writer_clip"] = WorkflowGenerator.NodePath(writer, 0);
        promptInputs["seed"] = seed;
        string prompt = g.CreateNode("SEViggleRemakePrompt", promptInputs);

        JObject renderInputs = Inputs("SEViggleRemakeRender");
        renderInputs["model"] = model;
        renderInputs["clip"] = renderClip;
        renderInputs["video_vae"] = videoVae;
        renderInputs["audio_vae"] = g.CreateVAELoader(File(AudioVae));
        renderInputs["plan"] = WorkflowGenerator.NodePath(prompt, 0);
        renderInputs["seed"] = seed;
        renderInputs["filename_prefix"] = "video/Viggle_Remake/Swarm";
        // The Render node saves the MP4 with the source soundtrack itself; its UI output is what Swarm shows and stores in History.
        g.CreateNode("SEViggleRemakeRender", renderInputs);
        g.SkipFurtherSteps = true;

        JObject Inputs(string node)
        {
            JObject inputs = new();
            foreach ((string key, Func<T2IParamInput, JToken> value) in NodeInputs[node])
            {
                inputs[key] = value(g.UserInput);
            }
            return inputs;
        }
        bool RequireNode(string flag, string name, string node)
        {
            if (g.Features.Contains(flag))
            {
                return true;
            }
            Logs.Warning($"{name} was requested but the ComfyUI backend does not have the {node} node. Update the ComfyUI-TeaCache node package; continuing without it.");
            return false;
        }
        string File(T2IRegisteredParam<T2IModel> param) => g.UserInput.Get(param)?.ToString(g.ModelFolderFormat)
            ?? throw new SwarmUserErrorException($"Select {param.Type.Name} (an existing local file).");
    }
}
