# MOSS-Image

The SECourses presets extend SwarmUI's native MOSS-Image workflow with the official
MOSS-Image guidance and the same prompt tools as the ComfyUI presets. Install with
the SwarmUI installer/updater and restart SwarmUI. Native MOSS-Image support is
required in both SwarmUI and its ComfyUI backend (2026-10-16 or newer). No core
source files are changed.

Enable **MOSS Image Advanced** in the MOSS-Image group, or apply one of the
MOSS-Image presets. The extension only acts on MOSS-Image models: a switch left on
by a preset is removed for every other model, in the browser and on the server.

## Quality controls

- **Negative Skip Layers** (default `22`): the official two-pass skip-layer
  guidance. The negative (CFG) branch runs without these zero-based DiT layers.
  `24` and `26` also work; an empty value turns it off, which looks more
  realistic but can let details collapse. **Skip Start / End** limit it to part of
  sampling (official 0 to 1). It needs CFG above 1.
- **SFT De-Band** (default on): MOSS-Image SFT draws faint vertical color stripes
  on skies, fog, walls and backdrops. The fix loads
  `MOSS_Image_SFT_DeBand_L37-41.safetensors` (MATLOWAI/MOSS-Image-SFT-DeBand,
  Apache-2.0, part of the MOSS-Image Core Bundle), which reverts SFT layers 37-41
  to the Base weights. It is applied only to SFT models (file name contains
  `sft`); Base models never get it.
- **Attention** (default PyTorch): with PyTorch attention the INT8 ConvRot files
  matched the official BF16 outputs closely. The INT8 files request Comfy Kitchen
  INT8 attention, about 20% faster, but INT8 weights plus INT8 attention changed
  images noticeably in our RTX 3090 tests and, with short prompts or an empty
  negative prompt, added a coarse woven texture and muddy colors. PyTorch
  attention removed it. Setting the attention here also protects the BF16 files:
  with a `--use-sage-attention` backend they produced black (NaN) images on an
  RTX 3090.
- **Score Rescaling** (default 1 = off): ComfyUI's Temporal Score Rescaling. The
  Fast 25 Steps preset uses 0.95 with ER-SDE; lower values give more detail,
  higher values smoother images.

The main controls still set seed, steps, CFG, sampler, scheduler, resolution,
batch size, variation seed and LoRAs.

## Presets

Use **Direct Apply** on a preset card. The four SFT presets and the two Base
presets use `qwen3.5_9b_int8_convrot`, `Flux/flux2-vae`, PyTorch attention and
SFT De-Band (skipped for Base); every file is in the downloader's MOSS-Image Core
Bundle.

| Preset | Recipe | 1024x1024, RTX 3090 |
| --- | --- | --- |
| Advanced Text To Image | Official: 50 Euler steps, normal scheduler, CFG 3, sigma shift 5, skip layer 22, negative tags `nsfw, worst quality, bad anatomy, watermark, jpeg artifact` | 48 s |
| Fast 25 Steps | 25 ER-SDE steps, simple scheduler, Score Rescaling 0.95, skip layers 22 and 24 from 10% to 70% | 24 s |
| Turbo 8 Steps | lodestones 8-step DMD LoRA at 0.7 (community work in progress), 8 Euler steps, CFG 1, no negative prompt | 6 s |
| Advanced Layout Boxes | Official recipe with three example layout boxes | 48 s |
| Base - Advanced Text To Image | Base model, official recipe (quality-first) | 53 s |
| Base - Advanced Layout Boxes | Base model, official recipe with the same layout boxes | 54 s |

Large lettering held up in all six. Fast occasionally misspells small text, and
Turbo can add stray signage; the 50-step presets are the most faithful.

**Base** is the raw model: more varied images, and the model that LoRAs trained
on Base expect; SFT gives clearer text grouping and more organized layouts. The
Base presets keep the official 50 steps: with the same seed, 80 steps reproduced
the 50-step image with no visible gain while taking about 60% longer. The 8-step
Turbo LoRA was trained on SFT, so there is no Base Turbo preset.

## Prompting

MOSS-Image reads the prompt with Qwen3.5 as plain language, tags or JSON, up to
about 4K tokens. Long, specific prompts work best; quote text that must appear in
the image and use real line breaks to control lines.

- `{red|blue}` makes a seeded random choice.
- `[red:blue:0.5]` switches halfway through sampling, `[red|blue]` alternates.
- `(word:1.2)` weights are removed: Qwen3.5 would read them as literal text, so
  describe emphasis in words. Times and ratios such as `(10:30)` or `(4:3)` stay.
- Other brackets stay literal text, and native Swarm tags keep working.
- A complete **JSON prompt** passes through unchanged (compact). Official compiled
  layouts (`abs_centralized`, `yxyx`) and structured captions both work.
- **Pixel drafts** from the official MOSS-Image prompt enhancer (`width`, `height`,
  `bbox_coord_space: "abs"`, `bbox_format: "xyxy"`) are compiled to the model's
  centred `yxyx` boxes exactly like its `compile_prompt.py`, scaled to the output
  size when that differs from the draft canvas.

## Layout boxes

Choose **Layout boxes** mode, then draw a box on the canvas; drag it to move it or
use its lower-right handle to resize. Select each box and describe it. Object and
Character boxes place things; Text boxes render their exact text (line breaks
allowed). The main prompt becomes the overall image caption. Coordinates follow
the output aspect ratio. Boxes guide composition; they are not inpainting masks.
The extension builds the layout with the native `CreateBoundingBoxes` and
`MOSSImageLayoutPrompt` nodes.

## Prompt batches

Use **Add prompt**, import multiple `.txt` files, or import a folder. Each text file
is one complete prompt, JSON layouts included. **Generate prompt batch** includes
the main prompt and runs the extra prompts sequentially with the same captured
settings. **Stop after current** stops submission of further prompts. The ordinary
Generate button still generates only the main prompt. SwarmUI merges a selected
(toggled, not applied) preset on the server after your values, so a preset with
its own prompt would replace every batch prompt; the batch refuses to start in
that case and asks you to Direct Apply the preset instead.

## Validation

Validated on 2026-10-17 with SwarmUI `40375eb0` and ComfyUI `ce1e922d` (both the
latest upstream commits that day) on an RTX 3090. The ComfyUI graph with these
settings reproduced official MOSS-Image Space examples from the same seed (same
composition, subject and details).

- Generated workflows of all six presets contain the expected nodes and values:
  AuraFlow shift 5, PyTorch attention, the de-band LoRA (not for Base models),
  skip-layer and Score Rescaling settings, the DMD LoRA and the layout nodes. A
  FLUX.2 Klein model with the same parameters got none of them.
- Prompt conversion matches the ComfyUI node's Python code exactly: pixel drafts
  at the final resolution, compact JSON pass-through, seeded choices, weight
  removal (times and ratios kept) and `[red:blue:0.5]` schedules.
- One real generation per preset rendered correctly (times above).
- Headless Chrome: the Import dialog imported the six presets; Direct Apply
  (the Base presets select the Base model and show the MOSS-Image controls),
  drawing, editing and deleting a box, hiding the group for a FLUX.2 model, the
  selected-preset batch guard and a two-prompt Turbo batch all worked without
  page errors. Both Base presets rendered pixel-identical images to the ComfyUI Base
  presets with the same seed.
