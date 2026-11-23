# Viggle Remake

Remake a video in one click: the people you name become your characters
(character swap), or objects, the background or the style change while the
people stay (video edit). Give it your video, up to four photos and one sentence
such as "Elon Musk (@image1) replaces the man in the dark blazer on the right".
The extension runs the whole Viggle Remake recipe on MiniMax H3 FL2VA: the video
is cut into render windows at its hard cuts, one keyframe per shot is painted
with the local MiniMax H3 Image model from your photos and text, Qwen3.5 9B writes
the prompt of every window, every window is rendered, and the windows are joined,
scaled to 1080p and saved as an MP4 with the original soundtrack in SwarmUI's
History.

## How to use it

1. Select a Viggle Remake main model:
   `viggle_remake_swap_pruned_int8_convrot` (character swap) or
   `viggle_remake_edit_pruned_int8_convrot` (video edit), both in
   `Models/diffusion_models`.
2. Switch **Viggle Remake Enabled** on.
3. Attach your video to the prompt (drag it into the prompt box). With the MiniMax
   H3 uploader you can trim it there ("Add a reference with trim"); the first
   attached video is used. A video set as the **Init Image** also works.
4. Attach up to four photos of the new characters to the prompt. They are
   `@image1` to `@image4` in their order; a still **Init Image** comes after the
   prompt images.
5. Write who becomes whom (swap), e.g. `Sam Altman (@image1) replaces the short man
   under the left sombrero. Elon Musk (@image2) replaces the tall man in the
   center.`, or the change (edit), e.g. `Remove the wind turbines and make it a
   snowy winter day.`
6. Generate. Start with a short clip (**Max Seconds**): each 14 second window
   takes several minutes.

**Seed** and ordinary **LoRAs** from SwarmUI's LoRA list apply (on the Viggle
Remake model). Width, Height, Steps, CFG Scale and Video FPS do not: the canvas
follows your video's aspect ratio and the output is 24 FPS.

## Prompt batches

Under **Viggle Remake Prompt List**, add extra prompts, or import `.txt` files or a
whole folder (each file is one prompt), then press **Generate prompt batch**. The
main prompt runs first, then each extra prompt, one video each, with the same
settings, video and photos. **Stop after current** ends the batch after the video
in progress.

## Options

- **Mode**: Auto (default) takes Swap for `viggle_remake_swap` files and Edit for
  `viggle_remake_edit` files. Character swap or Video edit force one.
- **Quality**: 1280x736 class (best quality) or 832x480 class (about 3x faster
  drafts). The canvas follows your video's aspect ratio.
- **Max Window Seconds**: longest single render. Auto picks 345 frames on 32 GB+
  cards, 175 on 24 GB and 124 below.
- **References Mode**: Auto paints the keyframes from your photos and text.
  "Finished reference frames" uses your attached images as already edited frames
  of your video (nothing is painted).
- **Outfit** (swap only): keep each person's original clothes, or take the whole
  person (face, hair, body and outfit) from the photo or text.
- **Start Seconds** and **Max Seconds** (0 = the whole video) select a part of the
  video, on top of the uploader's trim.
- **Paint Enhance**: the official MiniMax H3 Image edit enhancer (fast by default)
  writes the painter's caption for every keyframe. **Extra Instruction** adds words
  to every keyframe's instruction.
- **Prompt Mode**: Auto (Qwen3.5 9B watches each window and writes its prompt),
  Template (no language model) or "My text is the complete six-section prompt".
- **Output Size**: 1080p (default), the render size, or the source resolution.
- **Low VRAM** (on): exact-math memory saving for the transformer; same result.
- **Optimizations** (off): MiniMax H3 speed optimizer (First Block Cache and sparse
  attention); faster, the result changes slightly. **Sparse Attention** and
  **Cache Threshold** are under Display Advanced Options.
- **VAE Speedup** (on): faster tiled video decode.
- Advanced (Display Advanced Options): Max Cuts Per Window 3, Paint Steps 6, Paint
  Megapixels 2.0, Keyframe Until 0.9, Video CRF 16, and the model selectors.

## Required files

Existing local files only; nothing is downloaded by the extension.

| Selector | Default file |
| --- | --- |
| Main model | `diffusion_models/viggle_remake_swap_pruned_int8_convrot.safetensors` or `viggle_remake_edit_pruned_int8_convrot.safetensors` |
| Painter Model | `diffusion_models/minimax_h3_image_pruned_int8_convrot.safetensors` |
| Image VAE | `VAE/MiniMaxH3/minimax_h3_image_vae_fp16.safetensors` |
| Text Encoder | `text_encoders/qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors` |
| Video VAE | `VAE/MiniMaxH3/minimax_h3_video_vae_int8_convrot.safetensors` |
| Audio VAE | `VAE/MiniMaxH3/minimax_h3_audio_vae_fp32.safetensors` |
| Writer Model | `text_encoders/qwen3.5_9b_int8_convrot.safetensors` |

## Requirements

The updated `FurkanGozukara/SECoursesAudioTools` nodes (Viggle Remake Plan, Paint,
Prompt and Render) and `FurkanGozukara/ComfyUI-TeaCache` (Low VRAM, Optimizations,
VAE Speedup) on the ComfyUI backend; `Windows_Install_or_Update_SwarmUI.bat`
installs them. Restart SwarmUI and its ComfyUI backend after updating. SwarmUI uses
the same Viggle Remake nodes as our ComfyUI presets, so improvements to the nodes
reach both.

The controls appear only while a Viggle Remake model is selected (the file name
must contain `viggle_remake`). With any other main model, for example after
applying another preset, Viggle Remake stays off for that generation, and SwarmUI's
generic model preload is skipped while it is on, so no companion models are loaded
or downloaded twice.
