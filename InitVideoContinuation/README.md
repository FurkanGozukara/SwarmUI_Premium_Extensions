# Init Video Continuation

This SwarmUI workflow extension turns video continuation into one checkbox in
the existing **Init Image** group.

## Usage

1. Select the desired model in the **Image To Video** group's **Video Model**
   input and keep the desired video settings.
2. Upload a video file to **Init Image**.
3. Enable the advanced **Continue Init Video From Last Frame** checkbox.
4. Generate normally.

The extension uses the input video's last frame exactly as a still Init Image
would be used by the current workflow. It then creates one final video from:

```text
all input-video frames + generated-video frames 1 through the end
```

Generated frame 0 is deliberately omitted because it represents the same
boundary frame used to condition the new video. The input video is rescaled to
the generated resolution and resampled to the output FPS while preserving its
duration. Existing input audio is kept; model-generated audio is appended when
the selected workflow produces it. When browser duration metadata is available,
the input track is padded or trimmed to the source-video boundary first.

For MP4, WebM, and ProRes output, the extension uses a bundled ComfyUI node to
stream the source through FFmpeg. Only the extracted last frame and the model's
newly generated frame batch need to be materialized as tensors. Source audio is
kept at the beginning, generated audio starts at the continuation boundary, and
silence is inserted on either side when just one segment has audio.

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

The checkbox intentionally ignores **Video2Video Creativity**. Continuation is
image-to-video from one extracted frame, not video-to-video over the complete
source clip.
