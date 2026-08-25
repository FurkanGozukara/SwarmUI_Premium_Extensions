using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.InitVideoContinuation;

/// <summary>Continues an Init Image video from its final video frames and joins the result back onto the source video.</summary>
public class InitVideoContinuationExtension : Extension
{
    private sealed class ContinuationState(WGNodeData originalVideo, JArray contextImages, int contextFrames,
        double? sourceDuration, JArray sourceVideoPath, string ffmpegPath, bool useStreamingMerge)
    {
        public WGNodeData OriginalVideo = originalVideo;

        public JArray ContextImages = contextImages;

        public int ContextFrames = contextFrames;

        public double? SourceDuration = sourceDuration;

        public JArray SourceVideoPath = sourceVideoPath;

        public string FFmpegPath = ffmpegPath;

        public bool UseStreamingMerge = useStreamingMerge;
    }

    private const string StreamingFeature = "init_video_continuation_ffmpeg";
    private static readonly ConditionalWeakTable<WorkflowGenerator, ContinuationState> States = new();
    private static readonly HashSet<string> OutputNodeClasses =
    [
        "SaveImage",
        "SwarmInitVideoContinuationSave",
        "SwarmSaveAnimationWS",
        "SwarmSaveImageWS"
    ];
    private static readonly HashSet<string> StreamingFormats =
    [
        "h264-mp4",
        "h265-mp4",
        "webm",
        "prores"
    ];

    private static bool _preInitialized;
    private static bool _initialized;
    private static T2IRegisteredParam<bool> ContinueInitVideo;
    private static T2IRegisteredParam<string> ContinuationFrames;

    /// <summary>This extension is installed by the SECourses updater rather than a git clone, so metadata is set directly instead of read from git.</summary>
    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Continues an Init Image video from 1, 5, 22, 39, or 56 final frames and saves the source and generated video as one result.";
        License = "MIT";
        Version = "1.3.1";
        ReadmeURL = "https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions/tree/main/InitVideoContinuation";
    }

    public override void OnPreInit()
    {
        if (_preInitialized)
        {
            return;
        }
        _preInitialized = true;

        string customNodeRoot = Path.GetFullPath(Path.Combine(FilePath, "ComfyNodes"));
        if (Directory.Exists(customNodeRoot) && !ComfyUISelfStartBackend.CustomNodePaths.Contains(customNodeRoot))
        {
            ComfyUISelfStartBackend.CustomNodePaths.Add(customNodeRoot);
        }
        ComfyUIBackendExtension.NodeToFeatureMap["SwarmInitVideoContinuationSave"] = StreamingFeature;
    }

    public override void OnInit()
    {
        if (_initialized)
        {
            Logs.Info("Init Video Continuation extension is already initialized.");
            return;
        }
        _initialized = true;

        ScriptFiles.Add("Assets/init_video_continuation.js");
        RegisterAdditionalVideoTypes();

        ContinueInitVideo = T2IParamTypes.Register<bool>(new(
            "Continue From Last Video Frames",
            "When Init Image is a video, continue from its selected final video-frame context and append only newly generated frames. One frame preserves the original behavior; 5, 22, 39, and 56 use MiniMax H3's native multi-frame guide.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", Group: T2IParamTypes.GroupInitImage,
            OrderPriority: -3.1, IsAdvanced: true, DependNonDefault: T2IParamTypes.InitImage.Type.ID,
            Permission: Permissions.ParamVideo, ChangeWeight: 8));
        T2IParamTypes.ParameterRemaps["continueinitvideofromlastframe"] = ContinueInitVideo.Type.ID;
        ContinuationFrames = T2IParamTypes.Register<string>(new(
            "Continuation Context Frames",
            "Final video frames used as context. One preserves the original last-frame behavior. Multi-frame choices require MiniMax H3, bypass Init Image Creativity, and are removed from the generated segment before it is appended.",
            "1", IgnoreIf: "1", FeatureFlag: "comfyui", Group: T2IParamTypes.GroupInitImage,
            OrderPriority: -3.0, IsAdvanced: true, DependNonDefault: ContinueInitVideo.Type.ID,
            Permission: Permissions.ParamVideo, ChangeWeight: 8,
            GetValues: (_) => ["1", "5", "22", "39", "56"]));

        WorkflowGenerator.AddStep(PrepareLastFrameInit, -8.9);
        WorkflowGenerator.AltImageToVideoPostHandlers.Add(ApplyH3ContextGuide);
        WorkflowGenerator.AddStep(MergeFinalVideo, 199.5);
        Logs.Info("Init Video Continuation extension initialized.");
    }

    private static void PrepareLastFrameInit(WorkflowGenerator g)
    {
        if (!g.UserInput.Get(ContinueInitVideo, false))
        {
            return;
        }
        if (!g.UserInput.TryGet(T2IParamTypes.InitImage, out Image initImage))
        {
            throw new SwarmUserErrorException("Continue From Last Video Frames requires a video in Init Image.");
        }
        if (initImage.Type.MetaType != MediaMetaType.Video)
        {
            throw new SwarmUserErrorException("Continue From Last Video Frames requires a supported video file in Init Image.");
        }
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel))
        {
            throw new SwarmUserErrorException("Continue From Last Video Frames requires a model in the Image To Video group's Video Model input.");
        }
        if (g.BasicInputImage is null || g.BasicInputImage.DataType != WGNodeData.DT_VIDEO || g.CurrentMedia is null)
        {
            throw new SwarmUserErrorException("The Init Image video could not be loaded as video frames. Make sure the selected backend supports SwarmUI video loading.");
        }
        int contextFrames = ParseContextFrames(g.UserInput.Get(ContinuationFrames, "1"));
        if (contextFrames > 1
            && videoModel.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatMiniMaxH3.ID)
        {
            throw new SwarmUserErrorException(
                "Continuation context choices 5, 22, 39, and 56 require a MiniMax H3 Video Model. " +
                "Choose 1 for other video models.");
        }
        int requestedFrames = WorkflowGenerator.MiniMaxH3AlignFrames(
            g.UserInput.Get(T2IParamTypes.VideoFrames, 124));
        if (contextFrames > 1 && requestedFrames <= contextFrames)
        {
            throw new SwarmUserErrorException(
                $"The generated video must be longer than its {contextFrames}-frame continuation context. " +
                "Increase Video Frames or choose a shorter context.");
        }
        if (contextFrames > 1 && g.UserInput.Get(T2IParamTypes.InitImageCreativity, 0.0) != 0.0)
        {
            g.UserInput.Set(T2IParamTypes.InitImageCreativity, 0.0);
            Logs.Info("Ignored Init Image Creativity because multi-frame continuation uses MiniMax H3's native context guide.");
        }

        WGNodeData processedVideo = g.BasicInputImage;
        JArray originalVideoPath = ClonePath(processedVideo.Path);
        (string processedType, JObject processedInputs) = processedVideo.SourceNodeData;
        bool hasInitNoise = processedType == "SwarmImageNoise" && processedInputs?["image"] is JArray;
        if (hasInitNoise)
        {
            originalVideoPath = ClonePath((JArray)processedInputs["image"]);
        }
        WGNodeData originalVideo = processedVideo.WithPath(originalVideoPath, WGNodeData.DT_VIDEO);

        JArray sourceVideoPath = FindSourceVideoPath(g, originalVideoPath);
        string outputFormat = g.UserInput.Get(T2IParamTypes.VideoFormat, "h264-mp4");
        bool streamingSettingsSupported = StreamingFormats.Contains(outputFormat)
            && !g.UserInput.Get(T2IParamTypes.VideoBoomerang, false);
        string ffmpegPath = streamingSettingsSupported ? GetFFmpegPath() : null;
        bool useStreamingMerge = sourceVideoPath is not null
            && ffmpegPath is not null
            && !WorkflowGenerator.RestrictCustomNodes
            && g.Features.Contains(StreamingFeature)
            && streamingSettingsSupported;

        string contextBatch;
        string lastFrame;
        string frameCount = null;
        if (useStreamingMerge)
        {
            contextBatch = g.CreateNode("SwarmInitVideoLastFrame", new JObject()
            {
                ["video"] = ClonePath(sourceVideoPath),
                ["context_frames"] = contextFrames
            });
        }
        else
        {
            frameCount = g.CreateNode("SwarmCountFrames", new JObject()
            {
                ["image"] = ClonePath(originalVideoPath)
            });
            string contextStartIndex = g.CreateNode("SwarmIntAdd", new JObject()
            {
                ["a"] = WorkflowGenerator.NodePath(frameCount, 0),
                ["b"] = -contextFrames
            });
            contextBatch = g.CreateNode("ImageFromBatch", new JObject()
            {
                ["image"] = ClonePath(originalVideoPath),
                ["batch_index"] = WorkflowGenerator.NodePath(contextStartIndex, 0),
                ["length"] = contextFrames
            });
        }
        if (contextFrames == 1)
        {
            lastFrame = contextBatch;
        }
        else
        {
            lastFrame = g.CreateNode("ImageFromBatch", new JObject()
            {
                ["image"] = WorkflowGenerator.NodePath(contextBatch, 0),
                ["batch_index"] = contextFrames - 1,
                ["length"] = 1
            });
        }
        JArray contextBatchPath = WorkflowGenerator.NodePath(contextBatch, 0);
        JArray lastFramePath = WorkflowGenerator.NodePath(lastFrame, 0);

        States.Remove(g);
        States.Add(g, new ContinuationState(originalVideo, ClonePath(contextBatchPath), contextFrames,
            GetSourceDuration(g), sourceVideoPath, ffmpegPath, useStreamingMerge));

        WGNodeData generationImage;
        if (hasInitNoise)
        {
            processedInputs["image"] = ClonePath(lastFramePath);
            generationImage = processedVideo.WithPath(ClonePath(processedVideo.Path), WGNodeData.DT_IMAGE);
        }
        else
        {
            if (useStreamingMerge)
            {
                ReplaceNodeConnectionExcept(g, originalVideoPath, lastFramePath, contextBatch, lastFrame);
            }
            else
            {
                ReplaceNodeConnectionExcept(g, originalVideoPath, lastFramePath, frameCount, contextBatch, lastFrame);
            }
            generationImage = processedVideo.WithPath(ClonePath(lastFramePath), WGNodeData.DT_IMAGE);
        }
        ClearVideoMetadata(generationImage, null);
        g.BasicInputImage = generationImage;

        WGNodeData explicitGenerationAudio = null;
        if (g.UserInput.TryGet(T2IParamTypes.VideoAudioInput, out AudioFile _))
        {
            explicitGenerationAudio = g.CurrentMedia.AttachedAudio;
        }
        string initDataType = g.CurrentMedia.IsLatentData ? WGNodeData.DT_LATENT_IMAGE : WGNodeData.DT_IMAGE;
        g.CurrentMedia = g.CurrentMedia.WithPath(ClonePath(g.CurrentMedia.Path), initDataType);
        ClearVideoMetadata(g.CurrentMedia, explicitGenerationAudio);

        if (g.UserInput.TryGet(T2IParamTypes.Video2VideoCreativity, out _))
        {
            g.UserInput.Remove(T2IParamTypes.Video2VideoCreativity);
            Logs.Info("Ignored Video2Video Creativity because video continuation uses only the selected final context frames.");
        }
        Logs.Info(useStreamingMerge
            ? $"Prepared the Init Image video's final {contextFrames} frame(s) with the streaming continuation path."
            : $"Prepared the Init Image video's final {contextFrames} frame(s) with SwarmUI's frame-batch fallback path.");
    }

    private static void ApplyH3ContextGuide(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator g = genInfo.Generator;
        if (!States.TryGetValue(g, out ContinuationState state) || state.ContextFrames == 1)
        {
            return;
        }
        if (genInfo.VideoModel.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatMiniMaxH3.ID
            || genInfo.Vae is null
            || genInfo.PosCond is null
            || g.CurrentMedia is null)
        {
            throw new SwarmUserErrorException(
                "Multi-frame video continuation could not attach its MiniMax H3 context guide. " +
                "Check the selected Video Model and backend version.");
        }

        // SwarmUI's regular H3 setup adds the still last frame here. Remove only
        // that first-frame input, preserving an optional user-selected end frame.
        string conditioningNodeId = $"{genInfo.PosCond[0]}";
        if (g.Workflow.TryGetValue(conditioningNodeId, out JToken token)
            && token is JObject conditioningNode
            && $"{conditioningNode["class_type"]}" == "SwarmMiniMaxH3AddKeyframes"
            && conditioningNode["inputs"] is JObject conditioningInputs)
        {
            conditioningInputs.Remove("first_frame");
        }

        string guide = g.CreateNode("MiniMaxH3AddGuide", new JObject()
        {
            ["positive"] = ClonePath(genInfo.PosCond),
            ["vae"] = ClonePath(genInfo.Vae.Path),
            ["latent"] = ClonePath(g.CurrentMedia.Path),
            ["image"] = ClonePath(state.ContextImages),
            ["frame_idx"] = 0
        });
        genInfo.PosCond = WorkflowGenerator.NodePath(guide, 0);
        Logs.Info($"Attached {state.ContextFrames} final video frames with MiniMax H3's native guide.");
    }

    private static int ParseContextFrames(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames)
            || frames is not (1 or 5 or 22 or 39 or 56))
        {
            throw new SwarmUserErrorException(
                "Continuation Context Frames must be 1, 5, 22, 39, or 56.");
        }
        return frames;
    }

    private static void MergeFinalVideo(WorkflowGenerator g)
    {
        if (!States.TryGetValue(g, out ContinuationState state))
        {
            return;
        }
        try
        {
            WGNodeData generatedVideo = g.CurrentMedia?.AsRawImage(g.CurrentVae);
            if (generatedVideo is null || generatedVideo.DataType != WGNodeData.DT_VIDEO)
            {
                throw new SwarmUserErrorException("Continue From Last Video Frames did not receive a generated video to append. Check the selected video model and video settings.");
            }

            if (state.UseStreamingMerge)
            {
                SaveStreamingContinuation(g, state, generatedVideo);
                return;
            }

            JToken outputFps = generatedVideo.FPS?.DeepClone() ?? new JValue(g.Text2VideoFPS());
            string generatedWidth = g.CreateNode("SwarmImageWidth", new JObject()
            {
                ["image"] = ClonePath(generatedVideo.Path)
            });
            string generatedHeight = g.CreateNode("SwarmImageHeight", new JObject()
            {
                ["image"] = ClonePath(generatedVideo.Path)
            });
            string scaledOriginal = g.CreateNode("ImageScale", new JObject()
            {
                ["image"] = ClonePath(state.OriginalVideo.Path),
                ["width"] = WorkflowGenerator.NodePath(generatedWidth, 0),
                ["height"] = WorkflowGenerator.NodePath(generatedHeight, 0),
                ["upscale_method"] = "lanczos",
                ["crop"] = "disabled"
            });
            string resampledOriginal = g.CreateNode("SwarmVideoResampleFPS", new JObject()
            {
                ["images"] = WorkflowGenerator.NodePath(scaledOriginal, 0),
                ["fps_in"] = state.OriginalVideo.FPS?.DeepClone() ?? outputFps.DeepClone(),
                ["fps_out"] = outputFps.DeepClone(),
                ["method"] = "linear"
            });

            string generatedFrameCount = g.CreateNode("SwarmCountFrames", new JObject()
            {
                ["image"] = ClonePath(generatedVideo.Path)
            });
            string appendedFrameCount = g.CreateNode("SwarmIntAdd", new JObject()
            {
                ["a"] = WorkflowGenerator.NodePath(generatedFrameCount, 0),
                ["b"] = -state.ContextFrames
            });
            string generatedWithoutBoundaryFrame = g.CreateNode("ImageFromBatch", new JObject()
            {
                ["image"] = ClonePath(generatedVideo.Path),
                ["batch_index"] = state.ContextFrames,
                ["length"] = WorkflowGenerator.NodePath(appendedFrameCount, 0)
            });
            string joinedVideo = g.CreateNode("ImageBatch", new JObject()
            {
                ["image1"] = WorkflowGenerator.NodePath(resampledOriginal, 0),
                ["image2"] = WorkflowGenerator.NodePath(generatedWithoutBoundaryFrame, 0)
            });

            WGNodeData mergedVideo = generatedVideo.WithPath(WorkflowGenerator.NodePath(joinedVideo, 0), WGNodeData.DT_VIDEO);
            mergedVideo.FPS = outputFps;
            mergedVideo.Frames = null;
            WGNodeData generatedAudio = TrimGeneratedAudio(g, generatedVideo, state.ContextFrames);
            mergedVideo.AttachedAudio = AppendAudio(g, state.OriginalVideo.AttachedAudio, generatedAudio,
                state.SourceDuration, WorkflowGenerator.NodePath(resampledOriginal, 0), outputFps);
            g.CurrentMedia = mergedVideo;

            RemoveAutomaticOutput(g, "9");
            RemoveAutomaticOutput(g, "30");
            g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, "9");
            Logs.Info($"Joined the Init Image video after removing {state.ContextFrames} generated context frame(s).");
        }
        finally
        {
            States.Remove(g);
        }
    }

    private static void SaveStreamingContinuation(WorkflowGenerator g, ContinuationState state, WGNodeData generatedVideo)
    {
        WGNodeData generatedAudio = TrimGeneratedAudio(g, generatedVideo, state.ContextFrames);
        JObject inputs = new()
        {
            ["source_video"] = ClonePath(state.SourceVideoPath),
            ["generated_images"] = ClonePath(generatedVideo.Path),
            ["fps"] = generatedVideo.FPS?.DeepClone() ?? new JValue(g.Text2VideoFPS()),
            ["format"] = g.UserInput.Get(T2IParamTypes.VideoFormat, "h264-mp4"),
            ["ffmpeg_path"] = state.FFmpegPath,
            ["source_duration_hint"] = state.SourceDuration ?? 0,
            ["skip_frames"] = state.ContextFrames
        };
        if (generatedAudio is not null)
        {
            inputs["generated_audio"] = ClonePath(generatedAudio.Path);
        }

        RemoveAutomaticOutput(g, "9");
        RemoveAutomaticOutput(g, "30");
        g.CreateNode("SwarmInitVideoContinuationSave", inputs, "9");
        Logs.Info($"Streaming merge will remove {state.ContextFrames} generated context frame(s) before appending with FFmpeg.");
    }

    private static WGNodeData TrimGeneratedAudio(WorkflowGenerator g, WGNodeData generatedVideo, int contextFrames)
    {
        WGNodeData audio = DecodeAudio(g, generatedVideo.AttachedAudio);
        if (audio is null || contextFrames == 1)
        {
            // Preserve version 1.2's exact single-frame audio behavior.
            return audio;
        }
        double fps = generatedVideo.FPS is null
            ? g.UserInput.Get(T2IParamTypes.VideoFPS, 24)
            : Convert.ToDouble(generatedVideo.FPS, CultureInfo.InvariantCulture);
        int totalFrames = generatedVideo.Frames
            ?? WorkflowGenerator.MiniMaxH3AlignFrames(g.UserInput.Get(T2IParamTypes.VideoFrames, 124));
        if (!double.IsFinite(fps) || fps <= 0 || totalFrames <= contextFrames)
        {
            throw new SwarmUserErrorException(
                "The generated continuation is not longer than its selected video-frame context.");
        }
        string trimmed = g.CreateNode("TrimAudioDuration", new JObject()
        {
            ["audio"] = ClonePath(audio.Path),
            ["start_index"] = contextFrames / fps,
            ["duration"] = (totalFrames - contextFrames) / fps
        });
        return audio.WithPath(WorkflowGenerator.NodePath(trimmed, 0), WGNodeData.DT_AUDIO);
    }

    private static WGNodeData AppendAudio(WorkflowGenerator g, WGNodeData sourceAudio, WGNodeData generatedAudio,
        double? sourceDuration, JArray sourceImages, JToken fps)
    {
        sourceAudio = DecodeAudio(g, sourceAudio);
        generatedAudio = DecodeAudio(g, generatedAudio);
        if (sourceAudio is null)
        {
            if (generatedAudio is not null
                && !WorkflowGenerator.RestrictCustomNodes
                && g.Features.Contains(StreamingFeature))
            {
                string aligned = g.CreateNode("SwarmInitVideoPrependSourceSilence", new JObject()
                {
                    ["audio"] = ClonePath(generatedAudio.Path),
                    ["source_images"] = ClonePath(sourceImages),
                    ["fps"] = fps.DeepClone(),
                    ["source_duration_hint"] = sourceDuration ?? 0
                });
                return generatedAudio.WithPath(WorkflowGenerator.NodePath(aligned, 0), WGNodeData.DT_AUDIO);
            }
            return generatedAudio;
        }
        if (generatedAudio is null)
        {
            return sourceAudio;
        }
        if (sourceDuration.HasValue)
        {
            string ensured = g.CreateNode("SwarmEnsureAudio", new JObject()
            {
                ["audio"] = ClonePath(sourceAudio.Path),
                ["target_duration"] = sourceDuration.Value
            });
            string trimmed = g.CreateNode("TrimAudioDuration", new JObject()
            {
                ["audio"] = WorkflowGenerator.NodePath(ensured, 0),
                ["start_index"] = 0,
                ["duration"] = sourceDuration.Value
            });
            sourceAudio = sourceAudio.WithPath(WorkflowGenerator.NodePath(trimmed, 0), WGNodeData.DT_AUDIO);
        }
        string concatenated = g.CreateNode("AudioConcat", new JObject()
        {
            ["audio1"] = ClonePath(sourceAudio.Path),
            ["audio2"] = ClonePath(generatedAudio.Path),
            ["direction"] = "after"
        });
        return sourceAudio.WithPath(WorkflowGenerator.NodePath(concatenated, 0), WGNodeData.DT_AUDIO,
            sourceAudio.Compat ?? generatedAudio.Compat);
    }

    private static WGNodeData DecodeAudio(WorkflowGenerator g, WGNodeData audio)
    {
        if (audio is null || audio.DataType == WGNodeData.DT_AUDIO)
        {
            return audio;
        }
        if (audio.DataType == WGNodeData.DT_LATENT_AUDIO && g.CurrentAudioVae is not null)
        {
            return audio.DecodeLatents(g.CurrentAudioVae, true);
        }
        return null;
    }

    private static double? GetSourceDuration(WorkflowGenerator g)
    {
        string key = $"{T2IParamTypes.InitImage.Type.ID}_duration";
        if (!g.UserInput.ExtraMeta.TryGetValue(key, out object durationRaw))
        {
            return null;
        }
        string durationText = Convert.ToString(durationRaw, CultureInfo.InvariantCulture);
        if (double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
            && double.IsFinite(duration)
            && duration > 0)
        {
            return duration;
        }
        return null;
    }

    private static void RegisterAdditionalVideoTypes()
    {
        MediaType.Register(new("mkv", "video/x-matroska", MediaMetaType.Video));
        MediaType.Register(new("avi", "video/x-msvideo", MediaMetaType.Video));
        MediaType.Register(new("m4v", "video/x-m4v", MediaMetaType.Video));
        MediaType.Register(new("mpeg", "video/mpeg", MediaMetaType.Video, ["mpg"]));
        MediaType.Register(new("ts", "video/mp2t", MediaMetaType.Video, ["m2ts", "mts"]));
        MediaType.Register(new("wmv", "video/x-ms-wmv", MediaMetaType.Video));
        MediaType.Register(new("flv", "video/x-flv", MediaMetaType.Video));
        MediaType.Register(new("ogv", "video/ogg", MediaMetaType.Video));
        MediaType.Register(new("3gp", "video/3gpp", MediaMetaType.Video));
    }

    private static string GetFFmpegPath()
    {
        string ffmpegPath = Utilities.FfmegLocation.Value;
        if (string.IsNullOrWhiteSpace(ffmpegPath) || ffmpegPath == "ffmpeg")
        {
            return ffmpegPath;
        }
        try
        {
            return Path.GetFullPath(ffmpegPath);
        }
        catch (Exception)
        {
            return ffmpegPath;
        }
    }

    private static JArray FindSourceVideoPath(WorkflowGenerator g, JArray path)
    {
        return FindSourceVideoPath(g, path, [], 0);
    }

    private static JArray FindSourceVideoPath(WorkflowGenerator g, JArray path, HashSet<string> visited, int depth)
    {
        if (path is null || path.Count != 2 || depth > 16)
        {
            return null;
        }
        string nodeId = $"{path[0]}";
        if (!visited.Add(nodeId)
            || !g.Workflow.TryGetValue(nodeId, out JToken token)
            || token is not JObject node)
        {
            return null;
        }
        if ($"{node["class_type"]}" == "SwarmLoadVideoB64")
        {
            return ClonePath(path);
        }
        if (node["inputs"] is not JObject inputs)
        {
            return null;
        }
        foreach (string inputName in new[] { "video", "image", "images" })
        {
            if (inputs[inputName] is JArray inputPath)
            {
                JArray result = FindSourceVideoPath(g, inputPath, visited, depth + 1);
                if (result is not null)
                {
                    return result;
                }
            }
        }
        return null;
    }

    private static void ClearVideoMetadata(WGNodeData media, WGNodeData attachedAudio)
    {
        media.Frames = null;
        media.FPS = null;
        media.AttachedAudio = attachedAudio;
    }

    private static void RemoveAutomaticOutput(WorkflowGenerator g, string nodeId)
    {
        if (g.Workflow.TryGetValue(nodeId, out JToken token)
            && token is JObject node
            && OutputNodeClasses.Contains($"{node["class_type"]}"))
        {
            g.Workflow.Remove(nodeId);
        }
    }

    private static void ReplaceNodeConnectionExcept(WorkflowGenerator g, JArray oldNode, JArray newNode, params string[] excludedNodeIds)
    {
        string oldNodeId = $"{oldNode[0]}";
        string oldOutputIndex = $"{oldNode[1]}";
        HashSet<string> excludedNodes = new(excludedNodeIds);
        foreach (JProperty property in g.Workflow.Properties())
        {
            if (excludedNodes.Contains(property.Name) || property.Value["inputs"] is not JObject inputs)
            {
                continue;
            }
            foreach (JProperty input in inputs.Properties())
            {
                if (input.Value is JArray connection
                    && connection.Count == 2
                    && $"{connection[0]}" == oldNodeId
                    && $"{connection[1]}" == oldOutputIndex)
                {
                    input.Value = ClonePath(newNode);
                }
            }
        }
        g.UsedInputs = null;
    }

    private static JArray ClonePath(JArray path)
    {
        return (JArray)path.DeepClone();
    }
}
