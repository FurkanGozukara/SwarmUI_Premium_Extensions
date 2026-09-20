using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.AvatarForever;

public class AvatarForeverExtension : Extension
{
    private static T2IRegisteredParam<bool> Enabled;
    private static T2IRegisteredParam<T2IModel> TextEncoder, Projection, VideoVae, AudioVae;
    private static readonly Dictionary<string, T2IRegisteredParam<bool>> Toggles = [];
    private static readonly Dictionary<string, T2IRegisteredParam<int>> Integers = [];
    private static readonly Dictionary<string, T2IRegisteredParam<double>> Numbers = [];
    private static readonly Dictionary<string, T2IRegisteredParam<string>> Strings = [];

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "AvatarForever: audio + optional image to long lip-synced video, native INT8 and optional ForeverCache.";
        License = "MIT";
        Version = "1.0.1";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/AvatarForever";
    }

    public override void OnPreInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["SEAvatarForeverSampler"] = "secourses_avatarforever";
    }

    public override void OnInit()
    {
        T2IParamGroup group = new("AvatarForever", Open: true, OrderPriority: 8,
            Description: "Attach audio to the prompt. Init Image is optional. Main Model, Prompt, Width, Height, Seed, Video FPS and ordinary LoRAs apply. Length follows audio; Steps, CFG, negative prompt and refiner controls do not apply.");
        Enabled = T2IParamTypes.Register<bool>(new("AvatarForever Enabled", "Use the dedicated AvatarForever workflow. Requires the updated SECoursesAudioTools node pack. No model downloads are performed.",
            "false", IgnoreIf: "false", Group: group, FeatureFlag: "secourses_avatarforever", OrderPriority: -20));
        TextEncoder = Model("AvatarForever Text Encoder", "gemma_3_12B_it", "Clip");
        Projection = Model("AvatarForever Text Projection", "ltx2/ltx-2.3_text_projection_bf16", "Clip");
        VideoVae = Model("AvatarForever Video VAE", "LTX23_video_vae_bf16", "VAE");
        AudioVae = Model("AvatarForever Audio VAE", "LTX23_audio_vae_bf16", "VAE");
        Toggle("use_image", "Use Image", true, "Use Init Image as the identity/first frame. Off generates the scene from text + audio.");
        Toggle("match_image_aspect", "Match Image Aspect", true, "Preserve image aspect at the Width x Height pixel budget, rounded to 32 pixels.");
        Number("image_strength", "Image Strength", 1, 0, 1, "First-frame latent conditioning strength.");
        Integer("image_compression", "Image Compression", 0, 0, 100, "LTX image preprocessing compression. 0 disables it.");
        Number("audio_start_seconds", "Audio Start Seconds", 0, 0, 100000, "Skip this much source audio.");
        Number("duration_seconds", "Duration Seconds", 0, 0, 100000, "0 = whole audio, otherwise use this many seconds from Audio Start.");
        Number("lead_in_seconds", "Lead In Seconds", 0, 0, 10, "Optional silence prepended to the soundtrack.");
        Text("sigmas", "Sigmas", "1.0, 0.98125, 0.909375, 0.421875, 0.0", "Official four-step Euler schedule. CFG is 1. Main Steps is ignored.");
        Integer("chunk_size", "Chunk Size", 4, 1, 128, "Video latent frames per AR chunk. Official default 4.");
        Integer("history_chunks", "History Chunks", 1, -1, 10000, "Recent history chunks. -1 keeps all history and grows memory with duration.");
        Toggle("sink_first_chunk", "Sink First Chunk", true, "Keep the first chunk visible throughout generation.");
        Toggle("relative_positions", "Relative Positions", true, "Compact the selected history onto relative positions for long videos.");
        Toggle("forever_cache", "Forever Cache", false, "Approximate history-feature reuse after step 1 of each chunk. Released CLI default is off; enabling it can change results.");
        Text("cache_device", "Cache Device", "auto", "Auto selects GPU if there is room, otherwise system RAM.", ["auto", "gpu", "cpu"]);
        Toggle("channel_condition", "Channel Condition", true, "Learned identity condition from Init Image, or the generated first frame when no image is used.");
        Text("channel_mode", "Channel Mode", "gated", "Use the trained gated projection (default), or additive conditioning.", ["gated", "add"]);
        Toggle("first_frame_prefix", "First Frame Prefix", false, "Reuse the first generated latent and its audio when chunk 0 is no longer in the retained history.");
        Text("prefix_position", "Prefix Position", "prepend", "Place the dynamic first-frame condition before or after the selected window.", ["prepend", "append"]);
        Toggle("resident_weights", "Resident Weights", true, "Use native non-dynamic loading to avoid repeated weight paging beside the history cache.");
        Toggle("tiled_vae", "Tiled VAE", true, "Enable spatial VAE tiling. Temporal decoding remains bounded for long movies.");
        Integer("spatial_tile", "Spatial Tile", 512, 64, 4096, "Spatial VAE tile size in pixels.");
        Integer("spatial_overlap", "Spatial Overlap", 64, 0, 1024, "Spatial VAE overlap in pixels.");
        Integer("temporal_tile", "Temporal Tile", 128, 16, 2048, "Temporal VAE tile size in frames.");
        Integer("temporal_overlap", "Temporal Overlap", 32, 8, 512, "Temporal overlap in frames.");
        Integer("crf", "CRF", 17, 0, 51, "H.264 quality; lower means larger files and higher quality.");
        Text("encoding_preset", "Encoding Preset", "fast", "H.264 encoding speed/compression tradeoff.", ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"]);
        Toggle("mouth_enhancement", "Mouth Enhancement", true, "Apply aligned CodeFormer mouth restoration before export. Turn off to keep the original decoded frames.");
        Number("mouth_fidelity", "Mouth Fidelity", .9, 0, 1, "CodeFormer fidelity. 0.9 is the selected comparison recipe.");
        Number("mouth_blend", "Mouth Blend", .7, 0, 1, "Feathered mouth-only blend; 0 leaves frames unchanged.");
        Text("mouth_model", "Mouth Model", "codeformer.pth", "Existing checkpoint under models/facerestore_models. No automatic download.");
        Text("mouth_detector", "Mouth Detector", "models/buffalo_l/det_10g.onnx", "Existing SCRFD detector under models/insightface. No automatic download.");
        // Before the normal loader/audio steps: they can auto-download fallback
        // models and implement speaker-reference audio rather than frozen audio.
        WorkflowGenerator.AddStep(Generate, -16);

        T2IRegisteredParam<T2IModel> Model(string name, string value, string subtype) =>
            T2IParamTypes.Register<T2IModel>(new(name, "Existing local model file. No automatic download.", value, Subtype: subtype, Group: group, DependNonDefault: Enabled.Type.ID));
        void Toggle(string key, string name, bool value, string help) =>
            Toggles[key] = T2IParamTypes.Register<bool>(new("AvatarForever " + name, help, value ? "true" : "false", Group: group, DependNonDefault: Enabled.Type.ID));
        void Integer(string key, string name, int value, int min, int max, string help) =>
            Integers[key] = T2IParamTypes.Register<int>(new("AvatarForever " + name, help, value.ToString(), Min: min, Max: max, Group: group, DependNonDefault: Enabled.Type.ID));
        void Number(string key, string name, double value, double min, double max, string help) =>
            Numbers[key] = T2IParamTypes.Register<double>(new("AvatarForever " + name, help, value.ToString(CultureInfo.InvariantCulture), Min: min, Max: max, Step: 0.01, Group: group, DependNonDefault: Enabled.Type.ID));
        void Text(string key, string name, string value, string help, string[] choices = null) =>
            Strings[key] = T2IParamTypes.Register<string>(new("AvatarForever " + name, help, value, GetValues: choices is null ? null : _ => [.. choices], Group: group, DependNonDefault: Enabled.Type.ID));
    }

    private static void Generate(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(Enabled, false))
        {
            return;
        }
        if (!g.Features.Contains("secourses_avatarforever"))
        {
            throw new SwarmUserErrorException("Update SECoursesAudioTools and restart the ComfyUI backend for AvatarForever.");
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
            throw new SwarmUserErrorException("AvatarForever needs audio attached to the prompt, or Video Audio Input.");
        }
        g.FinalLoadedModel = g.UserInput.Get(T2IParamTypes.Model);
        g.FinalLoadedModelList = [g.FinalLoadedModel];
        string loader = g.CreateNode("SEAvatarForeverLoader", new JObject() { ["unet_name"] = g.FinalLoadedModel.ToString(g.ModelFolderFormat) });
        string textLoader = g.CreateNode("DualCLIPLoader", new JObject() {
            ["clip_name1"] = File(TextEncoder), ["clip_name2"] = File(Projection), ["type"] = "ltxv", ["device"] = "default" });
        (JArray model, JArray clip) = g.LoadLorasForConfinement(0, WorkflowGenerator.NodePath(loader, 0), WorkflowGenerator.NodePath(textLoader, 0));
        string prompt = g.CreateNode("CLIPTextEncode", new JObject() { ["clip"] = clip, ["text"] = g.UserInput.Get(T2IParamTypes.Prompt, "") });
        string audioLoad = g.CreateAudioLoadNode(audio, "${promptaudios.0}");
        JObject inputs = new() {
            ["model"] = model, ["positive"] = WorkflowGenerator.NodePath(prompt, 0),
            ["video_vae"] = g.CreateVAELoader(File(VideoVae)), ["audio_vae"] = g.CreateVAELoader(File(AudioVae)),
            ["audio"] = WorkflowGenerator.NodePath(audioLoad, 0), ["width"] = g.UserInput.GetImageWidth(),
            ["height"] = g.UserInput.GetImageHeight(), ["fps"] = g.UserInput.Get(T2IParamTypes.VideoFPS, 25),
            ["seed"] = g.UserInput.Get(T2IParamTypes.Seed), ["filename_prefix"] = "video/AvatarForever/Swarm"
        };
        foreach ((string key, T2IRegisteredParam<bool> param) in Toggles) inputs[key] = g.UserInput.Get(param);
        foreach ((string key, T2IRegisteredParam<int> param) in Integers) inputs[key] = g.UserInput.Get(param);
        foreach ((string key, T2IRegisteredParam<double> param) in Numbers) inputs[key] = g.UserInput.Get(param);
        foreach ((string key, T2IRegisteredParam<string> param) in Strings) inputs[key] = g.UserInput.Get(param);
        if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image image))
        {
            if (image.Type.MetaType == MediaMetaType.Video)
            {
                throw new SwarmUserErrorException("AvatarForever Init Image must be a still image.");
            }
            inputs["image"] = g.LoadImage(image, "${initimage}", false).Path;
        }
        g.CreateNode("SEAvatarForeverSampler", inputs);
        g.SkipFurtherSteps = true;

        string File(T2IRegisteredParam<T2IModel> param) => g.UserInput.Get(param)?.ToString(g.ModelFolderFormat)
            ?? throw new SwarmUserErrorException($"Select {param.Type.Name}.");
    }
}
