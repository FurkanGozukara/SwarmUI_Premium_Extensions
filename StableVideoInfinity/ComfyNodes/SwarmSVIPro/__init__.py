"""Deliver an already encoded SVI movie through SwarmUI's normal save protocol."""
import struct
from server import BinaryEventTypes, PromptServer


class SwarmSVIProSaveVideo:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"video": ("VIDEO",)}}

    RETURN_TYPES = ()
    FUNCTION = "save"
    OUTPUT_NODE = True
    CATEGORY = "SwarmUI/video"

    def save(self, video):
        with open(video.get_stream_source(), "rb") as movie:
            payload = struct.pack(">I", 5) + movie.read()
        server = PromptServer.instance
        server.send_sync("progress", {"value": 12346, "max": 12346}, sid=server.client_id)
        server.send_sync(BinaryEventTypes.PREVIEW_IMAGE, payload, sid=server.client_id)
        return {}


NODE_CLASS_MAPPINGS = {"SwarmSVIProSaveVideo": SwarmSVIProSaveVideo}
