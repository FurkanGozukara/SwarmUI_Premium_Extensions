# Stable Video Infinity Pro

Adds the **Stable Video Infinity Pro** parameter group and a dedicated workflow
only when **SVI Pro Enabled** is on. Other generation paths are unchanged.

Requires the updated FurkanGozukara `ComfyUI-TeaCache` node pack. The premium
extension installer copies this folder into `SwarmUI/src/Extensions`; restart
SwarmUI and its ComfyUI backend after installation. This extension registers its
small MP4 output node through the normal extension custom-node path.

Import **Stable Video Infinity 2 Pro - Unified Long Video - Wan 22 - 260919**
from `Amazing_SwarmUI_Presets_v75.json`. Upload an **Init Image**, then enter
paragraph-separated clip prompts. Main Model is high noise; **SVI Low Noise
Model** selects its pair. Main Steps, CFG, Sampler, Scheduler, Width, Height and
Video FPS are used. For extension, enable **SVI Continue Video** and choose
**SVI Source Video** (a video in Init Image also works). An optional separate
Init Image overrides the source's identity anchor. Output is silent H.264 MP4.

The unified controller supports clip count, changing prompts, motion context,
paired SVI adapters, optional fast adapters, tiled VAE, source append and CRF.
Unrelated image/video options are not applied to this dedicated workflow.

Recipes, existing bundle filenames, conditioning and limitations have one home:
[SVI Pro node documentation](https://github.com/FurkanGozukara/ComfyUI-TeaCache/blob/main/docs/svi_pro.md).
