# MiniMax H3 References SwarmUI Extension

Furkan Gozukara's SwarmUI integration for the official ComfyUI MiniMax H3
`MiniMaxH3ReferenceToVideo` node.

It exposes the model's complete dynamic reference limits in SwarmUI:

- One prompt-adjacent uploader for images, videos, and audio
- Drag-and-drop and clipboard media support directly on the main prompt
- Up to 9 images through Prompt Images
- Up to 3 videos, resampled to 24 FPS and limited to 15 seconds
- Up to 3 standalone audio references
- Automatic soundtrack pairing for every reference video
- Visible `<Picture i>`, `<Video i>`, and `<Audio i>` attachment labels
- Mixed or single-modality reference generation
- Current-ComfyUI first/last-frame batching compatibility

Use a MiniMax H3 Ref2VA checkpoint and enable **MiniMax H3 References**. Click
**Add references** beside the main prompt, or drag/paste media onto the prompt.
All three media types appear together as prompt attachments. Mention their
displayed `<Picture i>`, `<Video i>`, and `<Audio i>` labels in the prompt.
