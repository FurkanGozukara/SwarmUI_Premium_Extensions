# Anima

Native Anima text-to-image and ElectronGirl reference adapter integration. Install through the managed SwarmUI updater; it installs the adapter fork and SECoursesAudioTools.

Download the Anima Core Bundle. It contains base v1.0 INT8 ConvRot, Qwen3 600M, the reference adapter and the existing shared Qwen-Image VAE. BF16 is available separately. Anima arithmetic uses BF16 even when storage is INT8.

Apply the text-to-image or reference preset. For transfer, enable Anima Reference and upload one still image in Anima Reference Image, or attach it to the prompt. The explicit reference control takes priority; otherwise the first prompt attachment is used. Write scene, pose and framing; identity and some style come from the image. Euler/simple, 30 steps, CFG 4.5, shift 3, adapter strength 1, reference guidance 4.5 and APG are the starting settings.

The normal prompt, negative, LoRA, resolution and seed controls remain available. Prompt batches accept added prompts, .txt files or a folder. Reference mode preserves the original main latent, so native image-to-image/masks and subsequent refiners remain in Swarm's workflow. The adapter targets the base sampler only; a separate refiner uses its native sampler. Fine accessories and strong painterly styles can drift.

The reference preset transfers at 1024x1024, then uses the native PostApply pixel-Lanczos 2x refiner for a 2048x2048 final image. Refiner Steps 40 with Control 0.3 gives a 12-step detail pass. The reference adapter is off for that pass. Direct 2048x2048 reference sampling produced checkerboard artifacts with the author's native graph in BF16 as well as INT8; latent-space 2x refinement also softened line work. Decode, pixel-scale, re-encode and denoise 0.3 preserved clean lines. Disable the Refine / Upscale group to keep the 1 MP reference result. Text-to-image can sample directly at 2048x2048.
