# Fibo 2.0

The SECourses presets extend SwarmUI's native Fibo 2.0 (Bria) workflow so that
prompts, edit images and negative prompts reach the model exactly as Bria's own
pipeline sends them. Install with the SwarmUI installer/updater and restart
SwarmUI. Native Fibo 2.0 support is required in both SwarmUI and its ComfyUI
backend (2026-10-21 or newer), and the ComfyUI backend needs the SECoursesAudioTools
custom nodes (SECourses ComfyUI installer). No core source files are changed.

Enable **Fibo 2 Advanced** in the Fibo 2.0 group, or apply one of the Fibo 2.0
presets. The extension only acts on Fibo 2.0 models: a switch left on by a preset
is removed for every other model, in the browser and on the server.

## Why

SwarmUI's native Fibo 2.0 path encodes the prompt with its generic text encoder.
For Fibo 2.0 that encoder places the prompt inside the null caption's chat turn
(prompt text and the empty JSON caption in one message) and leaves the edit images
out of the text encoder, so edits mostly follow the attached latents and the
instruction is weakened. With **Fibo 2 Advanced** the prompt is encoded by
ComfyUI's native **Text Encode Fibo 2.0** node (through the SECoursesAudioTools
nodes), which follows Bria's pipeline: JSON caption cleaning, edit wrapping, the
images inside the text encoder and their reference latents. On top of that:

- **Negative prompts** written as JSON (such as Bria's text-to-image negative) are
  encoded as written, like Bria's pipeline; the ComfyUI node alone would clean
  them first (181 versus 163 tokens for the model card's negative).
- **Fibo 2 Sigma Shift** (default Official): Bria's pipeline raises the shift with
  the image size (1.15 at about 1024x1024, about 2 at 1536x1536) and starts the
  caption's text images at the same step at every size. ComfyUI default keeps
  1.15 at every size.
- **Fibo 2 Edit Size** (default Auto): an edit keeps the first image's own size
  from 1 to 4 MP (a 2 MP photo is edited at 2 MP), larger images are scaled down
  to 4 MP and smaller ones up to Fibo 2.0's trained ~1 MP size for their aspect
  ratio. Bria default edits at the trained ~1 MP size (less detail and more
  over-editing on 2 MP photos in our tests). Always use Width / Height center-crops a single image
  to the set resolution. Text to image always uses Width / Height.
- The text encoder defaults to `qwen3vl_4b_int8_convrot` (Fibo 2.0 Core Bundle)
  instead of a separate download.

The main controls still set seed, steps, CFG, sampler, scheduler, resolution,
batch size, variation seed and LoRAs.

## Presets

Use **Direct Apply** on a preset card. All four presets use
`qwen3vl_4b_int8_convrot` and `Flux/flux2-vae`; every file is in the downloader's
Fibo 2.0 Core Bundle.

| Preset | Recipe | RTX 5090 |
| --- | --- | --- |
| Dev - Advanced Text To Image | Official: Dev INT8, 50 Euler steps, simple scheduler, CFG 5, Bria's JSON negative prompt | 17 s at 1024x1024, 35 s at 1664x1248 |
| Dev - Advanced Image Edit | Official: Dev INT8, 50 steps, CFG 5, empty negative (the null caption) | 35 s at 1 MP |
| Turbo - Image Edit 4 Steps | Edit Turbo INT8, 4 steps, CFG 1 | 2.5 s at 1 MP, 5 s at 2 MP |
| Turbo - Text To Image 4 Steps | Edit Turbo INT8, 4 steps, CFG 1 | 1.5 s at 1024x1024, 3 s at 1664x1248 |

Bria made Edit Turbo for editing; it also renders text to image very well,
lettering included. About 1 MP is the trained size (1024x1024, 1424x800,
1248x832); 2 MP text to image also works well with both models.

## Prompting

Fibo 2.0 was trained on long structured JSON captions (Bria's VGL format:
`short_description`, `objects`, `background_setting`, `lighting`, `aesthetics`,
`photographic_characteristics`, `style_medium`, `text_render`, `context`,
`artistic_style`). A JSON caption gives the best control; the SECourses
`Fibo_2.0_Enchance_Prompt_Feed_For_LLMs.txt` guide turns an idea into one. Plain
text also works.

- A complete JSON caption is sent unchanged.
- `{red|blue}` in plain text makes a seeded random choice.
- `(word:1.2)` weights are removed (Qwen3-VL would read them as text); times and
  ratios such as `(10:30)` or `(4:3)` stay.
- Native Swarm tags such as `<segment:...>` keep working and stay out of the Fibo
  prompt.

## Editing

Drag 1 to 4 images into the prompt box and write the instruction, for example
`Turn @image1 into a watercolor painting` or
`Place the cat from @image1 in the basket of the bicycle from @image2`.
`@image1` (also `<image_1>`) refers to the first attached image. The Dev edit
preset uses the official empty negative prompt; a JSON caption of the result with
an `edit_instruction` key also works.

**Masked edit:** set an Init Image, paint a mask in the image editor (white = the
area to change) and write what to put there. Like Bria's training, the masked area
is greyed out and regenerated while the rest of the image is kept, at the image's
own size: Init Image Creativity is set to 1 and Mask Shrink Grow is turned off for
Fibo 2.0. A masked edit changes one image, so images attached to the prompt are
refused with a message.

## Prompt batches

Use **Add prompt**, import multiple `.txt` files, or import a folder. Each text file
is one complete prompt, JSON captions included. **Generate prompt batch** includes
the main prompt and runs the extra prompts sequentially with the same captured
settings and attached images. **Stop after current** stops submission of further
prompts. SwarmUI merges a selected (toggled, not applied) preset on the server
after your values, so a preset with its own prompt would replace every batch
prompt; the batch refuses to start in that case and asks you to Direct Apply the
preset instead.

## Validation

Validated on 2026-10-22 with SwarmUI `d74f8ce4` and ComfyUI `1a82e6d0` (frontend
1.57.1, Torch 2.13.0+cu130) on an RTX 5090.

- Generated workflows (19 checks): the four presets' models, recipes and Fibo
  nodes; the Fibo encoder feeding the sampler with no second set of reference
  latents; edit sizes (2 MP kept, 4 MP kept, a 427x640 photo raised to 832x1248,
  Bria default, fixed); two references with `@image2` and `<image_1>`; masked edit
  (whole image, creativity 1, Swarm's masked sampling and recomposite); masked edit
  plus attached images refused; shift choice; prompt conversion; INT8 text encoder
  default; another model ignores the Fibo parameters; Fibo 2 Advanced off leaves
  the native path untouched.
- Real generations of all four presets were pixel-identical to the SECourses
  ComfyUI Fibo 2.0 presets with the same seed. 2 MP edits, a masked sky and a
  masked hat, a two-image edit and 2 MP text to image rendered correctly; the same
  edit through the native path (Fibo 2 Advanced off) was clearly weaker (a flat
  cartoon instead of the requested polished anime illustration).
