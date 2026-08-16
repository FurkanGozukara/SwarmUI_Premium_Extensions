# MiniMax H3 References SwarmUI Extension

Furkan Gozukara's SwarmUI integration for the official ComfyUI MiniMax H3
`MiniMaxH3ReferenceToVideo` node.

## MiniMax H3 4x Speed (Core Parameters checkbox)

Since v1.4.0 the extension also adds a **MiniMax H3 4x Speed** checkbox to SwarmUI's Core
Parameters. It appears only while a MiniMax H3 architecture model is selected *and* the
ComfyUI backend has the `MiniMaxH3SpeedOptimizer` node (shipped by
[FurkanGozukara/ComfyUI-TeaCache](https://github.com/FurkanGozukara/ComfyUI-TeaCache)).

Enabling it wraps the loaded model and video VAE with the NVlabs Sana `sol-engine`
acceleration line: FirstBlockCache step skipping, Sol-Attn sparse attention over the packed
audio-video sequence, and batched VAE tile decoding. Every technique is verified on the
active GPU at runtime — the sparse kernel is compiled, correctness-gated against dense
attention on the model's own tensors, and micro-benchmarked against the incumbent attention
backend — so whatever does not work or does not win on that specific GPU falls back to the
normal path automatically. RTX 30xx and newer are supported (Triton backend everywhere,
CuTe DSL on SM90/SM100/SM120 where installed).

Two advanced parameters tune it under *Advanced Sampling*: **MiniMax H3 Speed Cache
Threshold** (default 0.08, the NVlabs-advertised near-lossless policy; higher skips more
aggressively) and **MiniMax H3 Speed Sparse
Attention** (`auto` / `enabled` / `disabled`).

## MiniMax H3 Low VRAM (Core Parameters checkbox)

Since v1.9.0 a **MiniMax H3 Low VRAM** checkbox sits at the bottom of Core Parameters, on the
same visibility rule: a MiniMax H3 model selected *and* the `MiniMaxH3LowVRAM` node present on
the backend. It is off by default; switch it on when a resolution or duration otherwise runs
out of memory.

It releases the fused qkv buffer and the normed block input at their last use and runs the
feedforward in token chunks. Unlike the speed parameter this changes nothing about the
result: feedforward rows are independent and the INT8 activation quantizer works per row, so
the output is bit-for-bit identical — verified end-to-end, where a full generation with it on
decoded to pixel-identical and audio-identical output. It is not slower either, since the
smaller working set keeps more of each matmul in cache. It stacks with **MiniMax H3 4x
Speed**.

Measured on one real-geometry H3 block at 38k packed tokens on an RTX 5090: 3.84 GB peak
unpatched, 3.30 GB with this on.

**MiniMax H3 Low VRAM Max Saving**, which appears in Core Parameters directly beneath the Low
VRAM checkbox once that is ticked, additionally splits attention into head groups, taking the
same measurement down to 2.25 GB — roughly 40% instead of 15%.
That part is not output-preserving: heads are mathematically independent, but an attention
kernel picks its tiling and quantization scales from the tensor it is handed, so a head group
can round about one bf16 ulp differently than those heads do inside the whole tensor, and a
diffusion sampler amplifies that into a different — not worse — video. Whether it happens
depends on the backend *and* the sequence length (SageAttention measured exact up to 8k
tokens and not at 16k; xformers was the reverse), which is why it is an explicit choice
rather than something the extension guesses at.

## Video Face Inpainting (parameter group)

Since v1.10.0 a **Video Face Inpainting** group appears between Core Parameters and Text To
Video whenever a MiniMax H3 model is selected and the backend has the MiniMax H3 face nodes
(`MiniMaxH3FaceStitch` and friends from FurkanGozukara/ComfyUI-TeaCache). It is off by default
and costs nothing while off.

Turn on **Video Face Inpainting** and the extension appends the same second pass the SECourses
ComfyUI presets ship: a YOLO face model (`Face Inpaint Detector`, default
`yolov9e-face-lindevs.pt` from `Models/yolov8`) tracks the subject's face in every decoded
frame, the crops are regenerated on a 384-768 px canvas with the same H3 model as img2img
(**Face Inpaint Denoise**, default `0.55`; the main pass's audio latent is copied and frozen so
speech and lipsync are untouched), and the result is stitched back with feathered,
colour-matched blending. The main prompt is reused with an identity-preserving detail clause,
and when MiniMax H3 References are attached the face pass is conditioned on the same references.

- **Face Inpaint Geometry Lock** (default on): re-aligns each regenerated crop onto the source
  face with dense optical flow before pasting, which removes the slight per-frame shaking /
  tilting the face pass otherwise introduces while keeping the regenerated detail.
- **Face Inpaint Size Aware Stitch** (default on): full refinement for faces up to 60 px,
  fade to the source between 60-180 px, original pixels at 180 px and above.
- **Face Inpaint Size Scaled Denoise** (default off) with editable start/end multipliers.
- Steps, sampler, scheduler, detection confidence, crop factor, canvas mode and identity
  tracking match the preset defaults (`20`, `res_multistep`, `simple`, `0.35`, `2.2`,
  `auto_capped_768`, on).

The group is designed to host other video architectures later; today it errors clearly when
enabled with a non-H3 model.

## Audio-only quality mode

**MiniMax H3 Audio Only** uses the model's minimum 32x32 disposable video canvas,
decodes only the sampled audio latent, and returns lossless FLAC without decoding or
saving generated video. The quality-first preset uses 50 `res_multistep` / `beta`
steps at 24 FPS and leaves the speed optimizer off by default.

The extension selects FL2VA automatically for text-only prompts and Ref2VA when any
attachment is present. In audio-only mode, a reference video is decoded directly as
soundtrack audio; its frames are not decoded or passed to conditioning, and `@video1`
maps to `<Audio 1>`. **Reference Max Seconds** defaults to 15 per attachment, but this
is not a hard cap. Longer references and output above the quality-tested 4-15 second
range are allowed up to the native MiniMax H3 node limit and remain experimental.

## Reference uploader

It exposes the model's complete dynamic reference limits in SwarmUI:

- One prompt-adjacent uploader for images, videos, and audio
- Strict MiniMax H3 architecture scoping, leaving every other model's native prompt-image uploader unchanged
- Drag-and-drop and clipboard media support directly on the main prompt
- Up to 9 images through Prompt Images
- Up to 3 videos, resampled to 24 FPS in video mode and bounded by the user-selected Reference Max Seconds
- Up to 3 standalone audio references
- Automatic soundtrack pairing for every reference video
- Colored `@image1`, `@video1`, and `@audio1` reference tokens with prompt-bar pills
- Clear toolbar guidance that `<Audio 1>` addresses `@video1`'s soundtrack while
  `@audio1` remains the first standalone audio file
- `@` autocomplete in the prompt with reference thumbnails
- Click any attachment card to insert its token at the cursor
- Drag attachment cards left/right to reorder them; tokens renumber by position
- Removing or reordering attachments never edits the prompt text
- Mixed or single-modality reference generation
- Current-ComfyUI first/last-frame batching compatibility

## Add A Reference With Trim

The green **Add A Reference With Trim** button next to **Add References** adds one
video or audio reference through a trim popup: pick the file, preview it, and drag
the start/end handles (or type exact seconds) to select the window to use. The
timeline supports click-to-seek, set-start/set-end at the playhead, and a
window-only preview. Leaving the full range selected adds the file untrimmed.

- Audio is cut sample-accurately in the browser and attached as a WAV of just the
  selected window.
- Video keeps its full quality: the untouched file is uploaded and the selected
  window is applied on the backend by the exact ComfyUI `Video Slice` node (also
  honored for the soundtrack in Audio Only mode), so nothing is re-encoded in the
  browser. The card shows a `✂ start – end` badge, and windows follow their card
  when attachments are reordered.

## Referencing attachments in the prompt

Use any MiniMax H3 checkpoint and add references with the **Add References**
button, drag-and-drop, or paste. Every attachment card shows its own colored
token, eg `@image1`. Mention attachments in the prompt in any of these ways:

- Type `@` in the prompt box and pick from the autocomplete list
- Click an attachment card to insert its token at the cursor
- Type the token by hand: `@image1`, `@video2`, `@audio1` (aliases like
  `@img1`, `@pic1`, `@vid2`, `@sound1`, and `@image#1` also work)

Tokens render as colored pills in the prompt bar, matching their attachment
card's color. Attachment numbering follows card position: drag cards
left/right to reorder them and the tokens renumber accordingly. Your prompt
text is never modified when attachments are removed or reordered — a token
pointing at a missing attachment shows in red and is simply omitted at
generation time, so it never causes an error.

At generation time the tokens are translated to the `<Picture i>`, `<Video i>`,
and `<Audio i>` labels the MiniMax H3 model expects (audio labels are offset
past video soundtracks automatically). With `@video1` and standalone `@audio1`
attached, use `<Audio 1>` for `@video1`'s soundtrack; `@audio1` remains the
standalone file and is translated to `<Audio 2>`. In audio-only mode, video
tokens become audio tokens because only their soundtracks are used. Typing
those legacy labels directly still works and they get the same colored pills.
