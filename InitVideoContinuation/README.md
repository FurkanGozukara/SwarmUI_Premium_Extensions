# Init Video Continuation

This SwarmUI workflow extension adds optional video-frame continuation controls
to the existing **Init Image** group.

## Usage

1. Select the desired model in the **Image To Video** group's **Video Model**
   input and keep the desired video settings.
2. Upload a video file to **Init Image**.
3. Enable **Continue From Last Video Frames**.
4. Select **1**, **5**, **22**, **39**, or **56** context frames, then generate.

The default **1** preserves the original behavior and works with the video
models already supported by the extension. The multi-frame choices use
ComfyUI's native `MiniMaxH3AddGuide` clip conditioning and therefore require a
MiniMax H3 Video Model. Multi-frame mode automatically bypasses **Init Image
Creativity** so that SwarmUI does not run an image-to-image sampler on an H3
audio-video model before applying the native clip guide. It then creates one
final video from:

```text
all input-video frames + generated-video frames after the replayed context
```

For one-frame mode, generated frame 0 is omitted as before. In multi-frame mode,
the first 5, 22, 39, or 56 generated frames reproduce the context clip and are
all omitted. Generated audio is trimmed by the same duration. The input video is rescaled to
the generated resolution and resampled to the output FPS while preserving its
duration. Existing input audio is kept; model-generated audio is appended when
the selected workflow produces it. When browser duration metadata is available,
the input track is padded or trimmed to the source-video boundary first.

For MP4, WebM, and ProRes output, the extension uses a bundled ComfyUI node to
stream the source through FFmpeg. Only the extracted context frames and the model's
newly generated frame batch need to be materialized as tensors. Source audio is
kept at the beginning, generated audio starts at the continuation boundary, and
silence is inserted on either side when just one segment has audio.
The frame-batch fallback likewise delays generated-only audio until after a
silent source segment. Visual duration is measured from the video stream rather
than the overall container, preventing audio encoder padding from adding a held
boundary frame.

## Video compatibility

The file chooser supports MP4, WebM, MOV, M4V, MKV, AVI, MPEG/MPG, TS/M2TS/MTS,
WMV, FLV, OGV, and 3GP containers. Decoding is performed by ComfyUI through
PyAV/FFmpeg, so the codec inside the container must also be available in the
installed backend. A browser preview is not required; formats Chromium cannot
preview are still submitted to the backend.

For the most predictable result, use an MP4 containing constant-frame-rate
H.264 video, yuv420p pixel format, and AAC audio. Variable-frame-rate, HDR,
unusual chroma formats, damaged files, or codecs missing from the backend may
fail or produce timing or color differences.

FFmpeg must be available in `PATH` or through SwarmUI's bundled ComfyUI
installation for the streaming path. If FFmpeg is unavailable, the backend did
not load the bundled node, **Video Boomerang** is enabled, or the output is WebP
or GIF, the extension automatically uses its original frame-batch merge. That
fallback decodes the complete source and generated videos into tensors and can
use substantial RAM or VRAM for long, high-resolution, or high-frame-rate clips.

The upload itself is still sent to ComfyUI as base64 data, so the compressed
source file occupies normal upload memory even on the streaming path. The large
decoded source-frame tensor is what the FFmpeg path avoids.

The checkbox intentionally ignores **Video2Video Creativity**. Multi-frame mode
also ignores **Init Image Creativity**; one-frame mode retains its original
behavior. Continuation is image-to-video from the selected final context, not
video-to-video over the complete source clip.
