# ComfyUI Phantom Character T2V

`PhantomReferenceBatch` prepares one to six subject-reference images for
ComfyUI's native Phantom-Wan workflow. It preserves each source image's aspect
ratio with white letterboxing by default, so portraits and character sheets are
not center-cropped before conditioning.

The node only prepares an image batch. ComfyUI's built-in
`WanPhantomSubjectToVideo` node performs Phantom conditioning and creates an
empty output-video latent. Reference images are therefore identity/subject
conditioning, not the first frame of an image-to-video generation.

Phantom officially recommends no more than four reference images. Inputs five
and six are available for experimentation by compatible workflows.

There are no third-party Python dependencies beyond the PyTorch installation
already required by ComfyUI.
