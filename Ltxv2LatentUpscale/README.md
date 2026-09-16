# Ltxv2LatentUpscale

SwarmUI extension by Furkan Gozukara (SECourses). Installed by the SECourses SwarmUI updater from
`https://github.com/FurkanGozukara/SwarmUI_Premium_Extensions`.

It adds three LTX-2 features to SwarmUI's ComfyUI workflow generator:

1. **LTXV2 image-to-video latent upscale** - the Refine/Upscale stage of an LTX-2 image-to-video run uses
   the LTX latent spatial upscaler (`Refiner Upscale Method = latentmodel-...`) with the audio latent carried
   through both stages.
2. **LTX 2.3 Foley video-to-audio** - parameter group `LTX 2.3 Foley Audio`: freezes the Init Image video and
   generates only its synchronized audio (single window or automatic overlapping windows for long videos).
3. **LTX 2.5 Audio To Video with an optional image** - parameter group `LTX 2.5 Audio To Video` (below).

## LTX 2.5 Audio To Video

The same graph as the SECourses ComfyUI preset
`LTX2.5 Audio To Video - Optional Image - 2-Stage 8+3 Steps`: the official Lightricks two-stage distilled
audio-to-video recipe with the source audio frozen in both stages, plus the SECourses additions.

How to use in SwarmUI:

- Select an LTX 2.5 model as the main **Model** (the preset uses the distilled INT8 ConvRot model).
- Attach the audio (speech, singing, music) to the prompt: drag it onto the prompt box or use the attach
  button beside the prompt. `Video Audio Input` works too.
- Leave **Init Image** empty for audio + text to video, or put a still image in Init Image for image + audio
  to video.
- Enable **LTX 2.5 Audio To Video** (the preset does this) and generate.

What happens:

| Step | Detail |
| --- | --- |
| Audio | Trimmed with `Audio Start Seconds` / `Audio Duration Seconds` (0 = whole file), optional `Max Duration Seconds` cap, `Lead In Silence Seconds` prepended (0.25 s default). The frame count is the smallest `8k+1` that covers the audio; the audio is padded with silence to the exact video length, encoded with the LTX audio VAE and frozen (noise mask 0) in both stages. |
| Resolution | Width/Height are the final size, like every other workflow; with an image the image is center-cropped to that aspect. Turn `Auto Resolution From Image` on (off by default) to take the final size from the image aspect at the 1080p pixel budget instead (16:9 = 1920x1080, 9:16 = 1080x1920, 1:1 = 1408x1408, 2:3 = 1152x1728, 4:3 = 1600x1216), ignoring Width/Height. Stage 1 runs at ceil(final / 2) on the 32 px grid, the 2x latent upscale doubles it, and the decoded frames are center-cropped to the exact final size. |
| Image (optional) | Stage 1: `LTXVPreprocess` compression 18 + `LTXVImgToVideoInplace` strength 0.7. Stage 2: compression 0, strength 1.0. Both editable (advanced). |
| Identity anchors (with image) | The image is re-injected as keyframe guides every `Anchor Every Seconds` (4 s) at `Anchor Strength` (0.4), only mid-clip, snapped to the quietest nearby moment (`Snap Anchors To Quiet`), mouth/jaw excluded (`Protect Mouth`; insightface when installed, else the bundled Haar cascade). `End Anchor = full` ends the clip on the input pose. |
| Sampling | Stage 1: `Sampler` (euler_ancestral), `Base Sigmas` = official distilled 8-step schedule, CFG = `CFG Scale` (1). Stage 2 after the 2x latent upscale (`Refiner Upscale Method` latent model, default `ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors`): `Refiner Sampler` (euler), `Refine Sigmas` = official 3-step refinement. Same seed in both stages. |
| Output | Video-only tiled decode (VAE Tile Size params, defaults 512/64/128/32), crop to the exact size, the trimmed/padded source waveform is muxed into the video (`Video Format`). Frame interpolation is honored; frame trimming is not (it would desync the audio). |
| Torch compile | `LTX 2.5 A2V Torch Compile` (off): torch.compile the transformer for repeated runs with the same shapes. |

`Steps`, `Refiner Steps`, `Refiner Control Percentage` and `Text2Video Frames` are ignored in this mode: the
schedules are the sigma lists and the length comes from the audio. `Init Image Creativity` (and `Init Image
Noise` / `Reset To Norm` / masks) are ignored too - this mode replaces the core sampler, so how hard the
video sticks to the image is set by `Base Image Strength` (stage 1) and `Refine Image Strength` (stage 2).

### Bundled ComfyUI nodes

`ComfyNodes/SwarmLtx25AudioToVideo/` is added to the self-starting ComfyUI backend's custom node paths
(`OnPreInit`). It is a copy of `SECoursesAudioTools/ltx25_a2v_nodes.py` with Swarm-prefixed node ids, so it
never collides with a SECoursesAudioTools install in the same ComfyUI:

- `SwarmLTX25AudioPrepare` - audio trim/pad, frame count, frozen audio latent, mux audio
- `SwarmLTX25ImageCondition` - preprocess + in-place first-frame conditioning
- `SwarmLTX25IdentityAnchors` - keyframe identity anchors (mouth protected, quiet snapping)
- `SwarmImageFitToSize` - center crop to the exact size
- `SwarmLTX25TorchCompileOptional` - optional torch.compile wrapper

Keep the file in sync with `SECoursesAudioTools/ltx25_a2v_nodes.py` when the recipe changes. The nodes
require a ComfyUI recent enough for LTX-2.5 (`comfy_extras.nodes_lt`, `comfy_extras.nodes_audio`). For a
remote ComfyUI backend, copy the `SwarmLtx25AudioToVideo` folder into its `custom_nodes`. The backend reports
the feature `swarm_ltx25_audio_to_video` when the nodes are loaded; generation stops with a clear message
otherwise.

Why not the core Prompt Audio path: SwarmUI core turns a Prompt Audio on an LTX-2 model into
`LTXVReferenceAudio` (ID-LoRA speaker-identity transfer, which generates new audio in a similar voice). This
extension hides the attachment from that core step while Audio To Video is on and uses the audio itself.
