using System;
using System.Collections.Generic;
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

    public override void OnInit()
    {
        ExtensionAuthor = "Furkan Gozukara";
        Description = "Adds the complete MiniMax H3 reference workflow and a unified prompt uploader for up to nine images, three videos, and three audio files.";
        License = "MIT";
        Version = "1.2.0";

        if (_initialized)
        {
            Logs.Info("MiniMax H3 References extension is already initialized.");
            return;
        }
        _initialized = true;

        ScriptFiles.Add("Assets/minimax_h3_prompt_references.js");
        StyleSheetFiles.Add("Assets/minimax_h3_prompt_references.css");
        RegisterParameters();
        WorkflowGenerator.AddStep(ApplyReferences, -7.9);
        WorkflowGenerator.AddStep(ReplaceLegacyBatchImages, 199);
        Logs.Info("MiniMax H3 complete image, video, and audio reference support initialized.");
    }

    private static void RegisterParameters()
    {
        T2IParamGroup group = new("MiniMax H3 References", Open: true, OrderPriority: 8,
            Description: "Add every image, video, and audio reference directly beside the main prompt. Video soundtracks are paired automatically. Use the labels shown on the prompt attachments in the prompt text.");
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
                "Internal slot populated by the MiniMax H3 prompt reference uploader. Refer to it with the <Audio i> label shown on its prompt attachment.",
                null, FeatureFlag: "comfyui", Group: group, OrderPriority: priority,
                DependNonDefault: Enabled.Type.ID, DoNotPreview: true));
        }
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
        if (g.UserInput.TryGet(T2IParamTypes.VideoAudioReference, out AudioFile legacyAudio))
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

        int frameCount = WorkflowGenerator.MiniMaxH3AlignFrames(g.UserInput.Get(T2IParamTypes.Text2VideoFrames, 124));
        JObject inputs = new()
        {
            ["clip"] = g.CurrentTextEnc.Path,
            ["vae"] = g.CurrentVae.Path,
            ["audio_vae"] = g.CurrentAudioVae.Path,
            ["prompt"] = g.UserInput.Get(T2IParamTypes.Prompt, ""),
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
