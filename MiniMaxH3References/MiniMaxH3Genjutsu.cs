using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace FurkanGozukara.SwarmExtensions.MiniMaxH3References;

/// <summary>MiniMax H3 Genjutsu: swap the performer of the first reference video (@video1) for the character of the first prompt
/// image (@image1). SAM 3.1 inverts only the performer, so H3 copies the motion, timing and camera but not the identity; the clip's
/// own soundtrack stays the soundtrack. The nodes come from FurkanGozukara/FoleyExtension (SECoursesH3Genjutsu*).</summary>
public partial class MiniMaxH3ReferencesExtension
{
    private static T2IRegisteredParam<bool> GenjutsuEnabled, GenjutsuMatchAspect, GenjutsuAutoDescribe, GenjutsuLockAudio;
    private static T2IRegisteredParam<string> GenjutsuTarget, GenjutsuBackground, GenjutsuSpeech, GenjutsuSpeechLanguage, GenjutsuSamModel, GenjutsuDescribeModel;
    private static T2IRegisteredParam<double> GenjutsuGuideMegapixels, GenjutsuThreshold;
    private static T2IRegisteredParam<int> GenjutsuMaskGrow;

    /// <summary>Feature id advertised when the ComfyUI backend has the Genjutsu nodes (shipped by FurkanGozukara/FoleyExtension).</summary>
    public const string GenjutsuFeatureId = "minimax_h3_genjutsu";

    /// <summary>SAM 3.1 checkpoint (Comfy-Org/sam3.1) and the local vision LLM of Auto Describe (Qwen Image 2.1's Qwen3-VL 8B encoder).</summary>
    public const string GenjutsuSamDefault = "sam3.1_multiplex_fp16.safetensors", GenjutsuDescribeDefault = "qwen3vl_8b_int8_convrot.safetensors";

    private sealed class GenjutsuState
    {
        public string Guide, AudioLock;
        public bool SoundtrackLogged;
    }

    private static readonly ConditionalWeakTable<WorkflowGenerator, GenjutsuState> GenjutsuStates = new();

    private static void RegisterGenjutsuParameters()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["SECoursesH3GenjutsuMotionGuide"] = GenjutsuFeatureId;
        T2IParamGroup group = new("MiniMax H3 Genjutsu", Open: true, OrderPriority: 8.1,
            Description: "Replace the person in a video with your character. Turn on MiniMax H3 References, attach the clip as @video1 and the new character as @image1, "
                + "then Generate. SAM 3.1 finds the performer in every frame and inverts only the performer, so MiniMax H3 copies the motion, timing, camera and lip movements "
                + "but not the identity. The result is as long as the clip (its trim window included) and keeps the clip's own soundtrack. Leave the prompt empty for the "
                + "automatic Genjutsu prompt, type short notes (eg outfit details), or paste a complete MiniMax H3 prompt from an LLM.");
        string dep = null;
        T2IRegisteredParam<T> Reg<T>(string name, string desc, string def, double prio, double min = 0, double max = 0, double step = 1, Func<Session, List<string>> values = null, bool advanced = false)
        {
            return T2IParamTypes.Register<T>(new(name, desc, def, Min: min, Max: max, Step: step, GetValues: values,
                IgnoreIf: def == "false" ? "false" : null, FeatureFlag: GenjutsuFeatureId, Group: group, OrderPriority: prio,
                DependNonDefault: dep, IsAdvanced: advanced, ChangeWeight: 2));
        }
        GenjutsuEnabled = Reg<bool>("MiniMax H3 Genjutsu", "Swap the performer of @video1 for the character of @image1. Needs MiniMax H3 References on, a MiniMax H3 model, the clip as the first reference video and the character as the first prompt image.", "false", -10);
        dep = GenjutsuEnabled.Type.ID;
        GenjutsuBackground = Reg<string>("Genjutsu Background", "'video (character replacement)' keeps the clip's location, lighting and framing and replaces only the performer. 'image (motion transfer)' keeps the character image's background and takes only the motion from the clip.",
            "video (character replacement)", -9.9, values: _ => ["video (character replacement)", "image (motion transfer)"]);
        GenjutsuTarget = Reg<string>("Genjutsu Target", "Who to replace, as SAM 3.1 text: 'person' for one person; 'man in red:1' or 'man:1' picks one of several. One phrase only.", "person", -9.8);
        GenjutsuAutoDescribe = Reg<bool>("Genjutsu Auto Describe", "When the prompt is not a complete MiniMax H3 prompt, a local vision LLM (Qwen3-VL 8B) looks at the character image and the clip and writes the character's outfit, the action, the setting and the lighting into the Genjutsu prompt. Without it, a generic prompt is used and parts of the performer's costume can carry over. Adds about 10-20 s.",
            "true", -9.7);
        GenjutsuSpeech = Reg<string>("Genjutsu Spoken Words", "Optional: the words spoken in the clip, for word-by-word lip sync. Ignored when the prompt is a complete MiniMax H3 prompt.", "", -9.6);
        GenjutsuSpeechLanguage = Reg<string>("Genjutsu Speech Language", "Language of the spoken words, eg English or Turkish.", "English", -9.5, advanced: true);
        GenjutsuLockAudio = Reg<bool>("Genjutsu Lock Clip Audio", "On (recommended): the clip's soundtrack is also locked into MiniMax H3's audio stream (exact audio, the picture is generated to follow it), which gave closer lip sync in our tests. Off: the soundtrack is only a reference. The saved video carries the clip's own audio either way.", "true", -9.45);
        GenjutsuMatchAspect = Reg<bool>("Genjutsu Match Clip Aspect", "On: keep the pixel area of your Width x Height but use the clip's aspect ratio, so the result is framed like the clip. Off: use Width and Height as they are.", "true", -9.4);
        GenjutsuGuideMegapixels = Reg<double>("Genjutsu Motion Guide Megapixels", "Size of the inverted motion guide. 0.15 is fast and keeps lip sync; 0.3-0.4 shows hands, face and background in more detail and costs more tokens.", "0.15", -9.3, 0.05, 1, 0.01);
        GenjutsuThreshold = Reg<double>("Genjutsu Detection Threshold", "SAM 3.1 detection score threshold. Lower it when the performer is missed in some frames.", "0.5", -9.2, 0.05, 0.95, 0.01, advanced: true);
        GenjutsuMaskGrow = Reg<int>("Genjutsu Mask Grow", "Grow the performer mask by this many pixels so hair and loose clothing edges are inverted too.", "0", -9.1, 0, 64, 1, advanced: true);
        GenjutsuSamModel = Reg<string>("Genjutsu SAM Model", "SAM 3.1 checkpoint in Models/Stable-Diffusion (MiniMax-H3 Core Bundle).", GenjutsuSamDefault, -9, advanced: true);
        GenjutsuDescribeModel = Reg<string>("Genjutsu Describe Model", "Qwen3-VL text encoder used by Auto Describe (MiniMax-H3 Core Bundle, also the Qwen Image 2.1 encoder).", GenjutsuDescribeDefault, -8.9, advanced: true);
    }

    /// <summary>Called by ApplyReferences before it creates the MiniMax H3 reference node: reference video 1 becomes the SAM 3.1 motion guide,
    /// the length, size and prompt come from the Genjutsu nodes, and the clip's soundtrack (cut to the length) stays paired with the guide.</summary>
    private static void ApplyGenjutsu(WorkflowGenerator g, JObject inputs, JArray clipFrames, JArray clipAudio)
    {
        if (!g.Features.Contains(GenjutsuFeatureId))
        {
            throw new SwarmUserErrorException("MiniMax H3 Genjutsu needs the updated FoleyExtension nodes on the ComfyUI backend. Run Windows_Install_or_Update_SwarmUI.bat and restart.");
        }
        if (g.UserInput.Get(AudioOnly, false))
        {
            throw new SwarmUserErrorException("MiniMax H3 Genjutsu makes a video. Turn MiniMax H3 Audio Only off.");
        }
        if (g.UserInput.TryGet(InitAudio, out AudioFile _))
        {
            throw new SwarmUserErrorException("MiniMax H3 Genjutsu keeps the clip's own soundtrack. Remove the Init Audio.");
        }
        if (clipFrames is null)
        {
            throw new SwarmUserErrorException("MiniMax H3 Genjutsu needs the clip to swap as the first reference video (@video1). Attach it beside the prompt.");
        }
        if (inputs["ref_images.ref_image_0"] is null && ActiveRefMods(g).Length == 0)
        {
            throw new SwarmUserErrorException("MiniMax H3 Genjutsu needs the new character as the first prompt image (@image1) or as a RefMod.");
        }
        string background = g.UserInput.Get(GenjutsuBackground, "video (character replacement)");
        string sam = g.CreateNode("CheckpointLoaderSimple", new JObject()
        {
            ["ckpt_name"] = g.UserInput.Get(GenjutsuSamModel, GenjutsuSamDefault)
        });
        string guide = g.CreateNode("SECoursesH3GenjutsuMotionGuide", new JObject()
        {
            ["images"] = clipFrames,
            ["sam3_model"] = WorkflowGenerator.NodePath(sam, 0),
            ["sam3_clip"] = WorkflowGenerator.NodePath(sam, 1),
            ["target"] = g.UserInput.Get(GenjutsuTarget, "person"),
            ["background"] = background,
            ["guide_megapixels"] = g.UserInput.Get(GenjutsuGuideMegapixels, 0.15),
            ["threshold"] = g.UserInput.Get(GenjutsuThreshold, 0.5),
            ["mask_grow"] = g.UserInput.Get(GenjutsuMaskGrow, 0),
            ["width"] = inputs["width"],
            ["height"] = inputs["height"],
            ["match_source_aspect"] = g.UserInput.Get(GenjutsuMatchAspect, true),
            ["audio"] = clipAudio,
            ["refine_iterations"] = 2
        });
        inputs["ref_videos.ref_video_0"] = WorkflowGenerator.NodePath(guide, 0);
        inputs["ref_video_audios.ref_video_audio_0"] = WorkflowGenerator.NodePath(guide, 5);
        inputs["length"] = WorkflowGenerator.NodePath(guide, 2);
        inputs["width"] = WorkflowGenerator.NodePath(guide, 3);
        inputs["height"] = WorkflowGenerator.NodePath(guide, 4);
        // The base sampler runs on the empty audio/video latent the core made at -9 from Text2Video Frames and Width x Height. Size it
        // like the guide too, or H3 samples 124 frames at the base size (a portrait clip came out landscape, a 4.5 s clip 5.2 s long).
        int sized = 0;
        foreach (string nodeClass in new[] { "EmptyMiniMaxH3LatentAV", "SwarmEmptyMiniMaxH3LatentAV" })
        {
            g.RunOnNodesOfClass(nodeClass, (_, node) =>
            {
                if (node["inputs"] is JObject latentInputs)
                {
                    latentInputs["length"] = WorkflowGenerator.NodePath(guide, 2);
                    latentInputs["width"] = WorkflowGenerator.NodePath(guide, 3);
                    latentInputs["height"] = WorkflowGenerator.NodePath(guide, 4);
                    sized++;
                }
            });
        }
        if (sized == 0)
        {
            throw new SwarmReadableErrorException("MiniMax H3 Genjutsu could not find the MiniMax H3 audio/video latent to size it like the clip.");
        }
        if (g.CurrentMedia is not null)
        {
            g.CurrentMedia.Frames = null; // the frame count now comes from the clip on the backend
        }
        JObject promptInputs = new()
        {
            ["prompt"] = inputs["prompt"],
            ["background"] = background,
            ["images"] = clipFrames,
            ["audio"] = WorkflowGenerator.NodePath(guide, 5),
            ["speech"] = g.UserInput.Get(GenjutsuSpeech, ""),
            ["speech_language"] = g.UserInput.Get(GenjutsuSpeechLanguage, "English")
        };
        if (inputs["ref_images.ref_image_0"] is JArray character)
        {
            promptInputs["character"] = character;
        }
        bool describe = g.UserInput.Get(GenjutsuAutoDescribe, true);
        if (describe)
        {
            string describer = g.CreateNode("CLIPLoader", new JObject()
            {
                ["clip_name"] = g.UserInput.Get(GenjutsuDescribeModel, GenjutsuDescribeDefault),
                ["type"] = "stable_diffusion",
                ["device"] = "default"
            });
            promptInputs["describe_clip"] = WorkflowGenerator.NodePath(describer, 0);
        }
        string prompt = g.CreateNode("SECoursesH3GenjutsuPrompt", promptInputs);
        inputs["prompt"] = WorkflowGenerator.NodePath(prompt, 0);
        GenjutsuStates.AddOrUpdate(g, new GenjutsuState() { Guide = guide });
        Logs.Info($"MiniMax H3 Genjutsu: @video1 becomes the SAM 3.1 motion guide ('{g.UserInput.Get(GenjutsuTarget, "person")}', {background}, {g.UserInput.Get(GenjutsuGuideMegapixels, 0.15)} MP), length from the clip, Auto Describe {(describe ? "on" : "off")}.");
    }

    /// <summary>Right before the base sampler (-5): lock the clip's soundtrack (cut to the output length) into the joint audio/video latent
    /// and give it to H3 as a clean audio guide, like Init Audio. A silent clip passes through unchanged.</summary>
    private static void ApplyGenjutsuAudioLock(WorkflowGenerator g)
    {
        if (!GenjutsuStates.TryGetValue(g, out GenjutsuState state) || !g.UserInput.Get(GenjutsuLockAudio, true))
        {
            return;
        }
        if (!g.Features.Contains(InitAudioFeatureId))
        {
            throw new SwarmUserErrorException("Genjutsu Lock Clip Audio needs the SECoursesMiniMaxH3InitAudio node on the ComfyUI backend. Update the FoleyExtension node package.");
        }
        if (g.CurrentMedia is null || g.CurrentMedia.DataType != WGNodeData.DT_LATENT_AUDIOVIDEO || g.CurrentAudioVae is null)
        {
            throw new SwarmReadableErrorException($"MiniMax H3 Genjutsu expected the MiniMax H3 audio/video latent and audio VAE but found {g.CurrentMedia?.DataType}.");
        }
        state.AudioLock = g.CreateNode("SECoursesMiniMaxH3InitAudio", new JObject()
        {
            ["positive"] = g.FinalPrompt,
            ["latent"] = g.CurrentMedia.Path,
            ["audio_vae"] = g.CurrentAudioVae.Path,
            ["init_audio"] = WorkflowGenerator.NodePath(state.Guide, 5),
            ["audio_conditioning"] = InitAudioConditioning
        });
        g.FinalPrompt = WorkflowGenerator.NodePath(state.AudioLock, 0);
        g.CurrentMedia = g.CurrentMedia.WithPath(WorkflowGenerator.NodePath(state.AudioLock, 1));
        Logs.Info("MiniMax H3 Genjutsu locked the clip's soundtrack into the audio stream.");
    }

    /// <summary>The Genjutsu output length (the clip's usable frame count), when Genjutsu built this generation.</summary>
    private static JToken GenjutsuLength(WorkflowGenerator g)
    {
        return GenjutsuStates.TryGetValue(g, out GenjutsuState state) ? WorkflowGenerator.NodePath(state.Guide, 2) : null;
    }

    /// <summary>Put the clip's own soundtrack (cut to the output length) on the saved video instead of the decoded audio stream; a silent
    /// clip keeps the generated audio. Runs after the base save (10) and after the core's final save (100), which rebuilds the save node.</summary>
    private static void UseGenjutsuSoundtrack(WorkflowGenerator g)
    {
        if (!GenjutsuStates.TryGetValue(g, out GenjutsuState state))
        {
            return;
        }
        int replaced = 0;
        g.RunOnNodesOfClass("SwarmSaveAnimationWS", (_, node) =>
        {
            if (node["inputs"] is not JObject inputs || inputs["audio"] is not JArray audioPath || $"{g.Workflow[$"{audioPath[0]}"]?["class_type"]}" != "VAEDecodeAudio")
            {
                return;
            }
            string fallback = g.CreateNode("SECoursesAudioFallback", new JObject()
            {
                ["audio"] = audioPath,
                ["override"] = state.AudioLock is null ? WorkflowGenerator.NodePath(state.Guide, 5) : WorkflowGenerator.NodePath(state.AudioLock, 2)
            });
            inputs["audio"] = WorkflowGenerator.NodePath(fallback, 0);
            replaced++;
        });
        if (replaced > 0 && !state.SoundtrackLogged)
        {
            state.SoundtrackLogged = true; // the final save (100) rebuilds the save node, so this runs twice
            Logs.Info($"MiniMax H3 Genjutsu: {replaced} output video(s) carry the clip's own soundtrack.");
        }
    }
}
