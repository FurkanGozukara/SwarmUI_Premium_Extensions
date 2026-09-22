using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.QwenImage21;

public class QwenImage21Extension : Extension
{
    private static T2IRegisteredParam<bool> Enabled, Transparent;
    private static T2IRegisteredParam<string> Mode, CacheDevice, CacheDtype;
    private static T2IRegisteredParam<int> ReferenceSize;
    private static T2IRegisteredParam<double> Denoise;
    private static T2IRegisteredParam<T2IModel> TextEncoder, Vae;

    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Qwen Image 2.1 unified generation, reference editing, img2img, inpainting and RGBA.";
        License = "MIT";
        Version = "1.0.1";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/QwenImage21";
    }

    public override void OnPreInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["SEQwenImage21SwarmInputs"] = "secourses_qwen_image21";
        ComfyUIBackendExtension.NodeToFeatureMap["TextEncodeQwenImage21"] = "qwen_image21_native";
    }

    public override void OnInit()
    {
        T2IParamGroup group = new("Qwen Image 2.1", Open: true, OrderPriority: -4,
            Description: "Attach reference images to the prompt: @image1, @image2, etc. The separate Init Image is @init. Main Model, Prompt, Negative Prompt, Width/Height, Seed, Steps, CFG, Sampler and Scheduler apply. Inpaint uses the main Mask Image (white = edit). Output follows init/first reference when present.");
        Enabled = T2IParamTypes.Register<bool>(new("Qwen Unified Enabled", "Use the unified Qwen Image 2.1 workflow. Requires updated SECoursesAudioTools and native ComfyUI Qwen 2.1 support.", "false", IgnoreIf: "false", Group: group, FeatureFlag: "secourses_qwen_image21", OrderPriority: -20));
        Mode = Choice("Qwen Unified Mode", "Generate/reference edit always uses denoise 1 and ignores the mask. Image to image needs Init Image. Inpaint needs Init Image plus a painted Mask Image, and restores unmasked pixels at the working resolution.", "Generate / reference edit", ["Generate / reference edit", "Image to image", "Inpaint masked area"]);
        Denoise = T2IParamTypes.Register<double>(new("Qwen Unified Denoise", "Strength for img2img/inpaint. Lower retains more source detail; 1 allows full replacement. Independent of mask opacity. Generate/reference edit always uses 1.", "0.85", Min: 0.01, Max: 1, Step: 0.01, Group: group, DependNonDefault: Enabled.Type.ID));
        ReferenceSize = T2IParamTypes.Register<int>(new("Qwen Unified Reference Size", "Resize each init/reference to about this size squared, preserving aspect and rounding to 32 pixels. 1024 is about 1 MP; 2048 about 4 MP; 0 keeps original size rounded to 32. Text-only output uses main Width/Height.", "1024", Min: 0, Max: 4096, Step: 32, Group: group, DependNonDefault: Enabled.Type.ID));
        Transparent = T2IParamTypes.Register<bool>(new("Qwen Unified Transparent", "Request a transparent background with the official RGBA prompt wording. Describe an isolated subject. This is generation, not automatic background removal. Save PNG to retain alpha.", "false", Group: group, DependNonDefault: Enabled.Type.ID));
        TextEncoder = T2IParamTypes.Register<T2IModel>(new("Qwen Unified Text Encoder", "Existing Qwen3-VL 8B text/vision encoder from the Qwen Image 2.1 Core Bundle.", "qwen3vl_8b", Subtype: "Clip", Group: group, DependNonDefault: Enabled.Type.ID));
        Vae = T2IParamTypes.Register<T2IModel>(new("Qwen Unified VAE", "Qwen Image 2.1 RGBA VAE. The older Qwen Image VAE is incompatible.", "QwenImage/qwen_image_2.1_vae_bf16", Subtype: "VAE", Group: group, DependNonDefault: Enabled.Type.ID));
        CacheDevice = Choice("Qwen Unified Cache Device", "Auto uses spare VRAM, then RAM. CPU stores the cache in RAM. Off recomputes references every step.", "auto", ["auto", "gpu", "cpu", "off"]);
        CacheDtype = Choice("Qwen Unified Cache Precision", "Default is lossless. Int8/int4 compress the reference cache and can change results.", "default", ["default", "int8", "int4"]);
        T2IParamInput.SpecialParameterHandlers.Add(input =>
        {
            if (!input.Get(Enabled, false)) return;
            // Applying another preset does not reset parameters absent from it.
            if (!IsQwenImage21(input.Get(T2IParamTypes.Model)))
            {
                input.Remove(Enabled);
                input.RequiredFlags.Remove("secourses_qwen_image21");
                return;
            }
            // Swarm's preliminary loader does not copy extension parameters.
            // This graph loads the selected model and its companions itself.
            input.Set(T2IParamTypes.NoLoadModels, true);
        });
        WorkflowGenerator.AddStep(Generate, -16);

        T2IRegisteredParam<string> Choice(string name, string help, string value, string[] choices) =>
            T2IParamTypes.Register<string>(new(name, help, value, GetValues: _ => [.. choices], Group: group, DependNonDefault: Enabled.Type.ID));
    }

    private static bool IsQwenImage21(T2IModel model)
    {
        if (model?.ModelClass is not null)
            return model.ModelClass.CompatClass?.ID == "qwen-image-2.1";
        // Older metadata caches can leave new architectures unclassified.
        // Use Swarm's native detector instead of guessing from the filename.
        if (model is null || !System.IO.File.Exists(model.RawFilePath)
            || Path.GetExtension(model.RawFilePath) is not (".safetensors" or ".sft")) return false;
        return T2IModelClassSorter.ModelClasses["qwen-image-2.1"].IsThisModelOfClass(
            model, T2IModel.GetMetadataHeaderFrom(model.RawFilePath));
    }

    private static void Generate(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(Enabled, false)) return;
        if (!g.Features.Contains("secourses_qwen_image21") || !g.Features.Contains("qwen_image21_native"))
            throw new SwarmUserErrorException("Update ComfyUI and SECoursesAudioTools with the installers, then restart the backend for Qwen Image 2.1.");
        g.FinalLoadedModel = g.UserInput.Get(T2IParamTypes.Model);
        g.FinalLoadedModelList = [g.FinalLoadedModel];
        string loader = g.CreateNode("UNETLoader", new JObject {
            ["unet_name"] = g.FinalLoadedModel.ToString(g.ModelFolderFormat), ["weight_dtype"] = "default" });
        string text = g.CreateNode("CLIPLoader", new JObject {
            ["clip_name"] = File(TextEncoder), ["type"] = "qwen_image", ["device"] = "default" });
        JArray model = WorkflowGenerator.NodePath(loader, 0), clip = WorkflowGenerator.NodePath(text, 0);
        (model, clip) = g.LoadLorasForConfinement(-1, model, clip);
        (model, clip) = g.LoadLorasForConfinement(0, model, clip);
        JArray vae = g.CreateVAELoader(File(Vae));
        JArray references = [];
        if (g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> images))
            foreach (Image image in images) references.Add(ImageData(image));
        string init = g.UserInput.TryGet(T2IParamTypes.InitImage, out Image initImage) ? ImageData(initImage) : "";
        string mask = g.UserInput.TryGet(T2IParamTypes.MaskImage, out Image maskImage) ? ImageData(maskImage) : "";
        string inputs = g.CreateNode("SEQwenImage21SwarmInputs", new JObject {
            ["prompt"] = g.UserInput.Get(T2IParamTypes.Prompt, ""), ["references_json"] = references.ToString(Formatting.None),
            ["init_base64"] = init, ["mask_base64"] = mask });
        string prepare = g.CreateNode("SEQwenImage21Prepare", new JObject {
            ["clip"] = clip, ["vae"] = vae, ["references"] = WorkflowGenerator.NodePath(inputs, 0),
            ["init_image"] = WorkflowGenerator.NodePath(inputs, 1), ["mask"] = WorkflowGenerator.NodePath(inputs, 2),
            ["mode"] = g.UserInput.Get(Mode), ["width"] = g.UserInput.GetImageWidth(), ["height"] = g.UserInput.GetImageHeight(),
            ["reference_resolution"] = g.UserInput.Get(ReferenceSize), ["denoise"] = g.UserInput.Get(Denoise),
            ["transparent"] = g.UserInput.Get(Transparent), ["negative_prompt"] = g.UserInput.Get(T2IParamTypes.NegativePrompt, "") });
        string cache = g.CreateNode("QwenImage21Cache", new JObject {
            ["model"] = model, ["device"] = g.UserInput.Get(CacheDevice), ["dtype"] = g.UserInput.Get(CacheDtype) });
        string sample = g.CreateNode("KSampler", new JObject {
            ["model"] = WorkflowGenerator.NodePath(cache, 0), ["positive"] = WorkflowGenerator.NodePath(prepare, 0),
            ["negative"] = WorkflowGenerator.NodePath(prepare, 1), ["latent_image"] = WorkflowGenerator.NodePath(prepare, 2),
            ["denoise"] = WorkflowGenerator.NodePath(prepare, 3), ["seed"] = g.UserInput.Get(T2IParamTypes.Seed),
            ["steps"] = g.UserInput.Get(T2IParamTypes.Steps), ["cfg"] = g.UserInput.Get(T2IParamTypes.CFGScale),
            ["sampler_name"] = g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler"),
            ["scheduler"] = g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "simple") });
        string decode = g.CreateNode("VAEDecode", new JObject { ["samples"] = WorkflowGenerator.NodePath(sample, 0), ["vae"] = vae });
        string finish = g.CreateNode("SEQwenImage21Finish", new JObject {
            ["images"] = WorkflowGenerator.NodePath(decode, 0), ["preserve_unmasked"] = WorkflowGenerator.NodePath(prepare, 4) });
        // Native SaveImage preserves all four channels and Swarm retrieves its PNG.
        g.CreateNode("SaveImage", new JObject { ["images"] = WorkflowGenerator.NodePath(finish, 0), ["filename_prefix"] = "Qwen_Image_2.1/Swarm" });
        g.SkipFurtherSteps = true;

        string File(T2IRegisteredParam<T2IModel> param) => g.UserInput.Get(param)?.ToString(g.ModelFolderFormat)
            ?? throw new SwarmUserErrorException($"Select {param.Type.Name}.");
        string ImageData(Image image) => image.Type.MetaType == MediaMetaType.Image ? image.AsBase64
            : throw new SwarmUserErrorException("Qwen Image 2.1 references, init and mask must be still images.");
    }
}
