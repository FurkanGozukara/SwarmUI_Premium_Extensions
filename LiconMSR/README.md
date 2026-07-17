# LTX 2.3 Licon MSR SwarmUI Extension

This small SwarmUI workflow extension wires the official `LiconMSR` image
composer into LTX 2.3 IC-LoRA conditioning. It intentionally uses the existing
LTXVideo nodes and ComfyUI core sampler nodes instead of introducing optional
workflow dependencies.

The repository-level `install_premium_extensions.py` installer deploys this
extension and a pinned copy of `ComfyUI-Licon-MSR` together.
