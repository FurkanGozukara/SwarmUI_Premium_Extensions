// SECourses Prompt Media: a model-agnostic prompt attachment uploader for SwarmUI.
//
// SwarmUI's own prompt media area (#alt_prompt_image_area, built by imagePromptAddImageData in
// genpage/main.js) stays the single source of truth: every attachment is still a native
// .alt-prompt-image-container holding an img / video / audio .alt-prompt-image whose dataset carries the
// file, so the core keeps collecting promptimages / promptvideos / promptaudios for every model and every
// extension (LTX 2.5 Audio To Video, LTX-2 voice reference, Kontext / IP-Adapter images, ...).
// This file adds what the core lacks: a toolbar (add files, pick from the inputs browser, add one video or
// audio with an exact trim window, clear), real cards (waveform audio player, video preview with controls
// and a mute toggle, type badges, filenames, duration / resolution), drag-to-reorder, video/audio paste, a
// drop highlight and the LTX 2.5 Audio To Video source-audio hint.
// While a MiniMax H3 model is selected the MiniMax H3 reference uploader (minimax_h3_prompt_references.js,
// loaded after this file) owns the prompt area and this toolbar hides itself; the SECoursesTrimPopup
// below is shared by both uploaders.

/** Static per-type data for prompt attachments: SwarmUI's display names, the card colors, and extension fallbacks
 * for files the browser reports without a MIME type. */
const SECoursesPromptMediaTypes = {
    image: {
        label: 'Image', tag: 'IMG', color: '#4dabf7', icon: '🖼',
        extensions: ['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp', 'tif', 'tiff', 'avif', 'heic', 'heif'],
    },
    video: {
        label: 'Video', tag: 'VIDEO', color: '#ff6b6b', icon: '🎬',
        extensions: ['mp4', 'webm', 'mov', 'm4v', 'mkv', 'avi', 'mpg', 'mpeg', 'ts'],
    },
    audio: {
        label: 'Audio', tag: 'AUDIO', color: '#fcc419', icon: '🎵',
        extensions: ['wav', 'mp3', 'aac', 'ogg', 'oga', 'opus', 'flac', 'm4a', 'wma', 'aiff', 'aif', 'weba'],
    },
};

/** 'image' / 'video' / 'audio' for a File, from its MIME type first and its extension second; null for anything else. */
function secoursesMediaTypeOf(file) {
    let mime = `${file?.type || ''}`.toLowerCase();
    for (let type in SECoursesPromptMediaTypes) {
        if (mime.startsWith(`${type}/`)) {
            return type;
        }
    }
    let extension = /\.([a-z0-9]{1,8})$/i.exec(`${file?.name || ''}`)?.[1]?.toLowerCase();
    if (extension) {
        for (let type in SECoursesPromptMediaTypes) {
            if (SECoursesPromptMediaTypes[type].extensions.includes(extension)) {
                return type;
            }
        }
    }
    return null;
}

/** Reads a File as a data URL (the form the core stores in .alt-prompt-image dataset.filedata). */
function secoursesReadFileAsDataUrl(file) {
    return new Promise((resolve, reject) => {
        let reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(file);
    });
}

/** "0:07" / "1:23" clock text for player positions. */
function secoursesFormatClock(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) {
        return '0:00';
    }
    let minutes = Math.floor(seconds / 60);
    let rest = Math.floor(seconds - minutes * 60);
    return `${minutes}:${`${rest}`.padStart(2, '0')}`;
}

/** "7.3s" / "1:23" duration text for card metadata. */
function secoursesFormatDuration(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) {
        return '';
    }
    if (seconds < 60) {
        return `${seconds.toFixed(1)}s`;
    }
    return secoursesFormatClock(seconds);
}

/** The display name of an attachment (last path segment of the stored filename). */
function secoursesDisplayFilename(filename) {
    return `${filename || ''}`.replaceAll('\\', '/').split('/').pop();
}

// ==================== Shared trim popup (used by the generic uploader and the MiniMax H3 uploader) ====================

/** Single-file trim popup for one video or audio file: preview, draggable start/end handles, exact second fields,
 * click-to-seek, set-start/set-end at the playhead and a window-only preview. It is UI only: the `onAdd(popup)`
 * callback decides what to do with `popup.file`, `popup.start`, `popup.end` and `popup.trimmed()`. Options:
 * type, title, addLabel, addTitle, minRange (seconds), onAdd(popup), onClose(popup). */
class SECoursesTrimPopup {
    constructor(file, options = {}) {
        if (SECoursesTrimPopup.active) {
            SECoursesTrimPopup.active.close();
        }
        SECoursesTrimPopup.active = this;
        this.file = file;
        this.type = options.type || secoursesMediaTypeOf(file) || 'audio';
        this.onAdd = options.onAdd || null;
        this.onClose = options.onClose || null;
        // Reference videos need a few frames at 24 FPS; audio can be cut much finer.
        this.minRange = options.minRange ?? (this.type === 'video' ? 0.25 : 0.05);
        this.duration = null;
        this.start = 0;
        this.end = null;
        this.previewActive = false;
        this.busy = false;
        this.closed = false;
        this.build(options);
    }

    /** True when the selected window is narrower than the whole file. */
    trimmed() {
        if (!this.duration) {
            return false;
        }
        return this.start > 0.01 || this.end < this.duration - 0.01;
    }

    build(options) {
        let type = this.type;
        this.modal = createDiv(null, 'secourses-trim-modal');
        let panel = createDiv(null, 'secourses-trim-panel');

        let head = createDiv(null, 'secourses-trim-head');
        let title = createSpan(null, 'secourses-trim-title');
        title.textContent = options.title || (type === 'video' ? '✂ Trim Video' : '✂ Trim Audio');
        let name = createSpan(null, 'secourses-trim-filename');
        name.textContent = this.file.name;
        name.title = this.file.name;
        let close = document.createElement('button');
        close.type = 'button';
        close.className = 'secourses-trim-close';
        close.innerHTML = '&times;';
        close.title = 'Close without adding';
        head.append(title, name, close);

        let preview = createDiv(null, 'secourses-trim-preview');
        this.element = document.createElement(type === 'video' ? 'video' : 'audio');
        this.element.className = `secourses-trim-media secourses-trim-media-${type}`;
        this.element.controls = true;
        this.element.preload = 'metadata';
        if (type === 'video') {
            this.element.playsInline = true;
        }
        this.url = URL.createObjectURL(this.file);
        this.element.src = this.url;
        preview.appendChild(this.element);

        this.track = createDiv(null, 'secourses-trim-track');
        this.track.title = 'Click to seek the preview. Drag the handles to set the trim window.';
        this.fill = createDiv(null, 'secourses-trim-fill');
        this.playhead = createDiv(null, 'secourses-trim-playhead');
        this.startHandle = createDiv(null, 'secourses-trim-handle secourses-trim-handle-start');
        this.startHandle.title = 'Drag to set the trim start';
        this.endHandle = createDiv(null, 'secourses-trim-handle secourses-trim-handle-end');
        this.endHandle.title = 'Drag to set the trim end';
        this.track.append(this.fill, this.playhead, this.startHandle, this.endHandle);

        let fields = createDiv(null, 'secourses-trim-fields');
        let makeTimeField = (labelText, titleText) => {
            let label = document.createElement('label');
            label.className = 'secourses-trim-label';
            label.append(labelText);
            let input = document.createElement('input');
            input.type = 'number';
            input.className = 'secourses-trim-input';
            input.min = '0';
            input.step = '0.05';
            input.title = titleText;
            label.appendChild(input);
            fields.appendChild(label);
            return input;
        };
        this.startInput = makeTimeField('Start', 'Trim start in seconds');
        this.endInput = makeTimeField('End', 'Trim end in seconds');
        let makeToolButton = (text, titleText) => {
            let button = document.createElement('button');
            button.type = 'button';
            button.className = 'basic-button secourses-trim-tool';
            button.textContent = text;
            button.title = titleText;
            fields.appendChild(button);
            return button;
        };
        let setStart = makeToolButton('⇤ Start', 'Set the trim start to the current playback position');
        let setEnd = makeToolButton('End ⇥', 'Set the trim end to the current playback position');
        let previewButton = makeToolButton('▶ Preview', 'Play only the selected trim window');
        this.badge = createSpan(null, 'secourses-trim-length');
        fields.appendChild(this.badge);

        let actions = createDiv(null, 'secourses-trim-actions');
        this.addButton = document.createElement('button');
        this.addButton.type = 'button';
        this.addButton.className = 'basic-button secourses-trim-add';
        this.addButton.textContent = options.addLabel || '➕ Add';
        this.addButton.title = options.addTitle || 'Attach this file. Only the selected window is used.';
        let cancelButton = document.createElement('button');
        cancelButton.type = 'button';
        cancelButton.className = 'basic-button secourses-trim-cancel';
        cancelButton.textContent = 'Cancel';
        this.note = createSpan(null, 'secourses-trim-note');
        this.note.textContent = 'Loading duration…';
        actions.append(this.addButton, cancelButton, this.note);

        panel.append(head, preview, this.track, fields, actions);
        this.modal.appendChild(panel);
        document.body.appendChild(this.modal);

        this.onEscape = (event) => {
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                this.close();
            }
        };
        document.addEventListener('keydown', this.onEscape, true);
        // Keep prompt-box hotkeys and SwarmUI global key handlers out of the popup.
        panel.addEventListener('keydown', (event) => {
            if (event.key !== 'Escape') {
                event.stopPropagation();
            }
        });
        this.modal.addEventListener('mousedown', (event) => {
            if (event.target === this.modal) {
                this.close();
            }
        });
        close.addEventListener('click', () => this.close());
        cancelButton.addEventListener('click', () => this.close());
        this.addButton.addEventListener('click', () => {
            if (this.onAdd && !this.busy) {
                this.onAdd(this);
            }
        });

        let element = this.element;
        element.addEventListener('loadedmetadata', () => {
            let duration = Number(element.duration);
            if (Number.isFinite(duration) && duration > 0) {
                this.duration = duration;
                this.start = 0;
                this.end = duration;
                this.note.textContent = '';
                this.updateUI();
            }
            else {
                this.note.textContent = 'Duration unavailable — this file can only be added untrimmed.';
            }
        });
        element.addEventListener('timeupdate', () => {
            if (!this.duration) {
                return;
            }
            let position = Math.min(element.currentTime, this.duration);
            this.playhead.style.left = `${(position / this.duration) * 100}%`;
            if (this.previewActive && element.currentTime >= this.end - 0.02) {
                element.pause();
                this.previewActive = false;
            }
        });
        element.addEventListener('pause', () => {
            this.previewActive = false;
        });
        element.addEventListener('error', () => {
            if (!this.duration) {
                this.note.textContent = 'Preview failed — the file can still be added untrimmed.';
            }
        });

        this.bindHandle(this.startHandle, true);
        this.bindHandle(this.endHandle, false);
        this.track.addEventListener('pointerdown', (event) => {
            if (!this.duration || event.target === this.startHandle || event.target === this.endHandle) {
                return;
            }
            event.preventDefault();
            element.currentTime = this.timelineTime(event);
        });
        this.startInput.addEventListener('change', () => {
            if (this.duration) {
                let value = parseFloat(this.startInput.value);
                this.setRange(value, this.end, value);
            }
        });
        this.endInput.addEventListener('change', () => {
            if (this.duration) {
                let value = parseFloat(this.endInput.value);
                this.setRange(this.start, value, value);
            }
        });
        setStart.addEventListener('click', () => {
            if (this.duration) {
                this.setRange(element.currentTime, this.end);
            }
        });
        setEnd.addEventListener('click', () => {
            if (this.duration) {
                this.setRange(this.start, element.currentTime);
            }
        });
        previewButton.addEventListener('click', () => {
            if (!this.duration) {
                return;
            }
            element.currentTime = this.start;
            this.previewActive = true;
            element.play().catch(() => {
                this.previewActive = false;
            });
        });
    }

    /** Shows a status line under the buttons (progress, errors). */
    setNote(text) {
        this.note.textContent = text || '';
    }

    /** Disables the add button while the owner processes the file. */
    setBusy(busy) {
        this.busy = Boolean(busy);
        this.addButton.disabled = this.busy;
    }

    close() {
        if (this.closed) {
            return;
        }
        this.closed = true;
        if (SECoursesTrimPopup.active === this) {
            SECoursesTrimPopup.active = null;
        }
        document.removeEventListener('keydown', this.onEscape, true);
        this.element.pause?.();
        this.modal.remove();
        URL.revokeObjectURL(this.url);
        if (this.onClose) {
            this.onClose(this);
        }
    }

    /** Timeline seconds for a pointer event over the trim track. */
    timelineTime(event) {
        let rect = this.track.getBoundingClientRect();
        let ratio = rect.width ? (event.clientX - rect.left) / rect.width : 0;
        return Math.max(0, Math.min(1, ratio)) * (this.duration ?? 0);
    }

    bindHandle(handle, isStart) {
        handle.addEventListener('pointerdown', (event) => {
            if (!this.duration) {
                return;
            }
            event.preventDefault();
            event.stopPropagation();
            try {
                handle.setPointerCapture(event.pointerId);
            }
            catch (error) {
                // Dragging still works without capture; it just stops at the panel edge.
            }
            let move = (moveEvent) => {
                let time = this.timelineTime(moveEvent);
                if (isStart) {
                    this.setRange(Math.min(time, this.end - this.minRange), this.end, time);
                }
                else {
                    this.setRange(this.start, Math.max(time, this.start + this.minRange), time);
                }
            };
            let stop = () => {
                handle.removeEventListener('pointermove', move);
                handle.removeEventListener('pointerup', stop);
                handle.removeEventListener('pointercancel', stop);
            };
            handle.addEventListener('pointermove', move);
            handle.addEventListener('pointerup', stop);
            handle.addEventListener('pointercancel', stop);
            move(event);
        });
    }

    setRange(start, end, seek = null) {
        if (!this.duration) {
            return;
        }
        let minRange = Math.min(this.minRange, this.duration);
        start = Math.max(0, Math.min(Number.isFinite(start) ? start : 0, this.duration));
        end = Math.max(0, Math.min(Number.isFinite(end) ? end : this.duration, this.duration));
        if (end - start < minRange) {
            end = Math.min(this.duration, start + minRange);
            start = Math.max(0, Math.min(start, end - minRange));
        }
        this.start = start;
        this.end = end;
        if (seek != null && Number.isFinite(seek)) {
            this.element.currentTime = Math.max(0, Math.min(seek, this.duration));
        }
        this.updateUI();
    }

    updateUI() {
        if (!this.duration) {
            return;
        }
        let startPct = (this.start / this.duration) * 100;
        let endPct = (this.end / this.duration) * 100;
        this.startHandle.style.left = `${startPct}%`;
        this.endHandle.style.left = `${endPct}%`;
        this.fill.style.left = `${startPct}%`;
        this.fill.style.width = `${Math.max(0, endPct - startPct)}%`;
        if (document.activeElement !== this.startInput) {
            this.startInput.value = this.start.toFixed(2);
        }
        if (document.activeElement !== this.endInput) {
            this.endInput.value = this.end.toFixed(2);
        }
        let trimmed = this.trimmed();
        this.badge.textContent = trimmed
            ? `✂ ${(this.end - this.start).toFixed(2)}s of ${this.duration.toFixed(2)}s`
            : `full ${this.duration.toFixed(2)}s (untrimmed)`;
        this.badge.classList.toggle('secourses-trim-length-active', trimmed);
    }

    /** Renders the selected slice of an audio file to a 16-bit PCM WAV File, fully client-side and sample-accurate. */
    static async sliceAudioToWav(file, start, end, newName) {
        let context = new AudioContext();
        let buffer;
        try {
            buffer = await context.decodeAudioData(await file.arrayBuffer());
        }
        finally {
            context.close().catch(() => {});
        }
        let rate = buffer.sampleRate;
        let first = Math.max(0, Math.floor(start * rate));
        let last = Math.min(buffer.length, Math.max(first + 1, Math.round(end * rate)));
        let frames = last - first;
        let channels = Math.min(2, buffer.numberOfChannels);
        let bytesPerFrame = channels * 2;
        let dataSize = frames * bytesPerFrame;
        let wav = new DataView(new ArrayBuffer(44 + dataSize));
        let writeText = (offset, text) => [...text].forEach((c, i) => wav.setUint8(offset + i, c.charCodeAt(0)));
        writeText(0, 'RIFF');
        wav.setUint32(4, 36 + dataSize, true);
        writeText(8, 'WAVEfmt ');
        wav.setUint32(16, 16, true);
        wav.setUint16(20, 1, true);
        wav.setUint16(22, channels, true);
        wav.setUint32(24, rate, true);
        wav.setUint32(28, rate * bytesPerFrame, true);
        wav.setUint16(32, bytesPerFrame, true);
        wav.setUint16(34, 16, true);
        writeText(36, 'data');
        wav.setUint32(40, dataSize, true);
        let offset = 44;
        let channelData = [];
        for (let c = 0; c < channels; c++) {
            channelData.push(buffer.getChannelData(c));
        }
        for (let i = first; i < last; i++) {
            for (let c = 0; c < channels; c++) {
                let sample = Math.max(-1, Math.min(1, channelData[c][i]));
                wav.setInt16(offset, sample < 0 ? sample * 0x8000 : sample * 0x7fff, true);
                offset += 2;
            }
        }
        return new File([wav.buffer], newName, { type: 'audio/wav' });
    }
}
SECoursesTrimPopup.active = null;

// ==================== Waveform audio player for attachment cards ====================

/** Waveform player drawn for a card's native <audio> element. The native element stays the playback engine and the
 * data carrier for the generation request; it is only hidden visually. Peaks are decoded once per source. */
class SECoursesAudioPlayer {
    constructor(audio, color) {
        this.audio = audio;
        this.color = color;
        this.peaks = null;
        this.decoding = null;
        this.failed = false;
        this.root = createDiv(null, 'secourses-audio-player');
        this.playButton = document.createElement('button');
        this.playButton.type = 'button';
        this.playButton.className = 'secourses-audio-play';
        this.playButton.title = 'Play / pause';
        this.playButton.textContent = '▶';
        this.wave = createDiv(null, 'secourses-audio-wave');
        this.wave.title = 'Click to seek';
        this.canvas = document.createElement('canvas');
        this.time = createSpan(null, 'secourses-audio-time');
        this.wave.append(this.canvas, this.time);
        this.root.append(this.playButton, this.wave);
        this.playButton.addEventListener('click', (event) => {
            event.stopPropagation();
            this.toggle();
        });
        this.wave.addEventListener('pointerdown', (event) => {
            event.stopPropagation();
            this.seek(event);
        });
        this.listeners = [];
        let listen = (name, handler) => {
            audio.addEventListener(name, handler);
            this.listeners.push([name, handler]);
        };
        listen('play', () => this.updatePlayState());
        listen('pause', () => this.updatePlayState());
        listen('ended', () => this.updatePlayState());
        listen('timeupdate', () => this.updateTime());
        listen('loadedmetadata', () => {
            this.updateTime();
            this.loadPeaks();
        });
        listen('durationchange', () => this.updateTime());
        listen('error', () => {
            this.failed = true;
            this.draw();
        });
        this.resizeObserver = new ResizeObserver(() => this.draw());
        this.resizeObserver.observe(this.wave);
        this.updatePlayState();
        this.updateTime();
        if (audio.readyState >= HTMLMediaElement.HAVE_METADATA || audio.src) {
            this.loadPeaks();
        }
        this.draw();
    }

    get element() {
        return this.root;
    }

    toggle() {
        if (this.audio.paused) {
            this.audio.play().catch((error) => {
                this.failed = true;
                this.draw();
            });
        }
        else {
            this.audio.pause();
        }
    }

    seek(event) {
        let duration = this.audio.duration;
        if (!Number.isFinite(duration) || duration <= 0) {
            return;
        }
        let rect = this.wave.getBoundingClientRect();
        let ratio = rect.width ? (event.clientX - rect.left) / rect.width : 0;
        this.audio.currentTime = Math.max(0, Math.min(1, ratio)) * duration;
        this.draw();
    }

    updatePlayState() {
        let playing = !this.audio.paused && !this.audio.ended;
        this.playButton.textContent = playing ? '❚❚' : '▶';
        this.root.classList.toggle('secourses-audio-playing', playing);
    }

    updateTime() {
        let duration = this.audio.duration;
        let text = Number.isFinite(duration) && duration > 0
            ? `${secoursesFormatClock(this.audio.currentTime)} / ${secoursesFormatClock(duration)}`
            : secoursesFormatClock(this.audio.currentTime);
        if (this.time.textContent !== text) {
            this.time.textContent = text;
        }
        this.draw();
    }

    /** Decodes the audio once (Web Audio, offline so no autoplay policy is involved) and keeps normalized peaks. */
    async loadPeaks() {
        let source = this.audio.currentSrc || this.audio.src;
        if (!source || this.peaks) {
            return;
        }
        let cached = SECoursesAudioPlayer.peakCache.get(source);
        if (cached) {
            this.peaks = cached;
            this.draw();
            return;
        }
        if (this.decoding === source) {
            return;
        }
        this.decoding = source;
        try {
            let response = await fetch(source);
            let bytes = await response.arrayBuffer();
            if (bytes.byteLength > SECoursesAudioPlayer.maxDecodeBytes) {
                throw new Error('audio too large to draw a waveform');
            }
            let context = new OfflineAudioContext(1, 1, 44100);
            let buffer = await context.decodeAudioData(bytes);
            let peaks = SECoursesAudioPlayer.computePeaks(buffer, 320);
            if (SECoursesAudioPlayer.peakCache.size >= 40) {
                SECoursesAudioPlayer.peakCache.delete(SECoursesAudioPlayer.peakCache.keys().next().value);
            }
            SECoursesAudioPlayer.peakCache.set(source, peaks);
            this.peaks = peaks;
        }
        catch (error) {
            this.failed = true;
        }
        finally {
            this.decoding = null;
            this.draw();
        }
    }

    /** Normalized peak amplitude per bucket, mixed down from every channel. */
    static computePeaks(buffer, buckets) {
        let length = buffer.length;
        let channels = buffer.numberOfChannels;
        let data = [];
        for (let c = 0; c < channels; c++) {
            data.push(buffer.getChannelData(c));
        }
        let peaks = new Float32Array(buckets);
        let step = Math.max(1, length / buckets);
        // Sample a bounded number of points per bucket so long files stay cheap.
        let stride = Math.max(1, Math.floor(step / 256));
        let maxPeak = 0;
        for (let b = 0; b < buckets; b++) {
            let first = Math.floor(b * step);
            let last = Math.min(length, Math.floor((b + 1) * step));
            let peak = 0;
            for (let i = first; i < last; i += stride) {
                let sum = 0;
                for (let c = 0; c < channels; c++) {
                    sum += Math.abs(data[c][i]);
                }
                let value = sum / channels;
                if (value > peak) {
                    peak = value;
                }
            }
            peaks[b] = peak;
            if (peak > maxPeak) {
                maxPeak = peak;
            }
        }
        if (maxPeak > 0) {
            for (let b = 0; b < buckets; b++) {
                peaks[b] = peaks[b] / maxPeak;
            }
        }
        return peaks;
    }

    draw() {
        let width = this.wave.clientWidth;
        let height = this.wave.clientHeight;
        if (!width || !height) {
            return;
        }
        let dpr = window.devicePixelRatio || 1;
        let canvasWidth = Math.round(width * dpr);
        let canvasHeight = Math.round(height * dpr);
        if (this.canvas.width !== canvasWidth || this.canvas.height !== canvasHeight) {
            this.canvas.width = canvasWidth;
            this.canvas.height = canvasHeight;
        }
        let ctx = this.canvas.getContext('2d');
        ctx.clearRect(0, 0, canvasWidth, canvasHeight);
        let duration = this.audio.duration;
        let progress = Number.isFinite(duration) && duration > 0 ? this.audio.currentTime / duration : 0;
        let barWidth = 2 * dpr;
        let gap = 1 * dpr;
        let bars = Math.max(1, Math.floor(canvasWidth / (barWidth + gap)));
        let middle = canvasHeight / 2;
        let playedUntil = progress * canvasWidth;
        for (let i = 0; i < bars; i++) {
            let amplitude;
            if (this.peaks) {
                amplitude = this.peaks[Math.min(this.peaks.length - 1, Math.floor((i / bars) * this.peaks.length))];
            }
            else {
                // Placeholder while decoding (or when the file cannot be decoded): a quiet even pattern.
                amplitude = this.failed ? 0.12 : 0.12 + 0.08 * Math.abs(Math.sin(i * 0.7));
            }
            let barHeight = Math.max(1.5 * dpr, amplitude * (canvasHeight - 2 * dpr));
            let x = i * (barWidth + gap);
            ctx.fillStyle = x < playedUntil ? this.color : SECoursesAudioPlayer.dim(this.color, this.peaks ? 0.42 : 0.28);
            ctx.fillRect(x, middle - barHeight / 2, barWidth, barHeight);
        }
    }

    /** rgba() of a #rrggbb color with the given alpha. */
    static dim(hex, alpha) {
        let match = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex);
        if (!match) {
            return hex;
        }
        return `rgba(${parseInt(match[1], 16)}, ${parseInt(match[2], 16)}, ${parseInt(match[3], 16)}, ${alpha})`;
    }

    destroy() {
        this.resizeObserver.disconnect();
        for (let [name, handler] of this.listeners) {
            this.audio.removeEventListener(name, handler);
        }
        this.listeners = [];
        this.root.remove();
    }
}
SECoursesAudioPlayer.peakCache = new Map();
SECoursesAudioPlayer.maxDecodeBytes = 80 * 1024 * 1024;

// ==================== The uploader ====================

/** Model-agnostic prompt attachment toolbar and card decorations over SwarmUI's native prompt media area. */
class SECoursesPromptMedia {
    constructor() {
        this.cardState = new WeakMap();
        this.dragContext = null;
        this.syncQueued = false;
        this.a2vInputId = 'input_ltxaudiotovideo';
        this.hoverCapable = typeof window.matchMedia === 'function' ? window.matchMedia('(hover: hover)').matches : true;
    }

    register() {
        if (document.getElementById('secourses_prompt_media_toolbar')) {
            return;
        }
        this.region = document.getElementById('alt_prompt_region');
        this.extraArea = document.getElementById('alt_prompt_extra_area');
        this.area = document.getElementById('alt_prompt_image_area');
        this.promptBox = document.getElementById('alt_prompt_textbox');
        this.clearButton = document.getElementById('alt_prompt_image_clear_button');
        if (!this.region || !this.extraArea || !this.area || !this.promptBox) {
            return;
        }
        this.createToolbar();
        this.bindEvents();
        this.observer = new MutationObserver((mutations) => this.onMutations(mutations));
        this.observer.observe(this.area, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['data-duration', 'data-resolution', 'data-filename'],
        });
        for (let id of ['current_model', 'input_model']) {
            document.getElementById(id)?.addEventListener('change', () => this.onModelChange());
        }
        // Parameter inputs are (re)built later, so listen delegated for the LTX 2.5 Audio To Video switch.
        document.addEventListener('change', (event) => {
            if (event.target?.id === this.a2vInputId) {
                this.scheduleSync();
            }
        }, true);
        this.syncAll();
    }

    /** True while the MiniMax H3 reference uploader owns the prompt area. */
    isH3Active() {
        if (typeof currentModelHelper !== 'undefined' && currentModelHelper.curCompatClass === 'minimax-h3') {
            return true;
        }
        return this.region.classList.contains('minimax-h3-prompt-references-active');
    }

    onModelChange() {
        this.scheduleSync();
        // The MiniMax H3 uploader updates its own state on the same events, partly deferred.
        window.setTimeout(() => this.scheduleSync(), 0);
    }

    createToolbar() {
        this.toolbar = createDiv('secourses_prompt_media_toolbar', 'secourses-prompt-media-toolbar');

        this.addButton = document.createElement('button');
        this.addButton.type = 'button';
        this.addButton.className = 'basic-button secourses-prompt-media-add';
        this.addButton.textContent = '➕ Add Image / Video / Audio';
        this.addButton.title = 'Attach one or more image, video, or audio files to the prompt (kept in the browser for this generation)';

        this.browseButton = document.createElement('button');
        this.browseButton.type = 'button';
        this.browseButton.className = 'basic-button secourses-prompt-media-browse';
        this.browseButton.textContent = '📂 Select From Inputs';
        this.browseButton.title = 'Pick an image, video, or audio file from the inputs browser (files saved there stay available after a reload)';

        this.trimButton = document.createElement('button');
        this.trimButton.type = 'button';
        this.trimButton.className = 'basic-button secourses-prompt-media-trim';
        this.trimButton.textContent = '✂ Add With Trim';
        this.trimButton.title = 'Attach one video or audio file and pick the exact start/end window to use. Audio is cut sample-accurately in the browser; video is trimmed losslessly-timed on the server into the inputs folder.';

        this.clearAllButton = document.createElement('button');
        this.clearAllButton.type = 'button';
        this.clearAllButton.className = 'basic-button secourses-prompt-media-clear';
        this.clearAllButton.textContent = '🗑 Clear';
        this.clearAllButton.title = 'Remove every prompt attachment';
        this.clearAllButton.style.display = 'none';

        this.status = createSpan(null, 'secourses-prompt-media-status');
        this.status.setAttribute('aria-live', 'polite');

        this.hint = createSpan(null, 'secourses-prompt-media-hint');
        this.hint.style.display = 'none';

        this.fileInput = document.createElement('input');
        this.fileInput.type = 'file';
        this.fileInput.multiple = true;
        this.fileInput.accept = 'image/*,video/*,audio/*';
        this.fileInput.className = 'secourses-prompt-media-file-input';
        this.fileInput.setAttribute('aria-label', 'Attach prompt images, videos, or audio');

        this.trimFileInput = document.createElement('input');
        this.trimFileInput.type = 'file';
        this.trimFileInput.accept = 'video/*,audio/*';
        this.trimFileInput.className = 'secourses-prompt-media-file-input';
        this.trimFileInput.setAttribute('aria-label', 'Attach one video or audio file with trim');

        this.toolbar.append(this.addButton, this.browseButton, this.trimButton, this.clearAllButton, this.status, this.hint, this.fileInput, this.trimFileInput);
        this.extraArea.prepend(this.toolbar);
    }

    bindEvents() {
        this.addButton.addEventListener('click', () => this.fileInput.click());
        this.fileInput.addEventListener('change', async () => {
            let files = [...this.fileInput.files];
            this.fileInput.value = '';
            await this.addFiles(files);
        });
        this.trimButton.addEventListener('click', () => this.trimFileInput.click());
        this.trimFileInput.addEventListener('change', () => {
            let file = this.trimFileInput.files[0];
            this.trimFileInput.value = '';
            if (file) {
                this.openTrimPopup(file);
            }
        });
        this.browseButton.addEventListener('click', () => {
            if (typeof inputBrowserHelper === 'undefined') {
                showError('The inputs browser is not available on this page.');
                return;
            }
            inputBrowserHelper.openInputBrowser(null, ['image', 'video', 'audio'], (file) => {
                imagePromptAddImageData(file.data.src, getMediaType(file.name), file.name, file.name);
            });
        });
        this.clearAllButton.addEventListener('click', () => {
            if (typeof clearPromptImages === 'function') {
                clearPromptImages();
            }
        });

        // The core's inline paste handler only attaches images; add pasted video and audio files too.
        this.promptBox.addEventListener('paste', (event) => {
            if (this.isH3Active()) {
                return;
            }
            let files = [...(event.clipboardData?.items || [])]
                .filter(item => item.kind === 'file')
                .map(item => item.getAsFile())
                .filter(file => file);
            let extra = files.filter(file => {
                let type = secoursesMediaTypeOf(file);
                return type === 'video' || type === 'audio';
            });
            if (extra.length) {
                this.addFiles(extra);
            }
        }, true);

        // Card reordering among attachments of the same type. Capture phase on the area: file drags never
        // carry a drag context, and internal drags carry no files, so the core's drop logic ignores them.
        this.area.addEventListener('dragover', (event) => {
            if (!this.dragContext) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            event.dataTransfer.dropEffect = 'move';
            this.updateDropMarker(this.reorderInsertPos(event));
        }, true);
        this.area.addEventListener('drop', (event) => {
            if (!this.dragContext) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            let insertPos = this.reorderInsertPos(event);
            this.clearDropMarkers();
            this.applyReorder(insertPos);
            this.dragContext = null;
        }, true);
    }

    scheduleSync() {
        if (this.syncQueued) {
            return;
        }
        this.syncQueued = true;
        requestAnimationFrame(() => {
            this.syncQueued = false;
            this.syncAll();
        });
    }

    onMutations(mutations) {
        let relevant = false;
        for (let mutation of mutations) {
            for (let node of mutation.removedNodes) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    for (let card of node.matches?.('.alt-prompt-image-container') ? [node] : node.querySelectorAll?.('.alt-prompt-image-container') || []) {
                        this.forgetCard(card);
                    }
                }
            }
            // Changes inside this module's own elements (player time, footer text) never need a resync.
            let target = mutation.target;
            if (target.nodeType === Node.ELEMENT_NODE && target.closest('.secourses-media-footer, .secourses-audio-player')) {
                continue;
            }
            relevant = true;
        }
        if (relevant) {
            this.scheduleSync();
        }
    }

    /** Native attachment containers in display order (the MiniMax H3 uploader's own video/audio cards are not native). */
    cards() {
        return [...this.area.querySelectorAll('.alt-prompt-image-container')]
            .filter(card => card.parentElement === this.area && card.querySelector('.alt-prompt-image'));
    }

    mediaOf(card) {
        return card.querySelector('.alt-prompt-image');
    }

    typeOf(media) {
        let tag = media?.tagName;
        for (let type in SECoursesPromptMediaTypes) {
            if (SECoursesPromptMediaTypes[type].tag === tag) {
                return type;
            }
        }
        return null;
    }

    /** True when LTX 2.5 Audio To Video is switched on (its first prompt audio is the source audio). */
    a2vEnabled() {
        let input = document.getElementById(this.a2vInputId);
        return Boolean(input && input.checked);
    }

    syncAll() {
        if (!this.area || !this.toolbar) {
            return;
        }
        let h3 = this.isH3Active();
        this.region.classList.toggle('secourses-prompt-media-active', !h3);
        this.toolbar.style.display = h3 ? 'none' : 'flex';
        let cards = this.cards();
        if (h3) {
            for (let card of cards) {
                this.undecorate(card);
            }
            return;
        }
        let counts = { image: 0, video: 0, audio: 0 };
        let a2v = this.a2vEnabled();
        for (let card of cards) {
            let media = this.mediaOf(card);
            let type = this.typeOf(media);
            if (!type) {
                continue;
            }
            this.decorate(card, media, type);
            counts[type]++;
            this.refreshCard(card, counts[type], a2v);
        }
        this.updateStatus(counts, a2v);
        if (typeof genTabLayout !== 'undefined') {
            genTabLayout.altPromptSizeHandle();
        }
    }

    decorate(card, media, type) {
        let state = this.cardState.get(card);
        if (state && state.media === media && state.type === type) {
            return;
        }
        if (state) {
            this.undecorate(card);
        }
        for (let orphan of card.querySelectorAll('.secourses-media-footer, .secourses-audio-player')) {
            orphan.remove();
        }
        let color = SECoursesPromptMediaTypes[type].color;
        card.classList.add('secourses-media-card', `secourses-media-card-${type}`);
        card.dataset.secoursesType = type;
        card.style.setProperty('--secourses-media-color', color);
        let footer = createDiv(null, 'secourses-media-footer');
        let badgeRow = createDiv(null, 'secourses-media-badge-row');
        let badge = createSpan(null, 'secourses-media-badge');
        let role = createSpan(null, 'secourses-media-role');
        role.style.display = 'none';
        let meta = createSpan(null, 'secourses-media-meta');
        badgeRow.append(badge, role, meta);
        let name = createSpan(null, 'secourses-media-name');
        footer.append(badgeRow, name);
        state = { media: media, type: type, footer: footer, badge: badge, role: role, meta: meta, name: name, player: null, muteButton: null, handlers: [] };
        if (type === 'audio') {
            state.player = new SECoursesAudioPlayer(media, color);
            media.after(state.player.element);
            state.player.element.after(footer);
        }
        else {
            media.after(footer);
        }
        if (type === 'video') {
            media.playsInline = true;
            media.controls = !this.hoverCapable;
            let muteButton = document.createElement('button');
            muteButton.type = 'button';
            muteButton.className = 'secourses-media-mute';
            muteButton.addEventListener('click', (event) => {
                event.stopPropagation();
                media.muted = !media.muted;
                this.refreshMuteButton(state);
            });
            let onVolume = () => this.refreshMuteButton(state);
            media.addEventListener('volumechange', onVolume);
            state.handlers.push(['volumechange', onVolume]);
            badgeRow.insertBefore(muteButton, meta);
            state.muteButton = muteButton;
            this.refreshMuteButton(state);
            if (this.hoverCapable) {
                let onEnter = () => { media.controls = true; };
                let onLeave = () => { media.controls = false; };
                card.addEventListener('mouseenter', onEnter);
                card.addEventListener('mouseleave', onLeave);
                state.cardHandlers = [['mouseenter', onEnter], ['mouseleave', onLeave]];
            }
        }
        media.draggable = false;
        this.bindCardDrag(card);
        this.cardState.set(card, state);
    }

    refreshMuteButton(state) {
        if (!state.muteButton) {
            return;
        }
        let muted = state.media.muted;
        let text = muted ? '🔇' : '🔊';
        if (state.muteButton.textContent !== text) {
            state.muteButton.textContent = text;
        }
        state.muteButton.title = muted ? 'Unmute the preview' : 'Mute the preview';
    }

    undecorate(card) {
        let state = this.cardState.get(card);
        if (!state) {
            return;
        }
        state.player?.destroy();
        state.footer.remove();
        for (let [name, handler] of state.handlers) {
            state.media.removeEventListener(name, handler);
        }
        for (let [name, handler] of state.cardHandlers || []) {
            card.removeEventListener(name, handler);
        }
        if (state.type === 'video') {
            state.media.controls = false;
        }
        card.classList.remove('secourses-media-card', 'secourses-media-card-image', 'secourses-media-card-video', 'secourses-media-card-audio');
        delete card.dataset.secoursesType;
        delete card.dataset.secoursesRole;
        card.style.removeProperty('--secourses-media-color');
        this.cardState.delete(card);
    }

    /** Drops the state of a card that left the area. A reorder is a removal plus an insertion in the same mutation
     * batch, so a card that is still connected keeps its decorations. */
    forgetCard(card) {
        if (card.isConnected) {
            return;
        }
        let state = this.cardState.get(card);
        if (state) {
            state.player?.destroy();
            this.cardState.delete(card);
        }
    }

    /** Applies the per-position label, source role, metadata and filename of one card. */
    refreshCard(card, index, a2v) {
        let state = this.cardState.get(card);
        if (!state) {
            return;
        }
        let media = state.media;
        let info = SECoursesPromptMediaTypes[state.type];
        let setText = (element, text) => {
            if (element.textContent !== text) {
                element.textContent = text;
            }
        };
        setText(state.badge, `${info.icon} ${info.label} ${index}`);
        let isSource = a2v && state.type === 'audio' && index === 1;
        if (isSource) {
            setText(state.role, 'source');
            state.role.title = 'LTX 2.5 Audio To Video follows this audio exactly (the first audio attachment).';
        }
        let roleDisplay = isSource ? '' : 'none';
        if (state.role.style.display !== roleDisplay) {
            state.role.style.display = roleDisplay;
        }
        if ((card.dataset.secoursesRole === 'source') !== isSource) {
            if (isSource) {
                card.dataset.secoursesRole = 'source';
            }
            else {
                delete card.dataset.secoursesRole;
            }
        }
        let parts = [];
        let duration = parseFloat(media.dataset.duration);
        if (Number.isFinite(duration)) {
            parts.push(secoursesFormatDuration(duration));
        }
        if (media.dataset.resolution) {
            parts.push(media.dataset.resolution.replace('x', '×'));
        }
        setText(state.meta, parts.join(' · '));
        // The MiniMax H3 uploader keeps the user's name on the container and a random storage name on the media element.
        let rawFilename = card.dataset.filename || media.dataset.filename || '';
        let filename = secoursesDisplayFilename(rawFilename);
        setText(state.name, filename || (media.dataset.filedata?.startsWith('data:') ? 'pasted / dropped file' : ''));
        state.name.title = rawFilename;
        let title = media.title || `${info.label} ${index}${filename ? `: ${filename}` : ''}`;
        if (card.title !== title) {
            card.title = title;
        }
    }

    updateStatus(counts, a2v) {
        let total = counts.image + counts.video + counts.audio;
        let parts = [];
        for (let type of ['image', 'video', 'audio']) {
            if (counts[type] > 0) {
                let label = SECoursesPromptMediaTypes[type].label.toLowerCase();
                parts.push(`${counts[type]} ${label}${counts[type] > 1 && type !== 'audio' ? 's' : ''}`);
            }
        }
        let statusText = total > 0
            ? `${parts.join(' · ')} attached · drag cards to reorder`
            : 'Drop, paste, or add image / video / audio files to attach them to the prompt';
        if (this.status.textContent !== statusText) {
            this.status.textContent = statusText;
        }
        this.status.classList.toggle('secourses-prompt-media-status-empty', total === 0);
        let clearDisplay = total > 0 ? '' : 'none';
        if (this.clearAllButton.style.display !== clearDisplay) {
            this.clearAllButton.style.display = clearDisplay;
        }
        let hintText = '';
        let hintLevel = '';
        if (a2v) {
            if (counts.audio > 0) {
                hintText = `LTX 2.5 Audio To Video follows Audio 1${counts.audio > 1 ? ' (drag another audio card first to use it instead)' : ''}`;
                hintLevel = 'ok';
                this.hint.title = 'The first audio attachment is the source audio: the video length and lipsync follow it, and the untouched waveform is muxed into the output.';
            }
            else {
                hintText = 'LTX 2.5 Audio To Video needs a source audio: add it here (or set Video Audio Input)';
                hintLevel = 'warn';
                this.hint.title = 'Attach the speech, singing, or music file the video should follow.';
            }
        }
        if (this.hint.textContent !== hintText) {
            this.hint.textContent = hintText;
        }
        this.hint.dataset.level = hintLevel;
        let hintDisplay = hintText ? '' : 'none';
        if (this.hint.style.display !== hintDisplay) {
            this.hint.style.display = hintDisplay;
        }
    }

    // ==================== Adding files ====================

    /** Attaches files through the core's own imagePromptAddImageData, with extension-based type detection for
     * files the browser reports without a MIME type. */
    async addFiles(files) {
        let rejected = [];
        for (let file of files) {
            let type = secoursesMediaTypeOf(file);
            if (!type) {
                rejected.push(file.name);
                continue;
            }
            try {
                let data = await secoursesReadFileAsDataUrl(file);
                imagePromptAddImageData(data, type, data, file.name);
            }
            catch (error) {
                rejected.push(`${file.name} (${error?.message || error})`);
            }
        }
        if (rejected.length) {
            showError(`Only image, video, and audio files can be attached to the prompt. Not added: ${rejected.join(', ')}`);
        }
    }

    // ==================== Add with trim ====================

    openTrimPopup(file) {
        let type = secoursesMediaTypeOf(file);
        if (type !== 'video' && type !== 'audio') {
            showError('Trim works on video or audio files. Add images with the Add Image / Video / Audio button.');
            return;
        }
        new SECoursesTrimPopup(file, {
            type: type,
            title: type === 'video' ? '✂ Trim Video Attachment' : '✂ Trim Audio Attachment',
            addLabel: '➕ Attach',
            addTitle: 'Attach this file to the prompt. Only the selected window is used.',
            onAdd: (popup) => this.confirmTrim(popup),
        });
    }

    /** Audio is sliced to a WAV in the browser; video is trimmed on the server (SwarmUI's video editor API) and the
     * saved inputs/ file is attached, so nothing is re-encoded in the browser. */
    async confirmTrim(popup) {
        popup.setBusy(true);
        try {
            if (!popup.trimmed()) {
                await this.addFiles([popup.file]);
                popup.close();
                return;
            }
            if (popup.type === 'audio') {
                popup.setNote('Rendering trimmed audio…');
                let base = popup.file.name.replace(/\.[^.]+$/, '');
                let wav = await SECoursesTrimPopup.sliceAudioToWav(popup.file, popup.start, popup.end,
                    `${base} [${popup.start.toFixed(2)}s-${popup.end.toFixed(2)}s].wav`);
                await this.addFiles([wav]);
                popup.close();
                return;
            }
            popup.setNote('Uploading and trimming the video on the server…');
            let data = await secoursesReadFileAsDataUrl(popup.file);
            let endMilliseconds = Math.abs(popup.end - popup.duration) < 0.001 ? -1 : Math.round(popup.end * 1000);
            let request = {
                video: data,
                filename: popup.file.name,
                startMilliseconds: Math.round(popup.start * 1000),
                endMilliseconds: endMilliseconds,
            };
            await new Promise((resolve, reject) => {
                genericRequest('EditVideo', request, (result) => {
                    imagePromptAddImageData(`${getImageOutPrefix()}/${result.result}`, 'video', result.result, result.result);
                    if (typeof inputBrowserHelper !== 'undefined' && inputBrowserHelper.inputImageBrowser) {
                        inputBrowserHelper.inputImageBrowser.lightRefresh();
                    }
                    resolve();
                }, 0, (error) => reject(new Error(`${error}`)));
            });
            popup.close();
        }
        catch (error) {
            popup.setBusy(false);
            popup.setNote(`Failed: ${error?.message || error}`);
        }
    }

    // ==================== Card drag & drop reordering ====================

    bindCardDrag(card) {
        if (card.dataset.secoursesDragBound) {
            return;
        }
        card.dataset.secoursesDragBound = 'true';
        card.draggable = true;
        card.addEventListener('dragstart', (event) => {
            if (this.isH3Active() || !this.cardState.has(card)) {
                return;
            }
            let state = this.cardState.get(card);
            this.dragContext = { card: card, type: state.type };
            event.dataTransfer.effectAllowed = 'move';
            event.dataTransfer.setData('application/x-secourses-prompt-media', state.type);
            card.classList.add('secourses-media-card-dragging');
        });
        card.addEventListener('dragend', () => {
            this.dragContext = null;
            this.clearDropMarkers();
            card.classList.remove('secourses-media-card-dragging');
        });
    }

    /** Cards of the dragged type, in display order. */
    reorderPeers() {
        return this.cards().filter(card => this.cardState.get(card)?.type === this.dragContext.type);
    }

    reorderInsertPos(event) {
        let pos = 0;
        for (let card of this.reorderPeers()) {
            let rect = card.getBoundingClientRect();
            if (event.clientY > rect.bottom || (event.clientY >= rect.top && event.clientX > rect.left + rect.width / 2)) {
                pos++;
            }
        }
        return pos;
    }

    updateDropMarker(insertPos) {
        this.clearDropMarkers();
        let peers = this.reorderPeers();
        if (!peers.length) {
            return;
        }
        if (insertPos < peers.length) {
            peers[insertPos].classList.add('secourses-media-drop-before');
        }
        else {
            peers[peers.length - 1].classList.add('secourses-media-drop-after');
        }
    }

    clearDropMarkers() {
        for (let element of this.area.querySelectorAll('.secourses-media-drop-before, .secourses-media-drop-after')) {
            element.classList.remove('secourses-media-drop-before', 'secourses-media-drop-after');
        }
    }

    applyReorder(insertPos) {
        let dragged = this.dragContext.card;
        let all = this.reorderPeers();
        let peers = all.filter(card => card !== dragged);
        if (insertPos > all.indexOf(dragged)) {
            insertPos--;
        }
        insertPos = Math.max(0, Math.min(peers.length, insertPos));
        if (insertPos < peers.length) {
            if (peers[insertPos] !== dragged.nextElementSibling) {
                this.area.insertBefore(dragged, peers[insertPos]);
            }
        }
        else if (peers.length && peers[peers.length - 1] !== dragged.previousElementSibling) {
            peers[peers.length - 1].after(dragged);
        }
        // DOM order is the order the core sends promptimages / promptvideos / promptaudios in.
        if (typeof updatePromptMediaTitles === 'function') {
            updatePromptMediaTitles();
        }
        if (typeof persistPromptMediaParams === 'function') {
            persistPromptMediaParams();
        }
        this.scheduleSync();
    }
}

let secoursesPromptMedia = new SECoursesPromptMedia();
if (typeof sessionReadyCallbacks !== 'undefined') {
    sessionReadyCallbacks.push(() => secoursesPromptMedia.register());
}
