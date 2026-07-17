"""Reference preparation nodes for Phantom-Wan character workflows."""

import torch
import torch.nn.functional as torch_functional


class PhantomReferenceBatch:
    """Build an aspect-safe reference batch without using a reference as frame zero."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_1": ("IMAGE",),
                "width": ("INT", {"default": 832, "min": 16, "max": 8192, "step": 16}),
                "height": ("INT", {"default": 480, "min": 16, "max": 8192, "step": 16}),
                "fit_mode": (
                    ["letterbox_white", "letterbox_black", "center_crop", "stretch"],
                    {"default": "letterbox_white"},
                ),
            },
            "optional": {
                "image_2": ("IMAGE",),
                "image_3": ("IMAGE",),
                "image_4": ("IMAGE",),
                "image_5": ("IMAGE",),
                "image_6": ("IMAGE",),
            },
        }

    RETURN_TYPES = ("IMAGE", "INT")
    RETURN_NAMES = ("references", "reference_count")
    FUNCTION = "prepare"
    CATEGORY = "conditioning/phantom character"
    DESCRIPTION = (
        "Combines one to six subject references and fits each image independently. "
        "Letterboxing preserves full character sheets and portraits before Phantom conditioning."
    )

    @staticmethod
    def _resize(images, width, height, fit_mode):
        images = images[..., :3]
        source_height, source_width = images.shape[1:3]
        channels_first = images.movedim(-1, 1)

        if fit_mode == "stretch":
            return torch_functional.interpolate(
                channels_first,
                size=(height, width),
                mode="bilinear",
                align_corners=False,
                antialias=True,
            ).movedim(1, -1)

        if fit_mode == "center_crop":
            scale = max(width / source_width, height / source_height)
        else:
            scale = min(width / source_width, height / source_height)

        resized_width = max(1, round(source_width * scale))
        resized_height = max(1, round(source_height * scale))
        resized = torch_functional.interpolate(
            channels_first,
            size=(resized_height, resized_width),
            mode="bilinear",
            align_corners=False,
            antialias=True,
        ).movedim(1, -1)

        if fit_mode == "center_crop":
            top = max(0, (resized_height - height) // 2)
            left = max(0, (resized_width - width) // 2)
            return resized[:, top : top + height, left : left + width, :]

        fill = 1.0 if fit_mode == "letterbox_white" else 0.0
        output = torch.full(
            (resized.shape[0], height, width, resized.shape[-1]),
            fill,
            device=resized.device,
            dtype=resized.dtype,
        )
        top = (height - resized_height) // 2
        left = (width - resized_width) // 2
        output[:, top : top + resized_height, left : left + resized_width, :] = resized
        return output

    def prepare(
        self,
        image_1,
        width,
        height,
        fit_mode,
        image_2=None,
        image_3=None,
        image_4=None,
        image_5=None,
        image_6=None,
    ):
        references = [image_1, image_2, image_3, image_4, image_5, image_6]
        prepared = [
            self._resize(image, width, height, fit_mode)
            for image in references
            if image is not None
        ]
        batch = torch.cat(prepared, dim=0)
        return batch, batch.shape[0]


NODE_CLASS_MAPPINGS = {
    "PhantomReferenceBatch": PhantomReferenceBatch,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "PhantomReferenceBatch": "Phantom Reference Batch (1-6)",
}

__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
