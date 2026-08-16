
/** Static per-type data for MiniMax H3 prompt references: '@' aliases, legacy label names, and pill color palettes. */
const MiniMaxH3ReferenceTypes = {
    image: {
        aliases: ['image', 'img', 'picture', 'pic'],
        label: 'Picture',
        colors: ['#4dabf7', '#ffa94d', '#69db7c', '#f783ac', '#b197fc', '#ffd43b', '#3bc9db', '#ff8787', '#a9e34b'],
    },
    video: {
        aliases: ['video', 'vid'],
        label: 'Video',
        colors: ['#ff6b6b', '#748ffc', '#38d9a9'],
    },
    audio: {
        aliases: ['audio', 'aud', 'sound'],
        label: 'Audio',
        colors: ['#fcc419', '#da77f2', '#66d9e8'],
    },
};

/** Maps any '@' alias (eg 'img') back to its canonical reference type (eg 'image'). */
const MiniMaxH3AliasToType = {};
for (let type in MiniMaxH3ReferenceTypes) {
    for (let alias of MiniMaxH3ReferenceTypes[type].aliases) {
        MiniMaxH3AliasToType[alias] = type;
    }
}

/** HTML-escape for the white-space:pre-wrap prompt overlay. SwarmUI's global escapeHtml()
 * keeps '\n' AND appends '<br>', which doubles every line break under pre-wrap and shifts
 * the mirrored text further down than the real textarea line by line. */
function minimaxH3EscapeText(text) {
    return text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;').replaceAll("'", '&#039;');
}

/** Gives backend file payloads an ASCII-only, cross-platform name while the UI keeps the original name. */
function minimaxH3StorageFilename(filename) {
    let words = new Uint32Array(4);
    if (globalThis.crypto?.getRandomValues) {
        globalThis.crypto.getRandomValues(words);
    }
    else {
        words[0] = Date.now();
        words[1] = Math.random() * 0xffffffff;
    }
    let id = Array.from(words, word => word.toString(16).padStart(8, '0')).join('');
    let extension = /\.([a-zA-Z0-9]{1,10})$/.exec(String(filename))?.[1]?.toLowerCase();
    return `minimax-h3-reference-${id}${extension ? `.${extension}` : ''}`;
}

class MiniMaxH3PromptReferences {
    constructor() {
        this.maxImages = 9;
        this.maxVideos = 3;
        this.maxAudios = 3;
        this.videoInputIds = [
            'input_minimaxhreferencevideoone',
            'input_minimaxhreferencevideotwo',
            'input_minimaxhreferencevideothree',
        ];
        this.audioInputIds = [
            'input_minimaxhreferenceaudioone',
            'input_minimaxhreferenceaudiotwo',
            'input_minimaxhreferenceaudiothree',
        ];
        this.overlay = null;
        this.overlayActive = false;
        this.suggestPopover = null;
        this.dragContext = null;
        this.syncQueued = false;
        this.trimsInputId = 'input_minimaxhreferencevideotrims';
        this.trimPopup = null;
    }

    /** Schedules a single syncAll on the next frame, coalescing bursts of DOM changes. */
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

    /** Revokes blob preview URLs of removed reference cards so they don't leak. */
    releaseRemovedPreviews(mutations) {
        for (let mutation of mutations) {
            for (let node of mutation.removedNodes) {
                if (node.nodeType !== Node.ELEMENT_NODE) {
                    continue;
                }
                for (let media of node.querySelectorAll('img, video, audio')) {
                    if (media.src && media.src.startsWith('blob:')) {
                        URL.revokeObjectURL(media.src);
                    }
                }
            }
        }
    }

    /** Builds a small thumbnail blob URL so cards never carry a full-resolution decode.
     * Returns null when the image cannot be decoded (caller falls back to the data URL). */
    async makeImageThumbnail(file, maxEdge = 384) {
        try {
            const bitmap = await createImageBitmap(file);
            const scale = Math.min(1, maxEdge / Math.max(bitmap.width, bitmap.height));
            const canvas = document.createElement('canvas');
            canvas.width = Math.max(1, Math.round(bitmap.width * scale));
            canvas.height = Math.max(1, Math.round(bitmap.height * scale));
            canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
            bitmap.close();
            return await new Promise(resolve => canvas.toBlob(
                blob => resolve(blob ? URL.createObjectURL(blob) : null), 'image/jpeg', 0.85));
        }
        catch (error) {
            return null;
        }
    }

    register() {
        if (document.getElementById('minimax_h3_prompt_reference_toolbar')) {
            return;
        }
        this.region = document.getElementById('alt_prompt_region');
        this.extraArea = document.getElementById('alt_prompt_extra_area');
        this.referenceArea = document.getElementById('alt_prompt_image_area');
        this.promptBox = document.getElementById('alt_prompt_textbox');
        this.addButton = document.getElementById('alt_text_add_button');
        this.clearButton = document.getElementById('alt_prompt_image_clear_button');
        this.enabledInput = document.getElementById('input_minimaxhreferences');
        this.modelInput = document.getElementById('current_model');
        this.backendModelInput = document.getElementById('input_model');
        if (!this.region || !this.extraArea || !this.referenceArea || !this.promptBox || !this.addButton) {
            return;
        }

        this.createToolbar();
        this.createOverlay();
        this.bindEvents();
        this.observer = new MutationObserver(mutations => {
            this.releaseRemovedPreviews(mutations);
            this.scheduleSync();
        });
        this.observer.observe(this.referenceArea, { childList: true });
        this.enabledInput?.addEventListener('change', () => this.updateActiveState());
        this.modelInput?.addEventListener('change', () => this.handleModelChange());
        this.backendModelInput?.addEventListener('change', () => this.handleModelChange());
        this.deactivateForOtherModels();
        this.syncAll();
        this.updateActiveState();
    }

    createToolbar() {
        this.toolbar = document.createElement('div');
        this.toolbar.id = 'minimax_h3_prompt_reference_toolbar';
        this.toolbar.className = 'minimax-h3-prompt-reference-toolbar';

        this.uploadButton = document.createElement('button');
        this.uploadButton.type = 'button';
        this.uploadButton.className = 'basic-button minimax-h3-prompt-reference-upload';
        this.uploadButton.textContent = 'Add References';
        this.uploadButton.title = 'Add MiniMax H3 prompt images, videos, or audio';

        this.trimUploadButton = document.createElement('button');
        this.trimUploadButton.type = 'button';
        this.trimUploadButton.className = 'basic-button minimax-h3-prompt-reference-trim-upload';
        this.trimUploadButton.textContent = '✂ Add A Reference With Trim';
        this.trimUploadButton.title = 'Add one video or audio reference and pick the exact start/end window to use from it';

        this.status = document.createElement('span');
        this.status.className = 'minimax-h3-prompt-reference-status';
        this.status.setAttribute('aria-live', 'polite');

        this.soundtrackHint = document.createElement('span');
        this.soundtrackHint.className = 'minimax-h3-prompt-reference-hint';
        this.soundtrackHint.textContent = "For @video1's soundtrack, type <Audio 1>; @audio1 is the first standalone audio file";
        this.soundtrackHint.title = "A reference video's soundtrack uses the native <Audio N> label. Standalone @audioN tokens always refer to standalone audio attachments and are offset after video soundtracks automatically.";

        this.hint = document.createElement('span');
        this.hint.className = 'minimax-h3-prompt-reference-hint';
        this.hint.textContent = 'Type @ in the prompt to reference';
        this.hint.title = 'Type @ in the prompt for reference autocomplete, eg "@image1". Click any attachment to insert its token.';
        this.hint.style.display = 'none';

        this.fileInput = document.createElement('input');
        this.fileInput.type = 'file';
        this.fileInput.multiple = true;
        this.fileInput.accept = 'image/*,video/*,audio/*';
        this.fileInput.className = 'minimax-h3-prompt-reference-file-input';
        this.fileInput.setAttribute('aria-label', 'Add MiniMax H3 prompt references');

        this.trimFileInput = document.createElement('input');
        this.trimFileInput.type = 'file';
        this.trimFileInput.accept = 'video/*,audio/*';
        this.trimFileInput.className = 'minimax-h3-prompt-reference-file-input';
        this.trimFileInput.setAttribute('aria-label', 'Add one MiniMax H3 reference with trim');

        this.toolbar.append(this.uploadButton, this.trimUploadButton, this.status, this.soundtrackHint, this.hint, this.fileInput, this.trimFileInput);
        this.extraArea.prepend(this.toolbar);
    }

    bindEvents() {
        this.uploadButton.addEventListener('click', () => this.fileInput.click());
        this.fileInput.addEventListener('change', async () => {
            await this.addFiles([...this.fileInput.files]);
            this.fileInput.value = '';
        });
        this.trimUploadButton.addEventListener('click', () => this.trimFileInput.click());
        this.trimFileInput.addEventListener('change', () => {
            let file = this.trimFileInput.files[0];
            this.trimFileInput.value = '';
            if (file) {
                this.openTrimPopup(file);
            }
        });

        this.addButton.addEventListener('click', (event) => {
            if (!this.isActive()) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            this.fileInput.click();
        }, true);

        this.region.addEventListener('dragover', (event) => {
            if (!this.isActive() || !this.supportedFiles(event.dataTransfer?.files).length) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            this.region.classList.add('minimax-h3-prompt-reference-dragging');
        }, true);
        this.region.addEventListener('dragleave', (event) => {
            if (!this.region.contains(event.relatedTarget)) {
                this.region.classList.remove('minimax-h3-prompt-reference-dragging');
            }
        }, true);
        this.region.addEventListener('drop', async (event) => {
            let files = this.supportedFiles(event.dataTransfer?.files);
            if (!this.isActive() || !files.length) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            this.region.classList.remove('minimax-h3-prompt-reference-dragging');
            await this.addFiles(files);
        }, true);

        // Internal card reordering. These run in the capture phase on the
        // reference area itself; file drags never reach them with a drag
        // context, and internal drags carry no files so the handlers above
        // (and SwarmUI's own image-drop logic) ignore them.
        this.referenceArea.addEventListener('dragover', (event) => {
            if (!this.dragContext) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            event.dataTransfer.dropEffect = 'move';
            this.updateDropMarker(this.reorderInsertPos(event));
        }, true);
        this.referenceArea.addEventListener('drop', (event) => {
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

        // The prompt box's inline onpaste handler runs before any listener added here can
        // cancel it, so swap it for a wrapper that defers to this extension while active.
        let corePasteHandler = this.promptBox.onpaste;
        this.promptBox.onpaste = (event) => {
            if (this.isActive()) {
                return;
            }
            return corePasteHandler ? corePasteHandler.call(this.promptBox, event) : undefined;
        };
        this.promptBox.addEventListener('paste', async (event) => {
            if (!this.isActive()) {
                return;
            }
            let files = [...(event.clipboardData?.items || [])]
                .filter(item => item.kind === 'file')
                .map(item => item.getAsFile())
                .filter(file => file && this.mediaType(file));
            if (!files.length) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            await this.addFiles(files);
        }, true);

        this.promptBox.addEventListener('input', () => this.onPromptInput());
        this.promptBox.addEventListener('scroll', () => this.syncOverlayScroll());
        new ResizeObserver(() => this.renderOverlay()).observe(this.promptBox);
        this.region.addEventListener('keydown', (event) => this.onSuggestKeydown(event), true);
    }

    isMiniMaxH3Model() {
        return typeof currentModelHelper !== 'undefined'
            && currentModelHelper.curCompatClass === 'minimax-h3';
    }

    isActive() {
        return this.isMiniMaxH3Model();
    }

    handleModelChange() {
        this.deactivateForOtherModels();
        this.updateActiveState();
        window.setTimeout(() => {
            this.enabledInput = document.getElementById('input_minimaxhreferences');
            this.deactivateForOtherModels();
            this.updateActiveState();
        }, 0);
    }

    deactivateForOtherModels() {
        if (this.isMiniMaxH3Model()) {
            return;
        }
        if (this.enabledInput?.checked) {
            this.enabledInput.checked = false;
            triggerChangeFor(this.enabledInput);
        }
        for (let reference of [...this.references('video'), ...this.references('audio')]) {
            reference.remove();
        }
    }

    updateActiveState() {
        let active = this.isActive();
        this.toolbar.style.display = active ? 'flex' : 'none';
        this.region.classList.toggle('minimax-h3-prompt-references-active', active);
        this.addButton.title = active
            ? 'Add MiniMax H3 prompt images, videos, or audio'
            : '';
        if (this.clearButton) {
            this.clearButton.textContent = active ? 'Clear references' : 'Clear Images';
        }
        for (let id of [...this.videoInputIds, ...this.audioInputIds, this.trimsInputId]) {
            let input = document.getElementById(id);
            let parent = input ? findParentOfClass(input, 'auto-input') : null;
            if (parent) {
                parent.classList.toggle('minimax-h3-internal-reference-input', active);
            }
        }
        if (!active) {
            this.closeSuggestions();
        }
        this.syncAll();
    }

    ensureEnabled() {
        if (this.enabledInput && !this.enabledInput.checked) {
            this.enabledInput.checked = true;
            triggerChangeFor(this.enabledInput);
        }
    }

    supportedFiles(fileList) {
        return [...(fileList || [])].filter(file => this.mediaType(file));
    }

    mediaType(file) {
        if (file.type.startsWith('image/')) {
            return 'image';
        }
        if (file.type.startsWith('video/')) {
            return 'video';
        }
        if (file.type.startsWith('audio/')) {
            return 'audio';
        }
        let extension = file.name.toLowerCase().split('.').pop();
        if (['png', 'jpg', 'jpeg', 'webp', 'gif'].includes(extension)) {
            return 'image';
        }
        if (['mp4', 'webm', 'mov', 'm4v'].includes(extension)) {
            return 'video';
        }
        if (['wav', 'mp3', 'aac', 'ogg', 'flac', 'm4a'].includes(extension)) {
            return 'audio';
        }
        return null;
    }

    count(type) {
        return this.collectReferences()[type].length;
    }

    async addFiles(files) {
        this.ensureEnabled();
        let rejected = [];
        for (let file of files) {
            let type = this.mediaType(file);
            let limit = type === 'image' ? this.maxImages
                : type === 'video' ? this.maxVideos : this.maxAudios;
            if (!type || this.count(type) >= limit) {
                rejected.push(file.name);
                continue;
            }
            let data = await this.readFile(file);
            let storageFilename = minimaxH3StorageFilename(file.name);
            if (type === 'image') {
                // A small thumbnail keeps the card (and its drag ghost, and every
                // DOM move) light; the full-resolution data URL only lives in
                // dataset.filedata for the generation request.
                let preview = await this.makeImageThumbnail(file);
                this.addImage(data, file.name, preview, storageFilename);
            }
            else {
                this.addMedia(data, file.name, type, URL.createObjectURL(file), storageFilename);
            }
        }
        this.syncAll();
        if (rejected.length) {
            showError(`MiniMax H3 reference limits are ${this.maxImages} images, ${this.maxVideos} videos, and ${this.maxAudios} audio files. Not added: ${rejected.join(', ')}`);
        }
    }

    readFile(file) {
        return new Promise((resolve, reject) => {
            let reader = new FileReader();
            reader.onload = () => resolve(reader.result);
            reader.onerror = () => reject(reader.error);
            reader.readAsDataURL(file);
        });
    }

    addImage(data, filename, preview = null, storageFilename = minimaxH3StorageFilename(filename)) {
        let container = document.createElement('div');
        container.className = 'alt-prompt-image-container';
        container.dataset.filename = filename;
        container.dataset.storageFilename = storageFilename;

        let remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'alt-prompt-image-container-remove-button';
        remove.innerHTML = '&times;';
        remove.title = 'Remove this prompt reference';
        remove.addEventListener('click', () => {
            container.remove();
            autoRevealRevision();
        });

        let image = new Image();
        image.src = preview || data;
        image.height = 128;
        image.className = 'alt-prompt-image';
        image.dataset.filedata = data;
        image.dataset.filename = storageFilename;
        container.append(remove, image);
        this.referenceArea.appendChild(container);
        this.showReferenceArea();
        showRevisionInputs(true);
    }

    addMedia(data, filename, type, preview = null, storageFilename = minimaxH3StorageFilename(filename)) {
        let container = document.createElement('div');
        container.className = 'minimax-h3-prompt-reference';
        container.dataset.referenceType = type;
        container.dataset.filedata = data;
        container.dataset.filename = filename;
        container.dataset.storageFilename = storageFilename;

        let remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'alt-prompt-image-container-remove-button';
        remove.innerHTML = '&times;';
        remove.title = `Remove this prompt ${type} reference`;
        remove.addEventListener('click', () => {
            container.remove();
            autoRevealRevision();
        });

        let media = document.createElement(type);
        media.className = 'minimax-h3-prompt-reference-preview';
        media.src = preview || data;
        media.controls = true;
        media.preload = 'metadata';
        if (type === 'video') {
            media.muted = true;
        }

        let label = document.createElement('span');
        label.className = 'minimax-h3-prompt-reference-label';
        let name = document.createElement('span');
        name.className = 'minimax-h3-prompt-reference-name';
        name.textContent = filename;
        name.title = filename;
        container.append(remove, media, label, name);
        this.referenceArea.appendChild(container);
        this.showReferenceArea();
        return container;
    }

    showReferenceArea() {
        if (this.clearButton) {
            this.clearButton.style.display = '';
        }
        if (typeof genTabLayout !== 'undefined') {
            genTabLayout.altPromptSizeHandle();
        }
    }

    references(type) {
        return [...this.referenceArea.querySelectorAll(
            `.minimax-h3-prompt-reference[data-reference-type="${type}"]`,
        )];
    }

    /** Returns the current reference container elements, in slot order, keyed by type. */
    collectReferences() {
        return {
            image: [...this.referenceArea.querySelectorAll('img.alt-prompt-image')]
                .map(img => img.closest('.alt-prompt-image-container')).filter(c => c),
            video: this.references('video'),
            audio: this.references('audio'),
        };
    }

    /** Returns the pill color for the given reference type and 1-based number. */
    colorFor(type, n) {
        let colors = MiniMaxH3ReferenceTypes[type].colors;
        return colors[(n - 1) % colors.length];
    }

    tokenFor(type, n) {
        return `@${type}${n}`;
    }

    /** Returns a flat descriptor list of all current references, for autocomplete and the overlay. */
    referenceEntries() {
        let entries = [];
        let current = this.collectReferences();
        for (let type of ['image', 'video', 'audio']) {
            current[type].forEach((container, index) => {
                let n = index + 1;
                let image = container.querySelector('.alt-prompt-image');
                entries.push({
                    type: type,
                    n: n,
                    token: this.tokenFor(type, n),
                    color: this.colorFor(type, n),
                    filename: container.dataset.filename || image?.dataset.filename || '',
                    thumbSrc: image ? image.src : null,
                    keys: MiniMaxH3ReferenceTypes[type].aliases.map(alias => `${alias}${n}`),
                });
            });
        }
        return entries;
    }

    syncAll() {
        if (!this.referenceArea || !this.status) {
            return;
        }
        // The prompt text is never modified when references are removed or
        // reordered: tokens renumber visually by position, and any token left
        // pointing past the attachment list is shown as inactive and omitted
        // at generation time by the backend instead of erroring.
        let current = this.collectReferences();
        current.image.forEach((container, index) => {
            let n = index + 1;
            container.dataset.referenceLabel = this.tokenFor('image', n);
            container.style.setProperty('--minimax-ref-color', this.colorFor('image', n));
            this.bindCardInsert(container, 'image');
            this.bindCardDrag(container, 'image');
        });
        for (let type of ['video', 'audio']) {
            current[type].forEach((container, index) => {
                let n = index + 1;
                let label = container.querySelector('.minimax-h3-prompt-reference-label');
                if (label) {
                    label.textContent = this.tokenFor(type, n);
                }
                container.style.setProperty('--minimax-ref-color', this.colorFor(type, n));
                this.bindCardInsert(container, type);
                this.bindCardDrag(container, type);
            });
        }
        this.syncSlots(current.video, this.videoInputIds);
        this.syncSlots(current.audio, this.audioInputIds);
        this.syncVideoTrims(current.video);
        this.status.textContent = `${current.image.length}/${this.maxImages} images | ${current.video.length}/${this.maxVideos} videos | ${current.audio.length}/${this.maxAudios} audio`;
        let total = current.image.length + current.video.length + current.audio.length;
        this.hint.style.display = total > 0 ? '' : 'none';
        if (this.clearButton && this.isActive()) {
            this.clearButton.textContent = 'Clear references';
        }
        this.renderOverlay();
        if (typeof genTabLayout !== 'undefined') {
            genTabLayout.altPromptSizeHandle();
        }
    }

    /** Attaches (once) a click-to-insert-token handler to a reference card. */
    bindCardInsert(container, type) {
        if (container.dataset.minimaxInsertBound) {
            return;
        }
        container.dataset.minimaxInsertBound = 'true';
        container.addEventListener('click', (event) => {
            if (!this.isActive()) {
                return;
            }
            if (event.target.closest('button, video, audio')) {
                return;
            }
            let list = this.collectReferences()[type];
            let n = list.indexOf(container) + 1;
            if (n < 1) {
                return;
            }
            this.insertToken(type, n);
        });
        if (!container.title) {
            container.title = 'Click to insert this reference into the prompt. Drag left/right to reorder; tokens renumber by position.';
        }
    }

    /** Inserts a reference token at the current prompt cursor position. */
    insertToken(type, n) {
        let box = this.promptBox;
        let token = this.tokenFor(type, n);
        let start = box.selectionStart ?? box.value.length;
        let end = box.selectionEnd ?? box.value.length;
        let before = box.value.substring(0, start);
        let after = box.value.substring(end);
        let insert = token;
        if (before.length && !/[\s([{"']$/.test(before)) {
            insert = ' ' + insert;
        }
        if (!/^[\s,.)\]}!?;:]/.test(after)) {
            insert = insert + ' ';
        }
        box.value = before + insert + after;
        let position = before.length + insert.length;
        box.focus();
        box.setSelectionRange(position, position);
        box.dispatchEvent(new Event('input'));
    }

    // ==================== Card drag & drop reordering ====================

    /** Attaches (once) drag-to-reorder behavior to a reference card. */
    bindCardDrag(container, type) {
        if (container.dataset.minimaxDragBound) {
            return;
        }
        container.dataset.minimaxDragBound = 'true';
        container.draggable = true;
        container.addEventListener('dragstart', (event) => {
            if (!this.isActive()) {
                return;
            }
            this.dragContext = { container: container, type: type };
            event.dataTransfer.effectAllowed = 'move';
            event.dataTransfer.setData('application/x-minimax-h3-reference', type);
            container.classList.add('minimax-h3-reference-dragging');
        });
        container.addEventListener('dragend', () => {
            this.dragContext = null;
            this.clearDropMarkers();
            container.classList.remove('minimax-h3-reference-dragging');
        });
        for (let media of container.querySelectorAll('img, video, audio')) {
            media.draggable = false;
        }
    }

    /** Containers of the same type as the current drag, in display order. */
    reorderPeers() {
        return this.collectReferences()[this.dragContext.type];
    }

    /** Insertion slot [0..n] among same-type containers for the pointer position. */
    reorderInsertPos(event) {
        let pos = 0;
        for (let container of this.reorderPeers()) {
            let rect = container.getBoundingClientRect();
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
            peers[insertPos].classList.add('minimax-h3-reference-drop-before');
        }
        else {
            peers[peers.length - 1].classList.add('minimax-h3-reference-drop-after');
        }
    }

    clearDropMarkers() {
        for (let elem of this.referenceArea.querySelectorAll('.minimax-h3-reference-drop-before, .minimax-h3-reference-drop-after')) {
            elem.classList.remove('minimax-h3-reference-drop-before', 'minimax-h3-reference-drop-after');
        }
    }

    /** Moves the dragged card so it sits at the given slot among cards of its type. */
    applyReorder(insertPos) {
        let dragged = this.dragContext.container;
        let peers = this.reorderPeers().filter(container => container !== dragged);
        if (insertPos > this.reorderPeers().indexOf(dragged)) {
            insertPos--;
        }
        insertPos = Math.max(0, Math.min(peers.length, insertPos));
        if (insertPos < peers.length) {
            this.referenceArea.insertBefore(dragged, peers[insertPos]);
        }
        else if (peers.length) {
            peers[peers.length - 1].after(dragged);
        }
        // The childList MutationObserver schedules the (coalesced) resync.
    }

    // ==================== Prompt pill overlay ====================

    createOverlay() {
        this.overlay = document.createElement('div');
        this.overlay.id = 'minimax_h3_prompt_highlight';
        this.overlay.className = 'minimax-h3-prompt-highlight';
        this.overlay.setAttribute('aria-hidden', 'true');
        this.overlay.style.display = 'none';
        // The text lives in an inner element that is moved with a transform to
        // mirror the textarea's scroll position. Unlike scrollTop, a transform
        // never clamps, so the overlay can never end up scrolled out of sync
        // while SwarmUI's auto-size logic is mid-flight.
        this.overlayInner = document.createElement('div');
        this.overlayInner.className = 'minimax-h3-prompt-highlight-inner';
        this.overlay.appendChild(this.overlayInner);
        this.promptBox.parentElement.appendChild(this.overlay);
        document.fonts?.ready?.then(() => this.renderOverlay());
        window.addEventListener('resize', () => this.renderOverlay());
    }

    setOverlayActive(active) {
        if (this.overlayActive === active) {
            return;
        }
        this.overlayActive = active;
        this.promptBox.classList.toggle('minimax-h3-highlight-on', active);
        this.promptBox.parentElement.classList.toggle('minimax-h3-overlay-host', active);
        this.overlay.style.display = active ? '' : 'none';
    }

    syncOverlayScroll() {
        if (!this.overlayActive) {
            return;
        }
        this.overlayInner.style.transform = `translate(${-this.promptBox.scrollLeft}px, ${-this.promptBox.scrollTop}px)`;
    }

    /** Mirrors the textarea's exact text metrics and geometry onto the overlay. */
    syncOverlayMetrics() {
        let box = this.promptBox;
        let inner = this.overlayInner;
        let cs = getComputedStyle(box);
        // Every property that can influence glyph metrics or line wrapping must
        // match the textarea exactly, or the overlay's text drifts away from
        // the real (invisible) text and the caret appears misplaced.
        for (let prop of ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'fontVariant', 'fontStretch', 'fontKerning',
            'fontFeatureSettings', 'fontVariantLigatures', 'fontVariantNumeric', 'fontVariantCaps', 'fontVariantEastAsian',
            'fontOpticalSizing', 'fontSizeAdjust', 'letterSpacing', 'wordSpacing', 'lineHeight', 'textTransform',
            'textIndent', 'textRendering', 'tabSize', 'direction', 'textAlign', 'whiteSpace', 'overflowWrap',
            'wordBreak', 'hyphens', 'unicodeBidi', 'writingMode', 'textOrientation']) {
            inner.style[prop] = cs[prop];
        }
        inner.style.paddingTop = cs.paddingTop;
        inner.style.paddingRight = cs.paddingRight;
        inner.style.paddingBottom = cs.paddingBottom;
        inner.style.paddingLeft = cs.paddingLeft;
        let host = box.parentElement;
        let hostRect = host.getBoundingClientRect();
        let rect = box.getBoundingClientRect();
        let borderLeft = parseFloat(cs.borderLeftWidth) || 0;
        let borderRight = parseFloat(cs.borderRightWidth) || 0;
        let borderTop = parseFloat(cs.borderTopWidth) || 0;
        let borderBottom = parseFloat(cs.borderBottomWidth) || 0;
        // clientWidth already excludes any classic scrollbar, and rounds; use
        // it directly so the overlay's wrap width is the textarea's wrap width.
        let contentWidth = box.clientWidth;
        let contentHeight = box.clientHeight;
        this.overlay.style.left = `${rect.left - hostRect.left + borderLeft}px`;
        this.overlay.style.top = `${rect.top - hostRect.top + borderTop}px`;
        this.overlay.style.width = `${contentWidth}px`;
        this.overlay.style.height = `${contentHeight}px`;
        this.overlay.style.borderRadius = cs.borderRadius;
        inner.style.width = `${contentWidth}px`;
    }

    /** Re-renders the colored token pills behind the prompt text. */
    renderOverlay() {
        if (!this.overlay) {
            return;
        }
        let active = this.isActive();
        this.setOverlayActive(active);
        if (!active) {
            return;
        }
        this.syncOverlayMetrics();
        let text = this.promptBox.value;
        let current = this.collectReferences();
        let counts = {
            image: current.image.length,
            video: current.video.length,
            audio: current.audio.length,
        };
        let tokenRegex = /(?<![\w@])@(image|img|picture|pic|video|vid|audio|aud|sound)#?(\d{1,2})(?![0-9a-zA-Z])|<(Picture|Video|Audio)[ ]?(\d{1,2})>/gi;
        let html = '';
        let last = 0;
        let match;
        while ((match = tokenRegex.exec(text)) !== null) {
            html += minimaxH3EscapeText(text.substring(last, match.index));
            last = match.index + match[0].length;
            let type, n;
            let legacyAudio = false;
            if (match[1] !== undefined) {
                type = MiniMaxH3AliasToType[match[1].toLowerCase()];
                n = parseInt(match[2]);
            }
            else {
                let label = match[3].toLowerCase();
                type = label === 'picture' ? 'image' : label;
                n = parseInt(match[4]);
                legacyAudio = type === 'audio';
            }
            let valid, color = null;
            if (legacyAudio) {
                // Legacy <Audio n> labels index video soundtracks first, then standalone audio.
                if (n >= 1 && n <= counts.video) {
                    valid = true;
                    color = this.colorFor('video', n);
                }
                else if (n > counts.video && n <= counts.video + counts.audio) {
                    valid = true;
                    color = this.colorFor('audio', n - counts.video);
                }
                else {
                    valid = false;
                }
            }
            else {
                valid = n >= 1 && n <= counts[type];
                if (valid) {
                    color = this.colorFor(type, n);
                }
            }
            let cls = valid ? 'minimax-h3-token' : 'minimax-h3-token minimax-h3-token-invalid';
            let style = valid ? ` style="--minimax-ref-color:${color};"` : '';
            html += `<span class="${cls}"${style}>${minimaxH3EscapeText(match[0])}</span>`;
        }
        html += minimaxH3EscapeText(text.substring(last));
        this.overlayInner.innerHTML = html + String.fromCharCode(0x200b);
        this.syncOverlayScroll();
    }

    // ==================== '@' reference autocomplete ====================

    onPromptInput() {
        this.renderOverlay();
        if (!this.isActive() || document.activeElement !== this.promptBox) {
            this.closeSuggestions();
            return;
        }
        let context = this.getSuggestContext();
        if (!context) {
            this.closeSuggestions();
            return;
        }
        this.openSuggestions(context);
    }

    /** Returns {start, end, partial} when the text before the cursor ends in a partial '@' reference. */
    getSuggestContext() {
        let box = this.promptBox;
        let start = box.selectionStart;
        if (start == null || box.selectionEnd !== start) {
            return null;
        }
        let before = box.value.substring(0, start);
        let match = /(?<![\w@])@([a-zA-Z]{0,7}#?\d{0,2})$/.exec(before);
        if (!match) {
            return null;
        }
        return { start: start - match[0].length, end: start, partial: match[1] };
    }

    closeSuggestions() {
        if (this.suggestPopover) {
            this.suggestPopover.remove();
            this.suggestPopover = null;
        }
    }

    openSuggestions(context) {
        this.closeSuggestions();
        let entries = this.referenceEntries();
        if (!entries.length) {
            return;
        }
        let partial = context.partial.toLowerCase().replace('#', '');
        let matches = partial === '' ? entries
            : entries.filter(entry => entry.keys.some(key => key.startsWith(partial)));
        if (!matches.length) {
            return;
        }
        // The built-in tab-complete popover would overlap ours; ours takes priority for '@'.
        if (typeof promptTabComplete !== 'undefined' && promptTabComplete.popover) {
            promptTabComplete.popover.remove();
            promptTabComplete.popover = null;
        }
        let buttons = [{
            key: 'References',
            key_html: `<span class="minimax-h3-suggest-header">References</span>`,
            className: 'minimax-h3-suggest-header-row',
        }];
        for (let entry of matches) {
            let thumb = entry.thumbSrc
                ? `<img class="minimax-h3-suggest-thumb" src="${entry.thumbSrc}" style="border-color:${entry.color};"/>`
                : `<span class="minimax-h3-suggest-thumb minimax-h3-suggest-thumb-icon" style="border-color:${entry.color};color:${entry.color};">${entry.type === 'video' ? '▶' : '♪'}</span>`;
            buttons.push({
                key: entry.token,
                key_html: `${thumb}<span class="minimax-h3-suggest-token" style="color:${entry.color};">${entry.token}</span><span class="minimax-h3-suggest-name">${escapeHtml(entry.filename || '')}</span>`,
                className: 'minimax-h3-suggest-item',
                action: () => this.applyCompletion(context, entry),
            });
        }
        let rect = this.promptBox.getBoundingClientRect();
        this.suggestPopover = new AdvancedPopover('minimax_ref_suggest', buttons, false,
            rect.x, rect.y + this.promptBox.offsetHeight + 6, this.promptBox.parentElement,
            matches[0].token, this.promptBox.offsetHeight + 6, 400);
        // The popover estimates row heights at 1.3rem; our thumbnail rows are taller, so correct it.
        let remPixels = parseFloat(getComputedStyle(document.documentElement).fontSize);
        this.suggestPopover.blockHeight = remPixels * 1.9;
        this.suggestPopover.expectedHeight = buttons.length * this.suggestPopover.blockHeight;
        this.suggestPopover.reposition();
    }

    applyCompletion(context, entry) {
        let box = this.promptBox;
        let before = box.value.substring(0, context.start);
        let after = box.value.substring(context.end);
        let insert = entry.token + (/^[\s,.)\]}!?;:]/.test(after) ? '' : ' ');
        box.value = before + insert + after;
        let position = before.length + insert.length;
        box.focus();
        box.setSelectionRange(position, position);
        box.dispatchEvent(new Event('input'));
    }

    onSuggestKeydown(event) {
        if (this.suggestPopover && !this.suggestPopover.popover) {
            this.suggestPopover = null;
        }
        if (!this.suggestPopover || event.target !== this.promptBox) {
            return;
        }
        if (event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) {
            return;
        }
        if (!['Escape', 'Tab', 'Enter', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
            return;
        }
        this.suggestPopover.onKeyDown(event);
        if (['Escape', 'Tab', 'Enter'].includes(event.key)) {
            this.closeSuggestions();
        }
        event.preventDefault();
        event.stopImmediatePropagation();
    }

    syncSlots(references, inputIds) {
        inputIds.forEach((id, index) => {
            let input = document.getElementById(id);
            if (!input) {
                return;
            }
            let reference = references[index];
            if (reference) {
                // The file payloads are multi-megabyte base64 strings; only touch
                // the attribute when the slot actually changes.
                if (input.dataset.filedata !== reference.dataset.filedata) {
                    input.dataset.filedata = reference.dataset.filedata;
                }
                let storageFilename = reference.dataset.storageFilename || reference.dataset.filename;
                if (input.dataset.filename !== storageFilename) {
                    input.dataset.filename = storageFilename;
                }
                input.dataset.has_data = 'true';
            }
            else if (input.dataset.has_data) {
                delete input.dataset.filedata;
                delete input.dataset.filename;
                delete input.dataset.duration;
                delete input.dataset.resolution;
                delete input.dataset.has_data;
                input.value = '';
            }
            let parent = findParentOfClass(input, 'auto-input');
            let label = parent?.querySelector('.auto-file-input-filename');
            if (label && label.textContent !== (reference?.dataset.filename || '')) {
                label.textContent = reference?.dataset.filename || '';
            }
        });
    }

    // ==================== Single-reference trim popup ====================

    /** Mirrors card trim windows into the internal 'Reference Video Trims' text param,
     * as comma-separated '<slot>:<start>-<end>' seconds in current display order. */
    syncVideoTrims(videos) {
        let input = document.getElementById(this.trimsInputId);
        if (!input) {
            return;
        }
        let parts = [];
        videos.forEach((container, index) => {
            if (container.dataset.trimStart && container.dataset.trimEnd) {
                parts.push(`${index + 1}:${container.dataset.trimStart}-${container.dataset.trimEnd}`);
            }
        });
        let value = parts.join(',');
        if (input.value !== value) {
            input.value = value;
            triggerChangeFor(input);
        }
    }

    /** Minimum trim window; reference videos need a few frames at 24 FPS. */
    trimMinRange() {
        return this.trimPopup?.type === 'video' ? 0.25 : 0.05;
    }

    trimPopupIsTrimmed() {
        let popup = this.trimPopup;
        if (!popup?.duration) {
            return false;
        }
        return popup.start > 0.01 || popup.end < popup.duration - 0.01;
    }

    /** Opens the single-file trim popup for one video or audio reference. */
    openTrimPopup(file) {
        let type = this.mediaType(file);
        if (type !== 'video' && type !== 'audio') {
            showError('Trim works on video or audio references. Add images with the Add References button.');
            return;
        }
        let limit = type === 'video' ? this.maxVideos : this.maxAudios;
        if (this.count(type) >= limit) {
            showError(`MiniMax H3 supports at most ${limit} ${type} references. Remove one first.`);
            return;
        }
        this.closeTrimPopup();
        let modal = createDiv(null, 'minimax-h3-trim-modal');
        let panel = createDiv(null, 'minimax-h3-trim-panel');

        let head = createDiv(null, 'minimax-h3-trim-head');
        let title = document.createElement('span');
        title.className = 'minimax-h3-trim-title';
        title.textContent = '✂ Trim Reference';
        let name = document.createElement('span');
        name.className = 'minimax-h3-trim-filename';
        name.textContent = file.name;
        name.title = file.name;
        let close = document.createElement('button');
        close.type = 'button';
        close.className = 'minimax-h3-trim-close';
        close.innerHTML = '&times;';
        close.title = 'Close without adding';
        head.append(title, name, close);

        let preview = createDiv(null, 'minimax-h3-trim-preview');
        let element = document.createElement(type);
        element.className = `minimax-h3-trim-media minimax-h3-trim-media-${type}`;
        element.controls = true;
        element.preload = 'metadata';
        if (type === 'video') {
            element.playsInline = true;
        }
        let url = URL.createObjectURL(file);
        element.src = url;
        preview.appendChild(element);

        let track = createDiv(null, 'minimax-h3-trim-track');
        track.title = 'Click to seek the preview. Drag the handles to set the trim window.';
        let fill = createDiv(null, 'minimax-h3-trim-fill');
        let playhead = createDiv(null, 'minimax-h3-trim-playhead');
        let startHandle = createDiv(null, 'minimax-h3-trim-handle minimax-h3-trim-handle-start');
        startHandle.title = 'Drag to set the trim start';
        let endHandle = createDiv(null, 'minimax-h3-trim-handle minimax-h3-trim-handle-end');
        endHandle.title = 'Drag to set the trim end';
        track.append(fill, playhead, startHandle, endHandle);

        let fields = createDiv(null, 'minimax-h3-trim-fields');
        let makeTimeField = (labelText, titleText) => {
            let label = document.createElement('label');
            label.className = 'minimax-h3-trim-label';
            label.append(labelText);
            let input = document.createElement('input');
            input.type = 'number';
            input.className = 'minimax-h3-trim-input';
            input.min = '0';
            input.step = '0.05';
            input.title = titleText;
            label.appendChild(input);
            fields.appendChild(label);
            return input;
        };
        let startInput = makeTimeField('Start', 'Trim start in seconds');
        let endInput = makeTimeField('End', 'Trim end in seconds');
        let makeToolButton = (text, titleText) => {
            let button = document.createElement('button');
            button.type = 'button';
            button.className = 'basic-button minimax-h3-trim-tool';
            button.textContent = text;
            button.title = titleText;
            fields.appendChild(button);
            return button;
        };
        let setStart = makeToolButton('⇤ Start', 'Set the trim start to the current playback position');
        let setEnd = makeToolButton('End ⇥', 'Set the trim end to the current playback position');
        let previewButton = makeToolButton('▶ Preview', 'Play only the selected trim window');
        let badge = document.createElement('span');
        badge.className = 'minimax-h3-trim-length';
        fields.appendChild(badge);

        let actions = createDiv(null, 'minimax-h3-trim-actions');
        let addButton = document.createElement('button');
        addButton.type = 'button';
        addButton.className = 'basic-button minimax-h3-trim-add';
        addButton.textContent = '➕ Add Reference';
        addButton.title = 'Add this file as a reference. Only the selected window is used at generation time.';
        let cancelButton = document.createElement('button');
        cancelButton.type = 'button';
        cancelButton.className = 'basic-button minimax-h3-trim-cancel';
        cancelButton.textContent = 'Cancel';
        let note = document.createElement('span');
        note.className = 'minimax-h3-trim-note';
        note.textContent = 'Loading duration…';
        actions.append(addButton, cancelButton, note);

        panel.append(head, preview, track, fields, actions);
        modal.appendChild(panel);
        document.body.appendChild(modal);

        let onEscape = (event) => {
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                this.closeTrimPopup();
            }
        };
        document.addEventListener('keydown', onEscape, true);
        this.trimPopup = {
            file, type, modal, element, url, track, fill, playhead, startHandle, endHandle,
            startInput, endInput, badge, note, addButton,
            duration: null, start: 0, end: null, previewActive: false, busy: false, onEscape,
        };
        // Keep prompt-box hotkeys and SwarmUI global key handlers out of the popup.
        panel.addEventListener('keydown', (event) => {
            if (event.key !== 'Escape') {
                event.stopPropagation();
            }
        });
        modal.addEventListener('mousedown', (event) => {
            if (event.target === modal) {
                this.closeTrimPopup();
            }
        });
        close.addEventListener('click', () => this.closeTrimPopup());
        cancelButton.addEventListener('click', () => this.closeTrimPopup());
        addButton.addEventListener('click', () => this.confirmTrimAdd());

        element.addEventListener('loadedmetadata', () => {
            let popup = this.trimPopup;
            if (popup?.element !== element) {
                return;
            }
            let duration = Number(element.duration);
            if (Number.isFinite(duration) && duration > 0) {
                popup.duration = duration;
                popup.start = 0;
                popup.end = duration;
                popup.note.textContent = '';
                this.updateTrimPopupUI();
            }
            else {
                popup.note.textContent = 'Duration unavailable — this file can only be added untrimmed.';
            }
        });
        element.addEventListener('timeupdate', () => {
            let popup = this.trimPopup;
            if (popup?.element !== element || !popup.duration) {
                return;
            }
            let position = Math.min(element.currentTime, popup.duration);
            popup.playhead.style.left = `${(position / popup.duration) * 100}%`;
            if (popup.previewActive && element.currentTime >= popup.end - 0.02) {
                element.pause();
                popup.previewActive = false;
            }
        });
        element.addEventListener('pause', () => {
            if (this.trimPopup) {
                this.trimPopup.previewActive = false;
            }
        });
        element.addEventListener('error', () => {
            let popup = this.trimPopup;
            if (popup?.element === element && !popup.duration) {
                popup.note.textContent = 'Preview failed — the file can still be added untrimmed.';
            }
        });

        this.bindTrimHandle(startHandle, true);
        this.bindTrimHandle(endHandle, false);
        track.addEventListener('pointerdown', (event) => {
            let popup = this.trimPopup;
            if (!popup?.duration || event.target === popup.startHandle || event.target === popup.endHandle) {
                return;
            }
            event.preventDefault();
            popup.element.currentTime = this.trimTimelineTime(event);
        });
        startInput.addEventListener('change', () => {
            let popup = this.trimPopup;
            if (popup?.duration) {
                let value = parseFloat(popup.startInput.value);
                this.setTrimRange(value, popup.end, value);
            }
        });
        endInput.addEventListener('change', () => {
            let popup = this.trimPopup;
            if (popup?.duration) {
                let value = parseFloat(popup.endInput.value);
                this.setTrimRange(popup.start, value, value);
            }
        });
        setStart.addEventListener('click', () => {
            let popup = this.trimPopup;
            if (popup?.duration) {
                this.setTrimRange(popup.element.currentTime, popup.end);
            }
        });
        setEnd.addEventListener('click', () => {
            let popup = this.trimPopup;
            if (popup?.duration) {
                this.setTrimRange(popup.start, popup.element.currentTime);
            }
        });
        previewButton.addEventListener('click', () => {
            let popup = this.trimPopup;
            if (!popup?.duration) {
                return;
            }
            popup.element.currentTime = popup.start;
            popup.previewActive = true;
            popup.element.play().catch(() => {
                popup.previewActive = false;
            });
        });
    }

    closeTrimPopup() {
        let popup = this.trimPopup;
        if (!popup) {
            return;
        }
        this.trimPopup = null;
        document.removeEventListener('keydown', popup.onEscape, true);
        popup.element.pause?.();
        popup.modal.remove();
        URL.revokeObjectURL(popup.url);
    }

    /** Timeline seconds for a pointer event over the trim track. */
    trimTimelineTime(event) {
        let rect = this.trimPopup.track.getBoundingClientRect();
        let ratio = rect.width ? (event.clientX - rect.left) / rect.width : 0;
        return Math.max(0, Math.min(1, ratio)) * (this.trimPopup.duration ?? 0);
    }

    bindTrimHandle(handle, isStart) {
        handle.addEventListener('pointerdown', (event) => {
            let popup = this.trimPopup;
            if (!popup?.duration) {
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
                let time = this.trimTimelineTime(moveEvent);
                if (isStart) {
                    this.setTrimRange(Math.min(time, popup.end - this.trimMinRange()), popup.end, time);
                }
                else {
                    this.setTrimRange(popup.start, Math.max(time, popup.start + this.trimMinRange()), time);
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

    setTrimRange(start, end, seek = null) {
        let popup = this.trimPopup;
        if (!popup?.duration) {
            return;
        }
        let minRange = Math.min(this.trimMinRange(), popup.duration);
        start = Math.max(0, Math.min(Number.isFinite(start) ? start : 0, popup.duration));
        end = Math.max(0, Math.min(Number.isFinite(end) ? end : popup.duration, popup.duration));
        if (end - start < minRange) {
            end = Math.min(popup.duration, start + minRange);
            start = Math.max(0, Math.min(start, end - minRange));
        }
        popup.start = start;
        popup.end = end;
        if (seek != null && Number.isFinite(seek)) {
            popup.element.currentTime = Math.max(0, Math.min(seek, popup.duration));
        }
        this.updateTrimPopupUI();
    }

    updateTrimPopupUI() {
        let popup = this.trimPopup;
        if (!popup?.duration) {
            return;
        }
        let startPct = (popup.start / popup.duration) * 100;
        let endPct = (popup.end / popup.duration) * 100;
        popup.startHandle.style.left = `${startPct}%`;
        popup.endHandle.style.left = `${endPct}%`;
        popup.fill.style.left = `${startPct}%`;
        popup.fill.style.width = `${Math.max(0, endPct - startPct)}%`;
        if (document.activeElement !== popup.startInput) {
            popup.startInput.value = popup.start.toFixed(2);
        }
        if (document.activeElement !== popup.endInput) {
            popup.endInput.value = popup.end.toFixed(2);
        }
        let trimmed = this.trimPopupIsTrimmed();
        popup.badge.textContent = trimmed
            ? `✂ ${(popup.end - popup.start).toFixed(2)}s of ${popup.duration.toFixed(2)}s`
            : `full ${popup.duration.toFixed(2)}s (untrimmed)`;
        popup.badge.classList.toggle('minimax-h3-trim-length-active', trimmed);
    }

    /** Renders the selected slice of an audio file to a 16-bit PCM WAV file, fully client-side. */
    async trimAudioToWav(file, start, end, newName) {
        let context = new AudioContext();
        let buffer;
        try {
            buffer = await context.decodeAudioData(await file.arrayBuffer());
        }
        finally {
            context.close();
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

    /** Adds the popup's file as a reference: audio is sliced to a WAV right here in the
     * browser, video keeps its full data and carries the window to the backend's exact
     * Video Slice trim (so there is no client-side re-encode of video). */
    async confirmTrimAdd() {
        let popup = this.trimPopup;
        if (!popup || popup.busy) {
            return;
        }
        popup.busy = true;
        popup.addButton.disabled = true;
        try {
            if (!this.trimPopupIsTrimmed()) {
                await this.addFiles([popup.file]);
            }
            else if (popup.type === 'audio') {
                popup.note.textContent = 'Rendering trimmed audio…';
                let base = popup.file.name.replace(/\.[^.]+$/, '');
                let wav = await this.trimAudioToWav(popup.file, popup.start, popup.end,
                    `${base} [${popup.start.toFixed(2)}s-${popup.end.toFixed(2)}s].wav`);
                await this.addFiles([wav]);
            }
            else {
                this.ensureEnabled();
                let data = await this.readFile(popup.file);
                let container = this.addMedia(data, popup.file.name, 'video', URL.createObjectURL(popup.file),
                    minimaxH3StorageFilename(popup.file.name));
                container.dataset.trimStart = `${Math.round(popup.start * 1000) / 1000}`;
                container.dataset.trimEnd = `${Math.round(popup.end * 1000) / 1000}`;
                let badge = document.createElement('span');
                badge.className = 'minimax-h3-prompt-reference-trim-badge';
                badge.textContent = `✂ ${popup.start.toFixed(2)}s – ${popup.end.toFixed(2)}s`;
                badge.title = 'Only this window of the video is used at generation time.';
                container.insertBefore(badge, container.querySelector('.minimax-h3-prompt-reference-name'));
                this.syncAll();
            }
            this.closeTrimPopup();
        }
        catch (error) {
            popup.busy = false;
            popup.addButton.disabled = false;
            popup.note.textContent = `Failed: ${error?.message || error}`;
        }
    }
}

let minimaxH3PromptReferences = new MiniMaxH3PromptReferences();
sessionReadyCallbacks.push(() => minimaxH3PromptReferences.register());

/** MiniMax H3 core parameters backed by an optional ComfyUI node: the backend advertises the
 * feature when the node is installed, and these changers keep the matching parameter visible
 * only while a MiniMax H3 architecture model is actually selected. */
let minimaxH3NodeFeatureSeen = {};
function minimaxH3NodeGatedFeature(flag) {
    let present = currentBackendFeatureSet.includes(flag);
    if (!(flag in minimaxH3NodeFeatureSeen)) {
        minimaxH3NodeFeatureSeen[flag] = present;
    }
    else if (!minimaxH3NodeFeatureSeen[flag] && present) {
        minimaxH3NodeFeatureSeen[flag] = true;
    }
    if (!minimaxH3NodeFeatureSeen[flag]) {
        return [[], []];
    }
    let compat = typeof currentModelHelper != 'undefined' ? currentModelHelper.curCompatClass : null;
    if (compat && compat.startsWith('minimax-h3')) {
        return [[flag], []];
    }
    return [[], [flag]];
}
if (typeof featureSetChangers != 'undefined') {
    featureSetChangers.push(() => minimaxH3NodeGatedFeature('minimax_h3_speed'));
    featureSetChangers.push(() => minimaxH3NodeGatedFeature('minimax_h3_low_vram'));
    featureSetChangers.push(() => minimaxH3NodeGatedFeature('minimax_h3_face_inpaint'));
}

/** Video Face Inpainting: keep the group's dependent parameters hidden until its enable checkbox is on
 * (core DependNonDefault compares the boolean value against the string default, so it never hides them). */
if (typeof hideParamCallbacks != 'undefined') {
    hideParamCallbacks.push(() => {
        let master = document.getElementById('input_videofaceinpainting');
        if (!master || master.checked) {
            return;
        }
        for (let param of gen_param_types) {
            if (param.depend_non_default == 'videofaceinpainting') {
                let elem = document.getElementById(`input_${param.id}`);
                let box = elem ? findParentOfClass(elem, 'auto-input') : null;
                if (box) {
                    box.style.display = 'none';
                }
            }
        }
    });
}
