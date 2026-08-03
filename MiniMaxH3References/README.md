# MiniMax H3 References SwarmUI Extension

Furkan Gozukara's SwarmUI integration for the official ComfyUI MiniMax H3
`MiniMaxH3ReferenceToVideo` node.

It exposes the model's complete dynamic reference limits in SwarmUI:

- Up to 9 images through Prompt Images
- Up to 3 videos, resampled to 24 FPS and limited to 15 seconds
- Up to 3 standalone audio references
- Automatic soundtrack pairing for every reference video
- Mixed or single-modality reference generation
- Current-ComfyUI first/last-frame batching compatibility

Use a MiniMax H3 Ref2VA checkpoint, enable **MiniMax H3 References**, attach at
least one reference, and mention the generated `<Picture i>`, `<Video i>`, and
`<Audio i>` labels in the prompt.
