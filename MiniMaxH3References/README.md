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

## Reference uploader

It exposes the model's complete dynamic reference limits in SwarmUI:

- One prompt-adjacent uploader for images, videos, and audio
- Strict MiniMax H3 architecture scoping, leaving every other model's native prompt-image uploader unchanged
- Drag-and-drop and clipboard media support directly on the main prompt
- Up to 9 images through Prompt Images
- Up to 3 videos, resampled to 24 FPS and limited to 15 seconds
- Up to 3 standalone audio references
- Automatic soundtrack pairing for every reference video
- Colored `@image1`, `@video1`, and `@audio1` reference tokens with prompt-bar pills
- `@` autocomplete in the prompt with reference thumbnails
- Click any attachment card to insert its token at the cursor
- Automatic token renumbering when a reference is removed
- Mixed or single-modality reference generation
- Current-ComfyUI first/last-frame batching compatibility

## Referencing attachments in the prompt

Use any MiniMax H3 checkpoint and add references with the **Add references**
button, drag-and-drop, or paste. Every attachment card shows its own colored
token, eg `@image1`. Mention attachments in the prompt in any of these ways:

- Type `@` in the prompt box and pick from the autocomplete list
- Click an attachment card to insert its token at the cursor
- Type the token by hand: `@image1`, `@video2`, `@audio1` (aliases like
  `@img1`, `@pic1`, `@vid2`, `@sound1`, and `@image#1` also work)

Tokens render as colored pills in the prompt bar, matching their attachment
card's color. Tokens pointing at a missing attachment show in red. When you
remove an attachment, remaining tokens renumber automatically and tokens for
the removed attachment are cleaned out of the prompt.

At generation time the tokens are translated to the `<Picture i>`, `<Video i>`,
and `<Audio i>` labels the MiniMax H3 model expects (audio labels are offset
past video soundtracks automatically). Typing those legacy labels directly
still works and they get the same colored pills.
