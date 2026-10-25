# FLUX.3 Klein

The SECourses presets extend SwarmUI's native FLUX.3 workflow with the same
prompt and reference controls as the ComfyUI presets. Install with the SwarmUI
installer/updater and restart SwarmUI. Native FLUX.3 support is required in both
SwarmUI and its ComfyUI backend. No core source files are changed.

Choose **Text / optional references**, **Reference edit**, or **Layout / optional
references** in the FLUX.3 Klein group. Attach images to the main prompt using
the existing media uploader. `@image1`, `@image2`, etc. follow attachment order
and become the model's zero-based labels: `@image1` is `<ref_image_0>`,
`@image2` is `<ref_image_1>`. Up to ten references are supported.
Removing a referenced image produces an actionable error. Video/audio references
are rejected. Reference size is independent of output size, preserves aspect,
rounds to 16 pixels, and composites transparency on white.

The main controls still set seed, steps, CFG, sampler, scheduler, resolution,
batch size, variation seed and compatible LoRAs. A fixed seed repeats;
Swarm's seed -1 randomizes. Use the existing Megapixels extension for aspect/MP
resolution. The distilled presets use 16 steps, Euler/simple, CFG 1 and native
shift 1.93586. CFG 1 ignores negative conditioning. Only FLUX.3-compatible
LoRAs should be selected; unrelated models' acceleration patches are not enabled.

## Prompting

- `(ceramic:1.1)` weights a phrase.
- `[red:blue:0.5]` switches halfway through sampling.
- `[red|blue]` alternates across steps.
- `<break>` separates encoding chunks.
- `{red|blue}` makes a seeded random choice.
- Native Swarm tags, including `<random:red|blue>`, `<fromto[0.5]:red|blue>`
  and `<alternate:red|blue>`, remain available.

The extension converts only the caption using Swarm's own legacy-syntax parser.
The native layout node appends its JSON table afterward. The new Swarm advanced
encoder therefore keeps the complete table, including when the caption uses
schedules or alternation. No global parser or model-class settings are changed.

For a complete native caption plus JSON array from the companion prompt guide,
choose Text / optional references or Reference edit. A trailing table of rows
with `id` fields passes through without caption-syntax conversion. Do not combine
that table with the visual layout mode, which creates its own table.

## Layout

Draw a box on the canvas; drag it to move it or use its lower-right handle to
resize. Select each box and describe its object or text. Cite the numbered
`<obj_1>`, `<obj_2>` labels in the main caption. Removing/reordering boxes changes
those labels. Coordinates follow output aspect ratio. Boxes guide composition;
they do not guarantee exact pixel placement and are not inpainting masks.

Advanced JSON input is a list of objects with `bbox: [top, left, bottom, right]`
in 0..1 coordinates and `desc`. Optional `type: "text"` and `text` add literal
writing. The extension validates bounds, converts these UI coordinates to native
pixel boxes with metadata, and uses `CreateBoundingBoxes` -> `Flux3LayoutPrompt`.
The model-facing table then uses native integer coordinates from 0 to 1000.

## Prompt batches

Use **Add prompt**, import multiple `.txt` files, or import a folder. Each text
file is one complete prompt. **Generate prompt batch** includes the main prompt
and runs the extra prompts sequentially, preserving captured settings and image
attachments. **Stop after current** stops submission of further prompts. The
ordinary Generate button still generates only the main prompt.

The extension is gated on the selected model in both the browser and server.
Switching to another model leaves its normal workflow intact even if the FLUX
enable switch remains stored. The layout preset explicitly resets mode and all
shared FLUX settings when switching between presets.

## Preset artwork

The three presets carry their original PNG thumbnails. The extension's
permission-checked `FluxKleinSavePngPreset` route imports these bytes unchanged;
the standard Swarm preset importer otherwise only accepts and re-encodes JPEG.
The browser import flow and SECourses preset manager select this route only for
FLUX.3 advanced presets with embedded PNG previews. Export preserves the same
PNG bytes. Other preset imports use their normal route.

## Validation

Validated on 2026-10-15 with SwarmUI `967f005c` and ComfyUI `20b46065`,
using actual headed Google Chrome and the HQ INT8 model on an RTX 5090.
Text generation, scene-preserving editing, two-reference selection, visual layout,
advanced prompting, sequential prompt batches, file/folder import, stop-after-current
and PNG import/export passed. Twenty-three workflow/API checks cover limits,
reference numbering, duplicate images, native JSON, sizing and model-family gating.
Fixed-seed repetition produced identical pixels. Version 1.0.1 also recognizes the model family under
SwarmUI's current class name, `flux-3-image-klein` (the core remaps the former `flux.3-klein`); with 1.0.0 the
FLUX.3 Klein group stayed hidden on those cores. The BF16 option and actual
FLUX.3 LoRA weights were not separately generated in this validation; they use
Swarm's native model and LoRA path.

Reference selection remains prompt-dependent. When selecting one of several
references, state which image supplies the object and which other objects should
be absent. Layout boxes guide composition rather than enforcing pixel masks.
