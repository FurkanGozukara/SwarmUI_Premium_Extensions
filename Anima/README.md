# Anima

Native Anima text-to-image and ElectronGirl reference adapter integration. Install through the managed SwarmUI updater; it installs the adapter fork and SECoursesAudioTools.

Download the Anima Core Bundle. It contains base v1.0 INT8 ConvRot, Qwen3 600M, the reference adapter and the existing shared Qwen-Image VAE. BF16 is available separately. Anima arithmetic uses BF16 even when storage is INT8.

Apply the text-to-image or reference preset. For transfer, enable Anima Reference and upload one still image in Anima Reference Image, or attach it to the prompt. The explicit reference control takes priority; otherwise the first prompt attachment is used. Write scene, pose and framing; identity and some style come from the image. Euler/simple, 30 steps, CFG 4.5, shift 3, adapter strength 1, reference guidance 4.5 and APG are the starting settings.

The normal prompt, negative, LoRA, resolution and seed controls remain available. Prompt batches accept added prompts, .txt files or a folder. Reference mode preserves the original main latent, so native image-to-image/masks and subsequent refiners remain in Swarm's workflow. The adapter targets the base sampler only; a separate refiner uses its native sampler. Fine accessories and strong painterly styles can drift.
