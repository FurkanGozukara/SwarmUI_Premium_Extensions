using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.StableVideoInfinity;

/// <summary>SVI Pro generation through the shared SECourses ComfyUI node.</summary>
public class StableVideoInfinityExtension : Extension
{
    private static T2IRegisteredParam<bool> Enabled, FastMode, TiledVae, ContinueVideo, AppendSource;
    private static T2IRegisteredParam<int> ClipCount, Frames, Motion, Crf;
    private static T2IRegisteredParam<double> Shift, SwitchSigma, SviStrength, FastStrength;
    private static T2IRegisteredParam<T2IModel> LowModel, SviHigh, SviLow, FastHigh, FastLow;
    private static T2IRegisteredParam<VideoFile> SourceVideo;

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Stable Video Infinity 2.0 Pro: long videos, paragraph prompt schedules, persistent identity, latent continuity and video extension.";
        License = "MIT";
        Version = "1.0.0";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/StableVideoInfinity";
    }

    public override void OnPreInit()
    {
        string path = Path.GetFullPath(Path.Combine(FilePath, "ComfyNodes"));
        if (!ComfyUISelfStartBackend.CustomNodePaths.Contains(path))
        {
            ComfyUISelfStartBackend.CustomNodePaths.Add(path);
        }
        ComfyUIBackendExtension.NodeToFeatureMap["SESVIProVideo"] = "secourses_svi_pro";
    }

    public override void OnInit()
    {
        T2IParamGroup group = new("Stable Video Infinity Pro", Open: true, OrderPriority: 8,
            Description: "Image + prompt stream to a long video. Use one paragraph per clip; the last paragraph repeats. Main Model is the high-noise Wan 2.2 I2V model. Steps, CFG, Sampler, Scheduler, Width, Height and Video FPS apply to SVI.");
        Enabled = T2IParamTypes.Register<bool>(new("SVI Pro Enabled", "Enable the dedicated SVI Pro workflow. Upload an Init Image, or enable Continue Video and select a source video. Output is an H.264 MP4. For text-to-video, first generate a starting image.", "false", IgnoreIf: "false", Group: group, FeatureFlag: "secourses_svi_pro", OrderPriority: -20));
        LowModel = T2IParamTypes.Register<T2IModel>(new("SVI Low Noise Model", "The matching Wan 2.2 I2V low-noise model. Main Model supplies the high-noise model.", "wan2.2_i2v_low_noise_14B_fp8_scaled", Subtype: "Stable-Diffusion", GetValues: s => T2IParamTypes.CleanModelList(Program.MainSDModels.ListModelNamesFor(s)), Group: group, DependNonDefault: Enabled.Type.ID));
        ClipCount = Integer("SVI Clip Count", "How many clips to generate. 81 frames then 76 new frames per continuation at motion count 1.", 3, 1, 1000000);
        Frames = Integer("SVI Frames Per Clip", "Frames before trimming the replayed anchor/motion prefix; 81 is the trained length. Rounded down to 4n+1.", 81, 5, 4097);
        Motion = Integer("SVI Motion Latent Count", "1 is the Pro default. 0 generates independent clips sharing the anchor; larger values carry more motion context.", 1, 0, 128);
        Shift = Number("SVI Shift", "Wan flow sampling shift.", 5, 0.01, 100);
        SwitchSigma = Number("SVI Switch Sigma", "Switch from high to low noise at this actual sigma (official boundary 0.875).", 0.875, 0, 1);
        SviHigh = Lora("SVI High LoRA", "SVI_Wan2.2-I2V-A14B_high_noise_lora_v2.0_pro");
        SviLow = Lora("SVI Low LoRA", "SVI_Wan2.2-I2V-A14B_low_noise_lora_v2.0_pro");
        SviStrength = Number("SVI LoRA Strength", "SVI Pro adapter strength; trained recipe is 1.", 1, 0, 2);
        FastMode = Boolean("SVI Fast Mode", "Enable the paired acceleration LoRAs. For the included Seko pair set Steps=4 and CFG=1. Quality default is off, 30 steps / CFG 4.", false);
        FastHigh = Lora("SVI Fast High LoRA", "Wan2_2-I2V-A14B-4steps-lora-rank64-Seko-V1_High");
        FastLow = Lora("SVI Fast Low LoRA", "Wan2_2-I2V-A14B-4steps-lora-rank64-Seko-V1_Low");
        FastStrength = Number("SVI Fast Strength", "Acceleration LoRA strength. Lower strengths with more steps can improve motion.", 1, 0, 2);
        TiledVae = Boolean("SVI Tiled VAE", "Decode one clip at a time with spatial VAE tiles to reduce VRAM.", true);
        ContinueVideo = Boolean("SVI Continue Video", "Continue from the final motion latents encoded from the source video. Init Image optionally supplies a separate identity anchor; otherwise its first frame is used.", false);
        AppendSource = Boolean("SVI Append Source", "Prepend the resampled source video to the newly generated continuation.", true);
        SourceVideo = T2IParamTypes.Register<VideoFile>(new("SVI Source Video", "Video to extend. You can also place a video in Init Image. Enable SVI Continue Video.", null, Group: group, DependNonDefault: ContinueVideo.Type.ID, DoNotPreview: true));
        Crf = Integer("SVI CRF", "H.264 quality: lower is higher quality / larger files.", 18, 0, 51);
        WorkflowGenerator.AddStep(Generate, -14.9);

        T2IRegisteredParam<int> Integer(string name, string help, int value, int min, int max) =>
            T2IParamTypes.Register<int>(new(name, help, value.ToString(), Min: min, Max: max, Group: group, DependNonDefault: Enabled.Type.ID));
        T2IRegisteredParam<double> Number(string name, string help, double value, double min, double max) =>
            T2IParamTypes.Register<double>(new(name, help, value.ToString(System.Globalization.CultureInfo.InvariantCulture), Min: min, Max: max, Step: 0.01, Group: group, DependNonDefault: Enabled.Type.ID));
        T2IRegisteredParam<bool> Boolean(string name, string help, bool value) =>
            T2IParamTypes.Register<bool>(new(name, help, value ? "true" : "false", Group: group, DependNonDefault: Enabled.Type.ID));
        T2IRegisteredParam<T2IModel> Lora(string name, string value) =>
            T2IParamTypes.Register<T2IModel>(new(name, "Paired adapter file; high and low must belong to the same release.", value, Subtype: "LoRA", GetValues: s => T2IParamTypes.CleanModelList(Program.T2IModelSets["LoRA"].ListModelNamesFor(s)), Group: group, DependNonDefault: Enabled.Type.ID));
    }

    private static void Generate(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(Enabled, false))
        {
            return;
        }
        WGNodeData high = g.CurrentModel, clip = g.CurrentTextEnc, vae = g.CurrentVae;
        T2IModel lowModel = g.UserInput.Get(LowModel);
        if (lowModel is null)
        {
            throw new SwarmUserErrorException("Select the SVI Low Noise Model.");
        }
        (_, WGNodeData low, _, _) = g.CreateModelLoader(lowModel, "SVI Low", sectionId: T2IParamInput.SectionID_VideoSwap);
        JObject inputs = new()
        {
            ["high_model"] = high.Path, ["low_model"] = low.Path, ["clip"] = clip.Path, ["vae"] = vae.Path,
            ["prompts"] = g.UserInput.Get(T2IParamTypes.Prompt, ""),
            ["negative_prompt"] = g.UserInput.Get(T2IParamTypes.NegativePrompt, ""),
            ["width"] = g.UserInput.GetImageWidth(), ["height"] = g.UserInput.GetImageHeight(),
            ["clip_count"] = g.UserInput.Get(ClipCount, 3), ["frames_per_clip"] = g.UserInput.Get(Frames, 81),
            ["fps"] = g.UserInput.Get(T2IParamTypes.VideoFPS, 16), ["seed"] = g.UserInput.Get(T2IParamTypes.Seed),
            ["steps"] = g.UserInput.Get(T2IParamTypes.Steps, 30), ["cfg"] = g.UserInput.Get(T2IParamTypes.CFGScale, 4),
            ["sampler_name"] = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler"),
            ["scheduler"] = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "simple"),
            ["shift"] = g.UserInput.Get(Shift, 5), ["switch_sigma"] = g.UserInput.Get(SwitchSigma, 0.875),
            ["motion_latent_count"] = g.UserInput.Get(Motion, 1),
            ["svi_high_lora"] = LoraName(SviHigh), ["svi_low_lora"] = LoraName(SviLow),
            ["svi_strength"] = g.UserInput.Get(SviStrength, 1), ["fast_mode"] = g.UserInput.Get(FastMode, false),
            ["fast_high_lora"] = LoraName(FastHigh), ["fast_low_lora"] = LoraName(FastLow),
            ["fast_strength"] = g.UserInput.Get(FastStrength, 1), ["tiled_vae"] = g.UserInput.Get(TiledVae, true),
            ["continue_video"] = g.UserInput.Get(ContinueVideo, false), ["append_source"] = g.UserInput.Get(AppendSource, true),
            ["crf"] = g.UserInput.Get(Crf, 18)
        };
        if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image image))
        {
            if (image.Type.MetaType == MediaMetaType.Video)
            {
                inputs["source_video"] = LoadVideo(image.AsBase64);
            }
            else
            {
                inputs["start_image"] = g.LoadImage(image, "${initimage}", false).Path;
            }
        }
        if (g.UserInput.TryGet(SourceVideo, out VideoFile video))
        {
            inputs["source_video"] = LoadVideo(video.AsBase64);
        }
        string result = g.CreateNode("SESVIProVideo", inputs);
        g.CreateNode("SwarmSVIProSaveVideo", new JObject() { ["video"] = WorkflowGenerator.NodePath(result, 0) });
        g.SkipFurtherSteps = true;

        JArray LoadVideo(string data) => WorkflowGenerator.NodePath(g.CreateNode("SwarmLoadVideoB64", new JObject() { ["video_base64"] = data }), 0);
        string LoraName(T2IRegisteredParam<T2IModel> param) => g.UserInput.Get(param)?.ToString(g.ModelFolderFormat)
            ?? throw new SwarmUserErrorException($"Select {param.Type.Name}.");
    }
}
