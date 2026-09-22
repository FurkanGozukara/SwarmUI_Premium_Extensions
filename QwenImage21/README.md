# Qwen Image 2.1

Enable either unified preset. Main Model, Prompt, Negative Prompt, Seed, Steps,
CFG, Sampler, Scheduler and text-only Width/Height apply. The Qwen Image 2.1
group selects the mode, denoise, reference size, transparency, text encoder,
VAE and reference cache. Ordinary compatible LoRAs apply; old Qwen Turbo LoRAs
are a different architecture. Generic refiner and other model-specific controls
are not part of these workflows.

The unified switch only activates for a Qwen Image 2.1 main model. Switching to
an older model/preset keeps its normal workflow even if this switch remains
checked. For models missing cached architecture metadata, the extension uses
Swarm's native detector on the local checkpoint header.

- **Generate / reference edit:** no images generates from text. Attach images to
  the prompt to edit/combine them. `@image1`, `@image2`, etc. follow attachment
  order; drag cards to reorder or remove them. The separate Init Image is `@init`.
  This mode always uses denoise 1 and ignores masks.
- **Image to image:** provide Init Image and adjust **Qwen Unified Denoise**
  (default 0.85). This changes the entire image.
- **Inpaint masked area:** use Swarm's image editor or upload Init Image plus
  Mask Image. White mask pixels change; black pixels are restored exactly at
  the working resolution. Gray values blend with the original. For a strong
  replacement use an explicit instruction, full mask opacity and denoise 1.
- **Transparent:** requests generated transparency with the official prompt
  wording. Describe an isolated subject/cutout. PNG preserves alpha, including
  RGBA reference uploads. This is not deterministic background removal.

Up to 10 images including init match the source workflows. Missing reference
tokens, missing init and empty inpaint masks fail with an actionable message.
Reference Size targets approximately that size squared, preserves each aspect
ratio and rounds to 32 pixels. 0 keeps original sizes rounded to 32. Output
follows init, or the first reference; text-only uses main Width/Height.

The standard preset is 1024x1024, 25 steps, CFG 1, random seed. Ultimate Quality
is 2048x2048, 40 steps, CFG 3, seed 1. Both use Euler/simple, reference size 1024,
denoise 0.85, and lossless auto/default cache, exactly as their source workflows.
CFG 1 ignores the negative prompt; CFG 3 uses it. Ultimate Quality is one pass.

Install/update using the SwarmUI installer, which fetches this extension from
`FurkanGozukara/SwarmUI_Premium_Extensions` and the adapters from
`FurkanGozukara/SECoursesAudioTools`. Restart SwarmUI and ComfyUI afterward.
Remote ComfyUI backends also need updated SECoursesAudioTools. Native ComfyUI
must expose `TextEncodeQwenImage21` and `QwenImage21Cache`.
Models come from the downloader's **Qwen Image 2.1 Core Bundle**; this extension
does not download models. No ComfyUI or SwarmUI core code is changed.

The shared inference and mask implementation is documented in
[SECoursesAudioTools](https://github.com/FurkanGozukara/SECoursesAudioTools/blob/master/docs/qwen_image21.md).

## Validation, 2026-09-22

SwarmUI `163503eb` and native ComfyUI Qwen 2.1 ran on physical GPU 0 (RTX 5090).
Both recipes completed text generation, RGBA, one-reference editing,
two-reference composition, img2img and masked inpainting. Standard text/RGBA
output was 1024x1024; Ultimate was 2048x2048. Both recipes' reference/init tests
were 1024x1024, as specified by their reference size.

Chrome upload, reorder, removal, reference generation and painting/generating
with Swarm's actual mask editor were exercised. The browser inpaint changed a
blue rocket to green and retained all 821,833 unmasked pixels exactly, including
alpha. Both API inpaint recipes also had zero unmasked RGBA error.
Ten mixed RGB/RGBA references, init plus nine references, original-size
non-square images, CPU/GPU/off cache, int8/int4 cache and CFG-3 negative prompts
all executed successfully. Ten shared-node regression tests passed.

Version 1.0.1 fixes a reproduced preset-switching regression: a leftover enabled
Qwen switch could route FLUX through this workflow. Non-Qwen requests now remove
the Qwen activation parameter and backend feature requirement before model
loading and graph construction. The complete SECoursesAudioTools unit suite
passed (29 tests, including AvatarForever and H3 streaming).
`tests/check_preset_routing.py` compares existing preset graphs with the switch
off versus left on; it reports presets that require unavailable models/media.
On the isolated backend, 31 prior presets produced identical graphs with the
switch off or left on. The other 33 could not be validated there because their
baseline requests failed (missing models/nodes, custom samplers, or required
inputs); the test report retains their exact errors. A live FLUX generation with
the leftover switch succeeded, and Qwen 2.1 output stayed pixel-identical to its
pre-fix result with the same prompt and seed. This does not claim that every
older preset was executed end to end.

Reproduction evidence on the development machine is in
`temp/qwen21_swarm_validation` under the downloader workspace: scripts, source
graphs, generated PNGs, timings and pixel checks. It is not a runtime dependency.
