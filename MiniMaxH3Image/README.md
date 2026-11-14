# MiniMax H3 Image

The SECourses MiniMax H3 Image presets extend SwarmUI's native MiniMax H3 Image workflow with the same prompt tools as
the ComfyUI presets. Install with the SwarmUI installer/updater and restart SwarmUI. Native MiniMax H3 Image support is
required in SwarmUI (2026-11-10 or newer) and the SECoursesAudioTools nodes on its ComfyUI backend. No core source files
are changed.

Apply one of the two presets, or enable **MiniMax H3 Image Advanced** in the MiniMax H3 Image group. The extension only
acts on MiniMax H3 Image models: a switch left on by a preset is removed for every other model, in the browser and on the
server.

## Prompt enhancer

MiniMax H3 Image was trained on long, detailed English captions. MiniMax's own service expands short prompts with its
prompt enhancer before generating; the extension runs the same official instructions (MiniMaxAI/MiniMax-H3-Image-PE) on a
local Qwen3.5 9B through the `SEH3ImageSwarmEnhance` node:

- **On: official enhancer with reasoning (best)**: write your idea in any language, a few words are enough. With prompt
  images it looks at them and writes the edit description.
- **On: fast enhancer without reasoning**: the same without the reasoning; a few seconds instead of about a minute.
- **Off: use my prompt exactly as written**: for long captions you wrote yourself (the MiniMax H3 Image LLM guide in the
  downloader makes ChatGPT, Claude or Gemini write one).

The prompt the model received is saved in each image's metadata as `h3_image_prompt_received`. The enhancer model is
`qwen3.5_9b_int8_convrot` (W4A8 also works); it is loaded only while the enhancer is on.

## Size

**Auto** (default) keeps the pixel count of Width x Height and takes the aspect ratio the enhancer chose, from tall
posters to 21:9 panoramas, rounded like MiniMax's API (16 MP: 4096 x 4096, 5472 x 3072, 6272 x 2688). Edits with the
enhancer off keep image 1's shape. **Exact** always uses Width x Height. Set the size with Megapixels and Aspect Ratio;
the presets use 16.78 MP (4096 x 4096). Image metadata lists the Width and Height you set; the image itself has the
final size.

## KJNodes empty image (text to image)

**MiniMax H3 Image KJNodes Empty Image** (off by default; on in the preset "MiniMax H3 Image - Text To Image 16 MP - KJNodes
Empty Image - 6 Steps - 261114") routes text to image through KJNodes' `MiniMaxH3PromptWeight` node in its "text with
empty image" mode: for the first part of the sampling (**Empty Image Start** 0 to **End** 0.25, the first 2 of the 6
steps, KJNodes' defaults) the model also sees an empty black picture, which on many images gives cleaner skin, hands and
fine detail. The composition differs from plain text to image, like a new seed. With prompt images attached the normal
edit runs. It needs ComfyUI-KJNodes (13 November 2026 or newer) on the ComfyUI backend; our ComfyUI installer installs it,
and without it the generation stops with a message. Switched off, the graph is exactly the one of version 1.0.0.

## Images and prompting

Attach up to 16 images to the prompt. `@image1`, `@image2`, ... follow attachment order and become the model's
`<Picture 1>`, `<Picture 2>` labels; `@image1` is the image that is edited. Removing a referenced image produces an
actionable error. Video and audio attachments are rejected.

- `{red|blue}` makes a seeded random choice.
- `(word:1.2)` weights are removed: the model reads them as literal text, so describe emphasis in words.
- Native Swarm tags keep working.

The main controls still set seed, steps, CFG, sampler, scheduler, resolution, images, batch size and LoRAs. Both presets
use the official recipe: 6 steps, Euler/simple, CFG 1.

## Prompt batches

Use **Add prompt**, import multiple `.txt` files, or import a folder. Each text file is one complete prompt.
**Generate prompt batch** includes the main prompt and runs the extra prompts sequentially with the same captured settings
and attached images. **Stop after current** stops submission of further prompts. The ordinary Generate button still
generates only the main prompt.

## Models

Every file is in the downloader's MiniMax-H3 Core Bundle: `minimax_h3_image_pruned_int8_convrot`, the shared
`qwen3vl_32b_minimax_h3_nvfp4_awq` text encoder, `MiniMaxH3/minimax_h3_image_vae_fp16` and `qwen3.5_9b_int8_convrot`. The
Low VRAM Bundle has the W4A8 model and enhancer; BF16 is in the catalog for original precision.
