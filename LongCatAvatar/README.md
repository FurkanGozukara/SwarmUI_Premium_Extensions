# LongCat Avatar

Turn a voice recording or a song into a lip-synced video of a person talking or
singing, as long as the audio itself: a 10 second greeting, a 3 minute product
explanation or a whole song. Give it a photo and that person speaks; give it no
photo and the prompt describes who appears. The extension runs
LongCat-Video-Avatar 1.6 with the official recipe (8 distilled steps with the DMD
and OPSD LoRAs, 93-frame segments that continue seamlessly from each other) and
saves an MP4 with your original audio into SwarmUI's History.

## How to use it

1. Select a LongCat Video Avatar main model (the downloader saves
   `LongCat_Video_Avatar_1.6_Premerged_DMD_OPSD_INT8_ConvRot.safetensors`: INT8
   ConvRot with the DMD LoRA merged in and an exact in-place switch to the OPSD
   merge, so no LoRA work at run time; it runs the distilled mode with Mix Alpha 1.
   `LongCat_Video_Avatar_1.6_INT8_ConvRot` and `_BF16` also work, with every option).
2. Switch **LongCat Avatar Enabled** on.
3. Attach the voice or song to the prompt (drag it into the prompt box). The
   video length follows the audio; **Duration Seconds** and **Audio Start
   Seconds** select a part of it.
4. Optional: set an **Init Image**. It becomes the person and the first frame.
   Without it the video is made from the prompt and the audio.
5. Write the prompt: who the person is, where they are and that they speak or
   sing. An empty prompt uses a neutral talking-to-camera description.
6. Generate. Live previews show the newest frame while each segment is sampled.

**Size Mode** 480p bucket (default) or 720p bucket picks the official size closest
to the Init Image's aspect ratio (or to the Width x Height aspect without an
image). **custom width x height** uses SwarmUI's Width and Height, rounded to 16;
the image is center-cropped to fill it without distortion.

**Seed**, **Prompt**, **Negative Prompt** (used only with Distilled 8 Steps off;
empty means the official negative prompt) and ordinary **LoRAs** from SwarmUI's
LoRA list apply. Steps, CFG Scale, Video FPS (always 25) and refiner controls do
not.

## Prompt batches

Under **LongCat Avatar Prompt List**, add extra prompts, or import `.txt` files or
a whole folder (each file is one prompt), then press **Generate prompt batch**.
The main prompt runs first, then each extra prompt, one video each, with the same
settings, the same attached audio and the same Init Image: several takes, scenes
or styles of one performance in one click. **Stop after current** ends the batch
after the video in progress.

## Options

- **Distilled 8 Steps** (on): the official 1.6 recipe. Off runs the 50-step base
  model with **Base Steps**, **Text Guidance** and **Audio Guidance** (about 20x
  slower).
- **Distilled Step Count** 8 (official), 4 or 3: 4 and 3 sample positions 0, 2,
  4, 6 and 0, 3, 6 of the same 8-step schedule, 2x and 2.7x fewer transformer
  passes with a look close to 8 steps.
- **Mix Steps** 4 and **Mix Alpha** 1: the official split between the DMD LoRA
  (first steps) and the OPSD LoRA (last steps).
- **Shorten Last Segment** (off): samples the last segment only as long as the
  remaining audio needs; earlier segments are unchanged.
- **Vocal Separation** (on): music and background noise do not move the mouth;
  the saved video keeps the original audio.
- **Mouth Enhancement** (off): aligned CodeFormer restoration of the mouth only,
  with **Mouth Fidelity** 0.9 and **Mouth Blend** 0.7. **Save Original Too** keeps a
  copy without it for comparisons (in the ComfyUI backend's output folder). Needs
  `facerestore_models/codeformer.pth` and `insightface/models/buffalo_l/det_10g.onnx`
  on the backend.
- **Torch Compile** (off): faster repeated runs after a slower first run; needs
  Triton.
- Advanced (Display Advanced Options): Condition Cache (on, same result, faster
  long videos), Attention (auto; `int8 (comfy kitchen)` runs the long
  self-attention with comfy-kitchen's INT8 kernel), Frames Per Segment 93, Context Frames 13,
  Reference Index 10, Mask Frame Range 0, Live Preview, CRF 17, Encoding Preset
  fast, Weight Dtype.

Model selectors (existing local files only, nothing is downloaded): Text Encoder
`umt5_xxl_fp8_e4m3fn_scaled` (our downloader saves the UMT5 FP16 encoder under
that SwarmUI default name), VAE `Wan/wan_2.1_vae`, Whisper Model
`LongCat_Whisper_Large_V3_fp16.safetensors` (models/audio_encoders), DMD LoRA
`LongCat_Video_Avatar_1.6_DMD_LoRA` and OPSD LoRA
`LongCat_Video_Avatar_1.6_OPSD_LoRA`. Keep the DMD and OPSD files out of the normal
LoRA list: the recipe applies them step by step itself.

## Requirements

The updated `FurkanGozukara/SECoursesAudioTools` nodes on the ComfyUI backend
(`Windows_Install_or_Update_SwarmUI.bat` installs them); restart SwarmUI and its
ComfyUI after updating. For a remote ComfyUI backend, install SECoursesAudioTools
there. SwarmUI uses the same LongCat Avatar sampler node as our ComfyUI
presets, so improvements to the node reach both.

The controls appear only while a LongCat Video Avatar model is selected (the file
name must contain `LongCat_Video_Avatar`). With any other main model, for example
after applying another preset, LongCat Avatar stays off for that generation, and
SwarmUI's generic model preload is skipped while it is on, so no companion models
are loaded or downloaded twice.
