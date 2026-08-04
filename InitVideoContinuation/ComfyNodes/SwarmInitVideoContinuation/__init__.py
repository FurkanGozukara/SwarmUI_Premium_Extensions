"""Streaming last-frame extraction and video continuation output for SwarmUI."""

from __future__ import annotations

import math
import os
import random
import shutil
import struct
import subprocess
import tempfile
import time
import wave

import av
import folder_paths
import numpy as np
import torch
from server import BinaryEventTypes, PromptServer


VIDEO_PROGRESS_ID = 12346
FORMAT_SETTINGS = {
    "h264-mp4": {
        "extension": "mp4",
        "type_num": 5,
        "video_args": ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "19"],
        "audio_args": ["-c:a", "aac"],
        "container_args": ["-movflags", "+faststart"],
    },
    "h265-mp4": {
        "extension": "mp4",
        "type_num": 5,
        "video_args": ["-c:v", "libx265", "-pix_fmt", "yuv420p"],
        "audio_args": ["-c:a", "aac"],
        "container_args": ["-movflags", "+faststart"],
    },
    "webm": {
        "extension": "webm",
        "type_num": 6,
        "video_args": ["-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-crf", "23", "-b:v", "0"],
        "audio_args": ["-c:a", "libvorbis"],
        "container_args": [],
    },
    "prores": {
        "extension": "mov",
        "type_num": 7,
        "video_args": ["-c:v", "prores_ks", "-profile:v", "3", "-pix_fmt", "yuv422p10le"],
        "audio_args": ["-c:a", "pcm_s16le"],
        "container_args": [],
    },
}


def _finite_positive(value) -> bool:
    try:
        return math.isfinite(float(value)) and float(value) > 0
    except (TypeError, ValueError):
        return False


def _ffmpeg_number(value: float) -> str:
    return f"{float(value):.9f}".rstrip("0").rstrip(".")


def _get_stream_source(video):
    getter = getattr(video, "get_stream_source", None)
    if not callable(getter):
        return None
    source = getter()
    if hasattr(source, "seek"):
        source.seek(0)
    return source


def _resolve_ffmpeg(requested_path: str) -> str:
    candidates = []
    if requested_path:
        candidates.append(requested_path)
    located = shutil.which("ffmpeg")
    if located:
        candidates.append(located)
    for candidate in candidates:
        if candidate == "ffmpeg" or os.path.isfile(candidate):
            return candidate
        resolved = shutil.which(candidate)
        if resolved:
            return resolved
    try:
        from imageio_ffmpeg import get_ffmpeg_exe

        bundled = get_ffmpeg_exe()
        if bundled and os.path.isfile(bundled):
            return bundled
    except (ImportError, RuntimeError):
        pass
    raise RuntimeError("FFmpeg is unavailable. SwarmUI should use the standard frame-batch continuation fallback instead.")


def _materialize_source(video, directory: str) -> str:
    source = _get_stream_source(video)
    if isinstance(source, (str, os.PathLike)) and os.path.isfile(source):
        return os.fspath(source)

    target = os.path.join(directory, "source_video.input")
    if hasattr(source, "read"):
        source.seek(0)
        with open(target, "wb") as output:
            shutil.copyfileobj(source, output, length=1024 * 1024)
        source.seek(0)
        return target

    save_to = getattr(video, "save_to", None)
    if callable(save_to):
        save_to(target)
        return target
    raise ValueError("The ComfyUI VIDEO input does not expose a stream source.")


def _video_stream_duration(source_path: str) -> float | None:
    # Container duration can include audio encoder padding, so measure the visual timeline directly.
    with av.open(source_path, mode="r") as container:
        if container.streams.video:
            stream = container.streams.video[0]
            if stream.duration is not None and stream.time_base is not None:
                duration = float(stream.duration * stream.time_base)
                if _finite_positive(duration):
                    return duration
            if stream.frames and stream.average_rate:
                duration = float(stream.frames / stream.average_rate)
                if _finite_positive(duration):
                    return duration

            first_timestamp = None
            last_end = None
            fallback_frame_duration = None
            if stream.average_rate:
                try:
                    fallback_frame_duration = 1.0 / float(stream.average_rate)
                except (TypeError, ValueError, ZeroDivisionError):
                    pass
            for packet in container.demux(stream):
                timestamp = packet.pts if packet.pts is not None else packet.dts
                if timestamp is None or stream.time_base is None:
                    continue
                packet_start = float(timestamp * stream.time_base)
                packet_duration = float((packet.duration or 0) * stream.time_base)
                if not _finite_positive(packet_duration):
                    packet_duration = fallback_frame_duration or 0.0
                first_timestamp = packet_start if first_timestamp is None else min(first_timestamp, packet_start)
                packet_end = packet_start + packet_duration
                last_end = packet_end if last_end is None else max(last_end, packet_end)
            if first_timestamp is not None and last_end is not None:
                duration = last_end - first_timestamp
                if _finite_positive(duration):
                    return duration
    return None


def _source_duration(video, source_path: str, duration_hint: float) -> float:
    stream_duration = _video_stream_duration(source_path)
    start_time, trim_duration = _active_trim_window(video)
    if _finite_positive(stream_duration):
        available_duration = max(0.0, stream_duration - start_time)
        if _finite_positive(trim_duration):
            return min(float(trim_duration), available_duration)
        if _finite_positive(duration_hint) and float(duration_hint) < available_duration:
            return float(duration_hint)
        get_duration = getattr(video, "get_duration", None)
        if callable(get_duration):
            try:
                duration = float(get_duration())
                if _finite_positive(duration) and duration < available_duration:
                    return duration
            except (av.error.FFmpegError, OSError, TypeError, ValueError):
                pass
        if _finite_positive(available_duration):
            return available_duration

    get_duration = getattr(video, "get_duration", None)
    if callable(get_duration):
        try:
            duration = float(get_duration())
            if _finite_positive(duration):
                return duration
        except (av.error.FFmpegError, OSError, TypeError, ValueError):
            pass
    if _finite_positive(duration_hint):
        return float(duration_hint)
    with av.open(source_path, mode="r") as container:
        if container.duration is not None:
            duration = float(container.duration / av.time_base)
            if _finite_positive(duration):
                return duration
    raise ValueError("Could not determine the input video's duration.")


def _active_trim_window(video) -> tuple[float, float | None]:
    getter = getattr(video, "get_active_trim_window", None)
    if callable(getter):
        try:
            start, duration = getter()
            start = max(0.0, float(start))
            duration = float(duration)
            return start, duration if _finite_positive(duration) else None
        except (TypeError, ValueError):
            pass
    return 0.0, None


def _decode_last_frame_attempt(source, start_time: float, end_time: float | None, seek_time: float):
    if hasattr(source, "seek"):
        source.seek(0)
    with av.open(source, mode="r") as container:
        if not container.streams.video:
            raise ValueError("The Init Image video has no video stream.")
        stream = container.streams.video[0]
        if seek_time > 0 and stream.time_base is not None:
            seek_pts = max(0, int(seek_time / stream.time_base))
            container.seek(seek_pts, stream=stream, backward=True, any_frame=False)

        last_frame = None
        reached_end = False
        for packet in container.demux(stream):
            try:
                frames = packet.decode()
            except av.error.FFmpegError:
                continue
            for frame in frames:
                frame_time = None if frame.pts is None or stream.time_base is None else float(frame.pts * stream.time_base)
                if frame_time is not None and frame_time + 1e-9 < start_time:
                    continue
                if end_time is not None and frame_time is not None and frame_time >= end_time:
                    reached_end = True
                    break
                last_frame = frame
            if reached_end:
                break

        if last_frame is None:
            return None
        image = last_frame.to_ndarray(format="rgb24")
        rotation = getattr(last_frame, "rotation", 0) or 0
        if rotation:
            image = np.rot90(image, k=int(round(rotation // 90)), axes=(0, 1))
        return np.ascontiguousarray(image)


def _decode_last_frame(video) -> torch.Tensor:
    source = _get_stream_source(video)
    if source is None:
        components = video.get_components()
        images = components.images
        if images.shape[0] == 0:
            raise ValueError("The Init Image video contains no decodable frames.")
        return images[-1:].contiguous()

    start_time, trim_duration = _active_trim_window(video)
    end_time = start_time + trim_duration if trim_duration is not None else None
    if end_time is None:
        get_duration = getattr(video, "get_duration", None)
        if callable(get_duration):
            try:
                duration = float(get_duration())
                if _finite_positive(duration):
                    end_time = start_time + duration
            except (av.error.FFmpegError, OSError, TypeError, ValueError):
                pass

    seek_time = start_time
    if end_time is not None:
        seek_time = max(start_time, end_time - 30.0)
    try:
        image = _decode_last_frame_attempt(source, start_time, end_time, seek_time)
    except (av.error.FFmpegError, OSError, ValueError):
        image = None
    if image is None and seek_time > start_time:
        image = _decode_last_frame_attempt(source, start_time, end_time, start_time)
    if image is None:
        raise ValueError("The Init Image video contains no decodable frames.")
    return torch.from_numpy(image).to(dtype=torch.float32).div_(255.0).unsqueeze(0)


def _first_decodable_audio_stream(source_path: str) -> int | None:
    with av.open(source_path, mode="r") as container:
        for stream in reversed(container.streams.audio):
            if stream.codec_context is not None:
                return stream.index
    return None


def _write_generated_audio(audio, path: str) -> bool:
    if audio is None:
        return False
    waveform = audio["waveform"]
    if waveform.dim() == 3:
        waveform = waveform[0]
    if waveform.dim() == 1:
        waveform = waveform.unsqueeze(0)
    if waveform.dim() != 2 or waveform.shape[1] == 0:
        return False

    sample_rate = int(audio["sample_rate"])
    if sample_rate <= 0:
        return False
    samples = waveform.detach().to(device="cpu", dtype=torch.float32).numpy().T
    samples = np.clip(samples, -1.0, 1.0)
    samples = np.rint(samples * 32767.0).astype(np.int16)
    with wave.open(path, "wb") as output:
        output.setnchannels(samples.shape[1])
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(samples.tobytes())
    return True


def _normalized_audio_filter(input_label: str, duration: float, output_label: str) -> str:
    duration_text = _ffmpeg_number(duration)
    return (
        f"{input_label}aresample=48000:async=1:first_pts=0,"
        "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,"
        f"apad=whole_dur={duration_text},atrim=duration={duration_text},"
        f"asetpts=N/SR/TB[{output_label}]"
    )


def _silence_filter(duration: float, output_label: str) -> str:
    duration_text = _ffmpeg_number(duration)
    return (
        "anullsrc=channel_layout=stereo:sample_rate=48000,"
        f"atrim=duration={duration_text},asetpts=N/SR/TB[{output_label}]"
    )


def _send_video(path: str, type_num: int) -> None:
    with open(path, "rb") as output:
        payload = struct.pack(">I", type_num) + output.read()
    server = PromptServer.instance
    server.send_sync("progress", {"value": VIDEO_PROGRESS_ID, "max": VIDEO_PROGRESS_ID}, sid=server.client_id)
    server.send_sync(BinaryEventTypes.PREVIEW_IMAGE, payload, sid=server.client_id)


def _run_ffmpeg(args: list[str], generated_images: torch.Tensor | None) -> None:
    with tempfile.TemporaryFile() as error_output:
        process = subprocess.Popen(
            args,
            stdin=subprocess.PIPE if generated_images is not None else subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=error_output,
        )
        write_error = None
        if generated_images is not None:
            try:
                for image in generated_images:
                    frame = image[..., :3].detach().to(device="cpu", dtype=torch.float32).numpy()
                    frame = np.clip(np.rint(frame * 255.0), 0, 255).astype(np.uint8)
                    process.stdin.write(frame.tobytes())
            except Exception as error:
                write_error = error
            finally:
                try:
                    process.stdin.close()
                except (BrokenPipeError, OSError) as error:
                    if write_error is None:
                        write_error = error
        return_code = process.wait()
        if return_code == 0 and write_error is None:
            return
        error_output.seek(0)
        details = error_output.read().decode("utf-8", errors="replace").strip()
        if len(details) > 6000:
            details = details[-6000:]
        if not details and write_error is not None:
            details = str(write_error)
        raise RuntimeError(f"FFmpeg continuation merge failed (exit {return_code}): {details}")


class SwarmInitVideoLastFrame:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"video": ("VIDEO",)}}

    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("last_frame",)
    FUNCTION = "extract"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Extracts only the last decodable frame of a VIDEO without materializing the full video as an image batch."

    @torch.inference_mode()
    def extract(self, video):
        return (_decode_last_frame(video),)


class SwarmInitVideoPrependSourceSilence:
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "audio": ("AUDIO",),
                "source_images": ("IMAGE",),
                "fps": ("FLOAT", {"default": 24.0, "min": 0.01, "max": 1000.0, "step": 0.01}),
                "source_duration_hint": (
                    "FLOAT",
                    {"default": 0.0, "min": 0.0, "max": 31536000.0, "step": 0.01},
                ),
            }
        }

    RETURN_TYPES = ("AUDIO",)
    FUNCTION = "prepend"
    CATEGORY = "SwarmUI/audio"
    DESCRIPTION = "Keeps generated audio aligned after a silent source-video segment."

    def prepend(self, audio, source_images, fps, source_duration_hint):
        if not isinstance(audio, dict) or "waveform" not in audio or "sample_rate" not in audio:
            raise ValueError("Generated continuation audio is invalid.")
        waveform = audio["waveform"]
        if not isinstance(waveform, torch.Tensor) or waveform.ndim != 3:
            raise ValueError("Generated continuation audio must be a three-dimensional waveform tensor.")
        sample_rate = int(audio["sample_rate"])
        if sample_rate <= 0:
            raise ValueError(f"Generated continuation audio has an invalid sample rate: {sample_rate}.")

        duration = float(source_duration_hint)
        if not _finite_positive(duration):
            if not _finite_positive(fps) or source_images.ndim != 4:
                raise ValueError("Cannot determine the silent source-video duration for generated audio alignment.")
            duration = int(source_images.shape[0]) / float(fps)
        silence_samples = max(0, int(round(duration * sample_rate)))
        silence = waveform.new_zeros((waveform.shape[0], waveform.shape[1], silence_samples))
        return ({"waveform": torch.cat((silence, waveform), dim=2), "sample_rate": sample_rate},)


class SwarmInitVideoContinuationSave:
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "source_video": ("VIDEO",),
                "generated_images": ("IMAGE",),
                "fps": ("FLOAT", {"default": 24.0, "min": 0.01, "max": 1000.0, "step": 0.01}),
                "format": (list(FORMAT_SETTINGS),),
                "ffmpeg_path": ("STRING", {"default": "ffmpeg"}),
                "source_duration_hint": ("FLOAT", {"default": 0.0, "min": 0.0, "max": 31536000.0, "step": 0.01}),
            },
            "optional": {"generated_audio": ("AUDIO",)},
        }

    RETURN_TYPES = ()
    FUNCTION = "save"
    OUTPUT_NODE = True
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Streams the source video through FFmpeg, skips generated frame zero, and saves both segments as one result."

    def save(
        self,
        source_video,
        generated_images,
        fps,
        format,
        ffmpeg_path,
        source_duration_hint,
        generated_audio=None,
    ):
        if format not in FORMAT_SETTINGS:
            raise ValueError(f"Unsupported streaming continuation format: {format}")
        if not _finite_positive(fps):
            raise ValueError(f"Continuation FPS must be positive, got {fps}.")
        if generated_images.ndim != 4 or generated_images.shape[0] == 0:
            raise ValueError("The generated video contains no frames.")
        if generated_images.shape[3] < 3:
            raise ValueError("The generated video must contain at least three color channels.")

        ffmpeg = _resolve_ffmpeg(ffmpeg_path)
        settings = FORMAT_SETTINGS[format]
        frame_count = int(generated_images.shape[0])
        append_count = max(0, frame_count - 1)
        height = int(generated_images.shape[1])
        width = int(generated_images.shape[2])
        if width <= 0 or height <= 0:
            raise ValueError(f"The generated video has invalid dimensions: {width}x{height}.")
        encoded_width = width + width % 2
        encoded_height = height + height % 2
        fps_value = float(fps)

        temp_root = folder_paths.get_temp_directory()
        os.makedirs(temp_root, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="swarm_init_video_", dir=temp_root) as directory:
            source_path = _materialize_source(source_video, directory)
            source_duration = _source_duration(source_video, source_path, source_duration_hint)
            generated_duration = append_count / fps_value
            total_duration = source_duration + generated_duration
            source_audio_stream = _first_decodable_audio_stream(source_path)

            generated_audio_path = os.path.join(directory, "generated_audio.wav")
            has_generated_audio = append_count > 0 and _write_generated_audio(generated_audio, generated_audio_path)
            has_audio = source_audio_stream is not None or has_generated_audio
            output_path = os.path.join(
                directory,
                f"continuation_{random.getrandbits(64):016x}.{settings['extension']}",
            )

            args = [ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", source_path]
            if append_count > 0:
                args += [
                    "-f",
                    "rawvideo",
                    "-pix_fmt",
                    "rgb24",
                    "-video_size",
                    f"{width}x{height}",
                    "-framerate",
                    _ffmpeg_number(fps_value),
                    "-i",
                    "pipe:0",
                ]
            if has_generated_audio:
                args += ["-i", generated_audio_path]

            source_duration_text = _ffmpeg_number(source_duration)
            pad_filter = ""
            if encoded_width != width or encoded_height != height:
                pad_filter = f",pad={encoded_width}:{encoded_height}:0:0:black"
            source_video_filter = (
                f"[0:v:0]scale={width}:{height}:flags=lanczos,format=pix_fmts=rgb24,"
                f"framerate=fps={_ffmpeg_number(fps_value)}:interp_start=0:interp_end=255:scene=100,"
                f"setsar=1{pad_filter},tpad=stop_mode=clone:stop_duration={source_duration_text},"
                f"trim=duration={source_duration_text},setpts=PTS-STARTPTS[srcv]"
            )
            filters = [source_video_filter]
            if append_count > 0:
                filters.append(f"[1:v:0]format=pix_fmts=rgb24,setsar=1{pad_filter},setpts=PTS-STARTPTS[genv]")
                filters.append("[srcv][genv]concat=n=2:v=1:a=0[v]")
            else:
                filters.append("[srcv]null[v]")

            audio_label = None
            if has_audio:
                if source_audio_stream is not None:
                    filters.append(_normalized_audio_filter(f"[0:{source_audio_stream}]", source_duration, "srca"))
                else:
                    filters.append(_silence_filter(source_duration, "srca"))

                if append_count > 0:
                    if has_generated_audio:
                        filters.append(_normalized_audio_filter("[2:a:0]", generated_duration, "gena"))
                    else:
                        filters.append(_silence_filter(generated_duration, "gena"))
                    filters.append("[srca][gena]concat=n=2:v=0:a=1[a]")
                    audio_label = "a"
                else:
                    audio_label = "srca"

            args += ["-filter_complex", ";".join(filters), "-map", "[v]"]
            if audio_label is not None:
                args += ["-map", f"[{audio_label}]"]
            args += settings["video_args"]
            if audio_label is not None:
                args += settings["audio_args"]
            args += ["-r", _ffmpeg_number(fps_value), "-t", _ffmpeg_number(total_duration)]
            args += settings["container_args"]
            args.append(output_path)

            frames_to_append = generated_images[1:] if append_count > 0 else None
            _run_ffmpeg(args, frames_to_append)
            if not os.path.isfile(output_path) or os.path.getsize(output_path) == 0:
                raise RuntimeError("FFmpeg completed without producing a continuation video.")
            _send_video(output_path, settings["type_num"])
        return {}

    @classmethod
    def IS_CHANGED(
        cls,
        source_video,
        generated_images,
        fps,
        format,
        ffmpeg_path,
        source_duration_hint,
        generated_audio=None,
    ):
        return time.time()


NODE_CLASS_MAPPINGS = {
    "SwarmInitVideoLastFrame": SwarmInitVideoLastFrame,
    "SwarmInitVideoPrependSourceSilence": SwarmInitVideoPrependSourceSilence,
    "SwarmInitVideoContinuationSave": SwarmInitVideoContinuationSave,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "SwarmInitVideoLastFrame": "Swarm Init Video Last Frame",
    "SwarmInitVideoPrependSourceSilence": "Swarm Init Video Prepend Source Silence",
    "SwarmInitVideoContinuationSave": "Swarm Init Video Continuation Save",
}

__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
