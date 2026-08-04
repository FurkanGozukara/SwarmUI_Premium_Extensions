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

/// <summary>Continues an Init Image video from its last frame and joins the result back onto the source video.</summary>
public class InitVideoContinuationExtension : Extension
{
    private sealed class ContinuationState(WGNodeData originalVideo, double? sourceDuration, JArray sourceVideoPath, string ffmpegPath, bool useStreamingMerge)
    {
        public WGNodeData OriginalVideo = originalVideo;

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

    /// <summary>This extension is installed by the SECourses updater rather than a git clone, so metadata is set directly instead of read from git.</summary>
    public override void PopulateMetadata()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Turns an Init Image video into a simple last-frame continuation and saves the source and generated video as one result.";
        License = "MIT";
        Version = "1.2.0";
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
            "Continue Init Video From Last Frame",
            "When the Init Image is a video, use its last frame as the still init image, generate with the current video setup, then append generated frames starting at frame 1 so the shared boundary frame is not duplicated.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", Group: T2IParamTypes.GroupInitImage,
            OrderPriority: -3.1, IsAdvanced: true, DependNonDefault: T2IParamTypes.InitImage.Type.ID,
            Permission: Permissions.ParamVideo, ChangeWeight: 8));

        WorkflowGenerator.AddStep(PrepareLastFrameInit, -8.9);
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
            throw new SwarmUserErrorException("Continue Init Video From Last Frame requires a video in Init Image.");
        }
        if (initImage.Type.MetaType != MediaMetaType.Video)
        {
            throw new SwarmUserErrorException("Continue Init Video From Last Frame requires a supported video file in Init Image.");
        }
        if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel _))
        {
            throw new SwarmUserErrorException("Continue Init Video From Last Frame requires a model in the Image To Video group's Video Model input.");
        }
        if (g.BasicInputImage is null || g.BasicInputImage.DataType != WGNodeData.DT_VIDEO || g.CurrentMedia is null)
        {
            throw new SwarmUserErrorException("The Init Image video could not be loaded as video frames. Make sure the selected backend supports SwarmUI video loading.");
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

        States.Remove(g);
        States.Add(g, new ContinuationState(originalVideo, GetSourceDuration(g), sourceVideoPath, ffmpegPath, useStreamingMerge));

        string lastFrame;
        string frameCount = null;
        if (useStreamingMerge)
        {
            lastFrame = g.CreateNode("SwarmInitVideoLastFrame", new JObject()
            {
                ["video"] = ClonePath(sourceVideoPath)
            });
        }
        else
        {
            frameCount = g.CreateNode("SwarmCountFrames", new JObject()
            {
                ["image"] = ClonePath(originalVideoPath)
            });
            string lastFrameIndex = g.CreateNode("SwarmIntAdd", new JObject()
            {
                ["a"] = WorkflowGenerator.NodePath(frameCount, 0),
                ["b"] = -1
            });
            lastFrame = g.CreateNode("ImageFromBatch", new JObject()
            {
                ["image"] = ClonePath(originalVideoPath),
                ["batch_index"] = WorkflowGenerator.NodePath(lastFrameIndex, 0),
                ["length"] = 1
            });
        }
        JArray lastFramePath = WorkflowGenerator.NodePath(lastFrame, 0);

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
                ReplaceNodeConnectionExcept(g, originalVideoPath, lastFramePath, lastFrame);
            }
            else
            {
                ReplaceNodeConnectionExcept(g, originalVideoPath, lastFramePath, frameCount, lastFrame);
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
            Logs.Info("Ignored Video2Video Creativity because Init Video Continuation intentionally uses only the source video's last frame.");
        }
        Logs.Info(useStreamingMerge
            ? "Prepared the Init Image video's last frame with the streaming continuation path."
            : "Prepared the Init Image video's last frame with SwarmUI's frame-batch fallback path.");
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
                throw new SwarmUserErrorException("Continue Init Video From Last Frame did not receive a generated video to append. Check the selected video model and video settings.");
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
                ["b"] = -1
            });
            string generatedWithoutBoundaryFrame = g.CreateNode("ImageFromBatch", new JObject()
            {
                ["image"] = ClonePath(generatedVideo.Path),
                ["batch_index"] = 1,
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
            mergedVideo.AttachedAudio = AppendAudio(g, state.OriginalVideo.AttachedAudio, generatedVideo.AttachedAudio, state.SourceDuration);
            g.CurrentMedia = mergedVideo;

            RemoveAutomaticOutput(g, "9");
            RemoveAutomaticOutput(g, "30");
            g.CurrentMedia.SaveOutput(g.CurrentVae, g.CurrentAudioVae, "9");
            Logs.Info("Joined the Init Image video with generated frames 1 through the end; generated frame 0 was skipped to prevent a duplicate boundary frame.");
        }
        finally
        {
            States.Remove(g);
        }
    }

    private static void SaveStreamingContinuation(WorkflowGenerator g, ContinuationState state, WGNodeData generatedVideo)
    {
        WGNodeData generatedAudio = DecodeAudio(g, generatedVideo.AttachedAudio);
        JObject inputs = new()
        {
            ["source_video"] = ClonePath(state.SourceVideoPath),
            ["generated_images"] = ClonePath(generatedVideo.Path),
            ["fps"] = generatedVideo.FPS?.DeepClone() ?? new JValue(g.Text2VideoFPS()),
            ["format"] = g.UserInput.Get(T2IParamTypes.VideoFormat, "h264-mp4"),
            ["ffmpeg_path"] = state.FFmpegPath,
            ["source_duration_hint"] = state.SourceDuration ?? 0
        };
        if (generatedAudio is not null)
        {
            inputs["generated_audio"] = ClonePath(generatedAudio.Path);
        }

        RemoveAutomaticOutput(g, "9");
        RemoveAutomaticOutput(g, "30");
        g.CreateNode("SwarmInitVideoContinuationSave", inputs, "9");
        Logs.Info("Streaming merge will append generated frames 1 through the end with FFmpeg; generated frame 0 is skipped to prevent a duplicate boundary frame.");
    }

    private static WGNodeData AppendAudio(WorkflowGenerator g, WGNodeData sourceAudio, WGNodeData generatedAudio, double? sourceDuration)
    {
        sourceAudio = DecodeAudio(g, sourceAudio);
        generatedAudio = DecodeAudio(g, generatedAudio);
        if (sourceAudio is null)
        {
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
