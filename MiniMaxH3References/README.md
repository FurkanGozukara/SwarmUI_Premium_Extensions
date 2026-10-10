# MiniMax H3 References SwarmUI Extension

Furkan Gozukara's SwarmUI integration for the official ComfyUI MiniMax H3
`MiniMaxH3ReferenceToVideo` node.

## MiniMax H3 Mouth Pass (1.20.0)

The new **MiniMax H3 Mouth Pass** group (closed by default, directly after Video Face Inpainting) restores
only the mouth of every final frame with aligned CodeFormer and blends it back through a feathered mouth
mask: the recipe of AvatarForever's Mouth Enhancement and of the ComfyUI MiniMax H3 Lip Synch preset
(fidelity **0.9**, blend **0.7**). Frame count, timing, audio and every pixel outside the mouth stay the same.

- It runs on the final frames of any MiniMax H3 video: text, references, Image To Video and Init Audio
  generations, after Image To Video and Extend Video and before frame interpolation and the save
  (`SECodeFormerMouthImages`, workflow step 13).
- With **MiniMax H3 Long Lip Sync** the long node restores each decoded chunk itself before encoding
  (`SEH3LongLipSync` mouth inputs), because it writes its own MP4.
- It follows the MiniMax H3 model gating of Long Lip Sync, and the server ignores a leftover value when no
  MiniMax H3 model is selected.
- Needs SECoursesAudioTools with `SECodeFormerMouthImages`, `codeformer.pth` in Models/facerestore_models and
  `models/buffalo_l/det_10g.onnx` under Models/insightface (both in the MiniMax-H3 Core Bundle). Model and
  detector names are advanced options; nothing is downloaded automatically.

Init Audio outputs carry the original soundtrack again. SwarmUI's final save rebuilds the output save node
from the current media, which replaced the swap to the user's own audio with the VAE-decoded audio stream;
the swap now also runs after that final save.

## Long Lip Sync with other models and its token estimate (1.19.1)

**MiniMax H3 Long Lip Sync** and its window and overlap controls now follow the same model gating as
Init Audio: they are shown and sent only while a MiniMax H3 base model, or a MiniMax H3 Video Model in
Image To Video, is selected. Before, the checkbox stayed on after a Long Lip Sync preset, and every later
generation with another model (for example a FLUX preset) failed with "H3 Long Lip Sync requires Init
Audio and a MiniMax H3 model or Video Model". The server also ignores a leftover Long Lip Sync value when
no MiniMax H3 model is selected, so API clients and old presets are covered too.

The prompt token meter now estimates one Long Lip Sync window (at most **H3 Long Window Frames**) instead
of the whole Init Audio, which turned the meter red for any soundtrack longer than a few seconds although
each window is sampled separately.

## Saved RefMods and builder (1.19.0)

Enable **MiniMax H3 RefMods** and use **Add RefMod** or **Import files**. Every row
is optional, with its own enable checkbox, detail-retention strength and
`all` / `visual` / `audio` component filter. Cite `@refmod1`, `@refmod2`, etc. in
the prompt. A bundle may represent several numbered pictures, videos or audio
references. Normal prompt attachments can be used alongside saved files.

Files are stored on the **ComfyUI backend** in `models/refmods` (SwarmUI's sibling
`Models/refmods` is also discovered). Refresh after copying or building a file.
There is no fixed number of file slots; context size and VRAM limit practical
use. The picker shows their additional reference-token cost. Empty, disabled and
zero-strength rows preserve ordinary generation. Active files automatically select
the matching Ref2VA checkpoint and the supplied 4/8-step Ref2V turbo LoRA.

Apply **MiniMax H3 RefMod Builder - Images Video Audio - 261009**, attach media
above the prompt, set **RefMod Name**, and Generate. Only the backend video/audio
VAEs execute; a source preview returns to SwarmUI. **One character** groups photos
as one visual reference; **Separate references** keeps independent pictures.
Resolution 512 and compression **none** are the starting defaults. Compression
uses spatial pooling, not gradient training. Videos include their soundtracks.
Existing files receive numbered names. A portable bundle supports 256 members.

The updated FoleyExtension backend reads MiniMaxH3Mod v4/legacy standalone files,
v5 bundles and Fantastic files with embedded encoder frames. New bundles work in
both projects. Audio-only use should select the **audio** component of mixed
bundles. Both native long lip-sync presets support RefMods. This feature adds
reference conditioning, not a guarantee of exact identity or voice cloning.

The builder is included in `presets/H3_RefMods.json` and in the installer preset
pack. Use the current installer/update scripts to obtain both this extension and
the matching FoleyExtension nodes; no third-party RefMod pack is required.

## Prompt media uploader for every model (since 1.15.0)

The prompt attachment area is upgraded for every model, not only MiniMax H3. SwarmUI's own prompt media
(`promptimages` / `promptvideos` / `promptaudios`: the attachments used by LTX 2.5 Audio To Video, the LTX-2
voice reference, Kontext / IP-Adapter images and so on) stays exactly what the core sends; the extension adds
the missing interface around it (`Assets/secourses_prompt_media.js` and `.css`, loaded before the MiniMax H3
script):

- A toolbar above the prompt: **Add Image / Video / Audio** (several files at once, type detected from the
  MIME type or the extension), **Select From Inputs** (SwarmUI's inputs browser, persistent files), **Add With
  Trim** (one video or audio with an exact start/end window: audio is cut sample-accurately in the browser to a
  WAV, video is trimmed on the server through SwarmUI's video editor API and the saved `inputs/edited_video`
  file is attached), **Clear**, an attachment count and a context hint.
- Real cards instead of the native 128 px thumbnails: audio gets a waveform player (play/pause, click to seek,
  elapsed / total time), video a preview with hover controls and a mute toggle, images a fixed frame; every card
  shows its type and number (`Audio 1`), duration / resolution and filename, with the core's ☰ menu (Split
  Audio, Advanced Video Editor, Remove) and × on hover.
- ✂ on an attached video or audio card (since 1.16.0) trims that card in place: the same popup opens on the
  attached media, audio is re-sliced in the browser, video is re-trimmed on the server, and the card keeps its
  position with a `✂ start – end` note.
- Drag cards left/right to reorder attachments of the same type; the card order is the order SwarmUI sends.
- Paste video and audio files into the prompt box (the core only pasted images); drag and drop keeps working,
  with a clearer drop highlight.
- LTX 2.5 Audio To Video: while that parameter is on, the first audio card is marked **source audio** and the
  toolbar says which attachment the video follows, or warns when no audio is attached yet.

The MiniMax H3 reference uploader described below takes over while a MiniMax H3 model is selected; both
uploaders share the trim popup (`SECoursesTrimPopup`), and since 1.16.0 every MiniMax H3 video or audio
reference card has its own **✂ Trim** button: a video keeps its full data and only the backend trim window
changes (the popup preselects the current window; select the full range to clear it), an audio reference is
sliced again in the browser.

## Options under enable checkboxes are sent only while enabled (since 1.17.0)

SwarmUI's `DependNonDefault` rule should hide an option, and leave it out of the generation request, while the
checkbox it depends on is off. The core compares the checkbox value `false` with the default `"false"` using `==`,
which never matches, so every SECourses option group (AvatarForever, Qwen Unified, SVI Pro, LTX 2.3 Foley, LTX 2.5
Audio To Video, Licon MSR, MiniMax H3 References, Video Face Inpainting) was shown and sent with every generation.
SwarmUI then wrote about 85 of these options into every image as `unused_parameters`, and listed and SHA-256 hashed
the model files they name under `sui_models` (Gemma 3 12B, Qwen3-VL 8B, Wan 2.2 14B, ... in a Z-Image picture).

`Assets/secourses_param_dependencies.js` (loaded first; it serves every SECourses extension) applies the rule with
a text comparison for checkbox masters: those options stay hidden while their checkbox is off, and `getGenInput`
drops them, so Generate, grids, batch tools and the Comfy workflow import send only what the workflow uses. Turning a
feature on sends its options exactly as before. Other masters are compared exactly as the core does, so once the core
compares checkboxes correctly the script removes nothing more. API clients still send whatever they send.

## Video and Audio Shift (Advanced Sampling)

Since v1.14.0, **MiniMax H3 Video Shift** and **MiniMax H3 Audio Shift** are
available in SwarmUI's **Advanced Sampling** panel whenever a MiniMax H3 base
model or Image To Video model is active. Both controls are optional: with their
toggles off, SwarmUI keeps choosing the model defaults automatically (normally
video `12` and audio `3`).

Turn on either control to override only that stream. The explicit Video Shift
takes precedence over SwarmUI's generic Sigma Shift for MiniMax H3. The values
are applied to text-to-video, image-to-video, reference-to-video, Audio Only,
and any additional MiniMax H3 model load in the same workflow.

## Live token meter (beside the prompt)

Since v1.13.0 the reference toolbar shows the estimated packed-sequence length of the generation
you have configured, eg `Tokens ≈37.7k / 109k (35%) · 1344×768 · 124f · 5.2s · text to video`.
MiniMax H3 runs full attention over one packed sequence, `[text | references | audio | video]`, so
this number is what drives VRAM and speed. It updates live as you type, change the resolution,
frames / duration, Text2Video Frames or Video Frames, add or remove references (their real size,
duration and trim window are read in the browser), pick an Init Audio (its length becomes the
video length by default) or an Init Image / Video End Image with a MiniMax H3 Video Model, switch
Reference Image Size or Reference Max Seconds, or toggle Audio Only. Hover the meter for the
breakdown.

- The math reproduces ComfyUI's own `PackedLayout` (`comfy/ldm/minimax/model.py`) and the
  `MiniMaxH3ReferenceToVideo` reference sizing in `Assets/minimax_h3_tokens.js`, the same file
  the FoleyExtension gallery node uses inside ComfyUI, where it is verified against ComfyUI. Only
  the prompt's Qwen token count is a heuristic (within a few percent).
- MiniMax documents no hard token limit; the budget (109,062) is the packed length of the model's
  documented maximum output, 15 s at the 768×1344 canvas cap, the envelope the released checkpoints
  were tested for. Above it generation still runs, but slower, with more VRAM and outside the
  quality-tested range (the bar turns red); above 299,593 the SageAttention kernels overflow int32.
- The meter is model-agnostic: estimators register per model compat class
  (`PromptTokenEstimators` in `Assets/minimax_h3_prompt_references.js`), so a future model that
  supports references or has a token budget only needs its own entry. It also follows the Image To
  Video group, so an image model + MiniMax H3 Video Model shows the video stage's tokens.

## Init Audio (parameter group, above Init Image)

Since v1.12.0 an **Init Audio** group sits directly above **Init Image**. Upload or select one
soundtrack there and MiniMax H3 keeps that audio **exactly** as the video's audio track while it
generates the picture to match it: lipsync, action timing, ambience. It is the same idea as an
optional init image and it is *not* an audio reference: nothing has to be mentioned in the prompt,
just describe who speaks and how (eg *the woman speaks the words we hear, natural lip movements*);
the words themselves come from the audio.

- Works with a text-only prompt (FL2VA), with **MiniMax H3 References** (Ref2VA), and with the
  **Init Image + Image To Video** flow when the Video Model is a MiniMax H3 model (set Init Image
  Creativity to 0 as usual). It also stacks with H3 Optimizations, Low VRAM, and Video Face Inpainting (the
  face pass keeps the same locked audio).
- **Init Audio Match Duration** (default on) makes the video as long as the audio, rounded up to the
  model's 17k+5 frame grid at 24 FPS, ignoring Text2Video Frames / Video Frames. Turn it off to keep
  your own frame count: longer audio is cut, shorter audio is padded with silence.
  Since 1.18.1 this also resizes SwarmUI's own text-to-video, image-to-video and reference
  latents (`SwarmEmptyMiniMaxH3LatentAV`, `SwarmMiniMaxH3CollectReferences`); 1.18.0 left those
  at the frame count from the parameters.
- Behind the scenes the extension uses the `SECoursesMiniMaxH3InitAudio` node from
  [FurkanGozukara/FoleyExtension](https://github.com/FurkanGozukara/FoleyExtension): the soundtrack
  is encoded once with the audio VAE, locked into the joint audio/video latent with a nested noise
  mask (video is denoised, audio is kept exact at every step) and given to the model as a clean t=1.0
  audio guide through ComfyUI's native MiniMax H3 guide mechanism, mirroring the
  `multimodalart/minimax-h3-audio-to-video` Space. The saved MP4 carries your original audio
  (normalized to 32 kHz stereo) rather than a VAE round trip. The group is designed to host other
  audio-video architectures later; with a non-H3 model it errors clearly.

## Native SOL and long lip sync (1.18.0)

**MiniMax H3 Optimizations** replaces the historical 4x label while retaining the saved
`minimaxhxspeed` parameter ID. It wraps the shared backend's native ComfyUI SOL adapter,
optional FirstBlockCache and batched VAE decoding. SOL and caching have separate switches.
SOL `auto` benchmarks complete projected attention, checks finite output and requires a 5%
win; `enabled` skips that benchmark and `disabled` retains the incoming attention path.
Both SOL and cache reuse are approximate. There is no universal 4x speed claim.

**MiniMax H3 Long Lip Sync** under Init Audio generates the complete soundtrack automatically
with held latent overlap, native denoise masks and incremental video decoding. Its window and
overlap controls apply to both the reference and Image To Video paths. It reduces drift from
pixel re-encoding but does not guarantee indefinite identity or invisible joins.

See the [usage, presets and measured limits](LONG_LIP_SYNC.md). The long-mode presets default
SOL/cache off, select the matching Turbo 4 LoRA and use 832x1248 at 24 fps. Import them from
[presets/H3_Long_Lip_Sync.json](presets/H3_Long_Lip_Sync.json). The ordinary short Init Audio
path and its optional face pass remain available when Long Lip Sync is disabled.

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
smaller working set keeps more of each matmul in cache. It stacks with **MiniMax H3 Optimizations**.

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

Since v1.10.0 (faces selection since v1.11.0) a **Video Face Inpainting** group appears between Core Parameters and Text To
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

- **Face Inpaint Faces** (default `1`): which faces to refine. Faces are ranked by size - `1` = the biggest face
  in the clip (average detected face height over the whole clip; ties go to the face on screen longer), `2` = the
  second biggest, ... `1` behaves exactly like before, `2` refines only the second biggest, `1,3` faces 1 and 3, `all`
  every detected face. Spaces, `;` and case do not matter; missing ranks are skipped. Each selected face is refined in
  its own pass (time scales with the count), and a hallucination guard keeps neighbours' faces and any face H3 invents at
  their original pixels (frames where H3 rewrote the head are not pasted).
- **Face Inpaint Geometry Lock** (default on): re-aligns each regenerated crop onto the source
  face with dense optical flow before pasting, which removes the slight per-frame shaking /
  tilting the face pass otherwise introduces while keeping the regenerated detail.
- **Face Inpaint Size Aware Stitch** (default on): full refinement for faces up to 60 px,
  fade to the source between 60-180 px, original pixels at 180 px and above.
- **Face Inpaint Size Scaled Denoise** (default off) with editable start/end multipliers.
- Steps, sampler, scheduler, detection confidence, crop factor, canvas mode and identity
  tracking match the preset defaults (`20`, `res_multistep`, `simple`, `0.35`, `2.2`,
  `auto_capped_768`, on).
- **Face Inpaint Detector** (since v1.13.1) never blocks a generation: the dropdown always lists
  the tested default (marked "not downloaded yet" until `yolov9e-face-lindevs.pt` is in
  `Models/yolov8`), the value is not validated against SwarmUI's YOLO list (the UI sends it even
  while Video Face Inpainting is off), and when the pass actually runs a missing / stale name
  falls back to another available face model (logged) or is handed to the ComfyUI node, which
  reports clearly where to place the model. This fixes the "Invalid value for param Face Inpaint
  Detector - '' - must be one of: ``" error users without any YOLO model hit on every generation.

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
- Native SwarmUI prompt video/audio attachments are recognized by the same cards, token autocomplete, and Ref2VA workflow
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
- Type the token by hand: `@image1`, `@video2`, `@audio1` (case is ignored;
  aliases and harmless spacing such as `@IMG # 1`, `@ picture 1`, `@vid2`,
  and `@sound1` also work)

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
Typing those native labels directly also works, and they get the same colored pills.
Case, spacing, optional `#`, and the same aliases are normalized before the
prompt reaches MiniMax, so `<picture1>`, `<PICTURE 1>`, and `< image #01 >`
all become the exact model label `<Picture 1>`.
