# AvatarForever

Enable the AvatarForever preset, attach audio to the prompt, and optionally supply
a still **Init Image**. **Duration Seconds = 0** uses the whole soundtrack; enter
10, 30, 60 or 300 to select that many seconds. Main Width/Height set the pixel
budget when Match Image Aspect is enabled, or the exact 32-pixel-grid canvas when
it is off. Prompt, Seed, Video FPS and ordinary LoRAs are honored.

Requires the updated `FurkanGozukara/SECoursesAudioTools` custom nodes. The extension
uses their sampler directly, so ComfyUI and SwarmUI share the same implementation.
The sampler's native video output works with Swarm's existing output handling;
no additional transport node is needed. Restart SwarmUI and ComfyUI after updating.
For a remote ComfyUI backend, install SECoursesAudioTools there.

**Mouth Enhancement** defaults on, with CodeFormer fidelity **0.9** and mouth
blend **0.7**. It restores the aligned mouth region before video export. Turn it
off to retain original decoded frames. The backend needs the existing
`facerestore_models/codeformer.pth` and
`insightface/models/buffalo_l/det_10g.onnx` files, plus the node pack's requirements.
The extension and presets have no dependency on development scripts.

The shared sampler's exact-output speed updates and simulated 8 GB memory test
are documented in the [node performance report](https://github.com/FurkanGozukara/SECoursesAudioTools/blob/master/docs/avatarforever.md#exact-output-speed-follow-up-and-8-gb-budget-test-2026-09-20).
Update SECoursesAudioTools and restart the ComfyUI backend to use them; existing
preset values and this extension's graph remain compatible.

All model selectors use existing local files; this extension never downloads
models. The released four-step schedule controls sampling; Main Steps, CFG,
negative prompt, refiner and unrelated video controls are not applied.

Model recipe, features and measured limits have one home:
[AvatarForever node documentation](https://github.com/FurkanGozukara/SECoursesAudioTools/blob/master/docs/avatarforever.md).
