# Init Video Continuation

This SwarmUI workflow extension turns video continuation into one checkbox in
the existing **Init Image** group.

## Usage

1. Select the desired model in the **Image To Video** group's **Video Model**
   input and keep the desired video settings.
2. Upload an MP4, WebM, or MOV file to **Init Image**.
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
the selected workflow produces it, otherwise SwarmUI pads the continuation with
silence during video encoding.

The checkbox intentionally ignores **Video2Video Creativity**. Continuation is
image-to-video from one extracted frame, not video-to-video over the complete
source clip.
