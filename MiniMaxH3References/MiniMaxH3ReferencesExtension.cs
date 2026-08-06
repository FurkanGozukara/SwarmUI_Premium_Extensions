using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.MiniMaxH3References;

/// <summary>Adds complete MiniMax H3 image, video, and audio reference inputs to SwarmUI.</summary>
public class MiniMaxH3ReferencesExtension : Extension
{
    private static bool _initialized;
    private static T2IRegisteredParam<bool> Enabled;
    private static T2IRegisteredParam<string> ReferenceImageSize;
    private static List<T2IRegisteredParam<VideoFile>> ReferenceVideos = [];
    private static List<T2IRegisteredParam<AudioFile>> ReferenceAudios = [];
    private static T2IRegisteredParam<bool> SpeedOptimize;
    private static T2IRegisteredParam<double> SpeedCacheThreshold;
    private static T2IRegisteredParam<string> SpeedSparseAttention;

    /// <summary>Feature id advertised when the ComfyUI backend has the MiniMaxH3SpeedOptimizer node (shipped by FurkanGozukara/ComfyUI-TeaCache).</summary>
    public const string SpeedFeatureId = "minimax_h3_speed";

    /// <summary>This extension is installed by the SECourses updater rather than a git clone, so metadata is set directly instead of read from git.</summary>
    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Adds the complete MiniMax H3 reference workflow, a unified prompt uploader for up to nine images, three videos, and three audio files (with colored @image1 / @video1 / @audio1 prompt tokens and autocomplete), and the NVlabs Sana sol-engine 4x speed optimizations with a one-click core parameter.";
        License = "MIT";
        Version = "1.5.1";
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

        ScriptFiles.Add("Assets/minimax_h3_prompt_references.js");
        StyleSheetFiles.Add("Assets/minimax_h3_prompt_references.css");
        // Advertise the speed feature only when the backend actually has the optimizer node.
        ComfyUIBackendExtension.NodeToFeatureMap["MiniMaxH3SpeedOptimizer"] = SpeedFeatureId;
        RegisterParameters();
        WorkflowGenerator.AddStep(ApplyReferences, -7.9);
        WorkflowGenerator.AddModelGenStep(ApplySpeedOptimizations, -3.5);
        WorkflowGenerator.AddStep(ReplaceLegacyBatchImages, 199);
        Logs.Info("MiniMax H3 complete image, video, and audio reference support initialized.");
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
                "Internal slot populated by the MiniMax H3 prompt reference uploader. The first 15 seconds are used, frames are resampled to 24 FPS, and an available soundtrack is paired automatically.",
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
        List<VideoFile> videos = GetValues(g, ReferenceVideos);
        List<AudioFile> audios = GetValues(g, ReferenceAudios);
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
            throw new SwarmUserErrorException("MiniMax H3 References needs at least one Prompt Image, reference video, or reference audio file.");
        }
        string prompt = TranslatePromptReferenceTokens(g.UserInput.Get(T2IParamTypes.Prompt, ""),
            images.Count, videos.Count, standaloneAudioCount, videos.Count + (hasLegacyAudio ? 1 : 0));

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

        for (int i = 0; i < videos.Count; i++)
        {
            string loaded = g.CreateNode("SwarmLoadVideoB64", new JObject()
            {
                ["video_base64"] = videos[i].AsBase64
            });
            string trimmed = g.CreateNode("Video Slice", new JObject()
            {
                ["video"] = WorkflowGenerator.NodePath(loaded, 0),
                ["start_time"] = 0.0,
                ["duration"] = 15.0,
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
            inputs[$"ref_audios.ref_audio_{i}"] = WorkflowGenerator.NodePath(loaded, 0);
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
        Logs.Info($"Created MiniMax H3 reference workflow with {images.Count} image, {videos.Count} video, and {audios.Count} standalone audio reference(s).");
    }

    private static readonly Regex PromptReferenceTokenMatcher = new(
        @"(?<![\w@])@(?<type>image|img|picture|pic|video|vid|audio|aud|sound)#?(?<num>\d{1,2})(?![0-9A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Translates prompt-bar "@image1" / "@video2" / "@audio3" reference tokens into the
    /// "&lt;Picture 1&gt;" / "&lt;Video 2&gt;" / "&lt;Audio n&gt;" labels the MiniMax H3 node expects.
    /// Audio labels index video soundtracks first, so standalone audio tokens are offset by the
    /// video count (and by the legacy audio reference, when present). Legacy labels typed directly
    /// in the prompt pass through unchanged. Tokens that point at a missing reference (eg '@image3'
    /// with two images attached) are silently omitted, together with one adjacent space, so a stale
    /// token left in the prompt never blocks generation.
    /// </summary>
    public static string TranslatePromptReferenceTokens(string prompt, int imageCount, int videoCount, int standaloneAudioCount, int audioLabelOffset)
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
                "video" or "vid" => ("Video", videoCount, 0),
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
