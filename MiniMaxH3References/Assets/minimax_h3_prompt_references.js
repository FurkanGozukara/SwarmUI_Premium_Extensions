
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
        this.lastKnown = null;
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
        this.observer = new MutationObserver(() => this.syncAll());
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
        this.uploadButton.textContent = 'Add references';
        this.uploadButton.title = 'Add MiniMax H3 prompt images, videos, or audio';

        this.status = document.createElement('span');
        this.status.className = 'minimax-h3-prompt-reference-status';
        this.status.setAttribute('aria-live', 'polite');

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

        this.toolbar.append(this.uploadButton, this.status, this.hint, this.fileInput);
        this.extraArea.prepend(this.toolbar);
    }

    bindEvents() {
        this.uploadButton.addEventListener('click', () => this.fileInput.click());
        this.fileInput.addEventListener('change', async () => {
            await this.addFiles([...this.fileInput.files]);
            this.fileInput.value = '';
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
        for (let id of [...this.videoInputIds, ...this.audioInputIds]) {
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
            if (type === 'image') {
                this.addImage(data, file.name);
            }
            else {
                this.addMedia(data, file.name, type);
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

    addImage(data, filename) {
        let container = document.createElement('div');
        container.className = 'alt-prompt-image-container';
        container.dataset.filename = filename;

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
        image.src = data;
        image.height = 128;
        image.className = 'alt-prompt-image';
        image.dataset.filedata = data;
        image.dataset.filename = filename;
        container.append(remove, image);
        this.referenceArea.appendChild(container);
        this.showReferenceArea();
        showRevisionInputs(true);
    }

    addMedia(data, filename, type) {
        let container = document.createElement('div');
        container.className = 'minimax-h3-prompt-reference';
        container.dataset.referenceType = type;
        container.dataset.filedata = data;
        container.dataset.filename = filename;

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
        media.src = data;
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
        let current = this.collectReferences();
        if (this.lastKnown && this.isActive()) {
            // Only a single-card removal renumbers and cleans prompt tokens. Bulk removals
            // (eg 'Clear references') leave the prompt text untouched, so a rebuilt
            // reference list can pick the old tokens right back up.
            let removedTotal = 0;
            for (let type of ['image', 'video', 'audio']) {
                removedTotal += Math.max(0, this.lastKnown[type].length - current[type].length);
            }
            if (removedTotal === 1) {
                for (let type of ['image', 'video', 'audio']) {
                    let old = this.lastKnown[type];
                    let now = current[type];
                    if (old.length - now.length === 1) {
                        let mapping = {};
                        old.forEach((elem, index) => {
                            let newIndex = now.indexOf(elem);
                            if (newIndex !== -1) {
                                mapping[index + 1] = newIndex + 1;
                            }
                        });
                        this.renumberTokensAfterRemoval(type, mapping);
                    }
                }
            }
        }
        this.lastKnown = current;

        current.image.forEach((container, index) => {
            let n = index + 1;
            container.dataset.referenceLabel = this.tokenFor('image', n);
            container.style.setProperty('--minimax-ref-color', this.colorFor('image', n));
            this.bindCardInsert(container, 'image');
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
            });
        }
        this.syncSlots(current.video, this.videoInputIds);
        this.syncSlots(current.audio, this.audioInputIds);
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
            container.title = 'Click to insert this reference into the prompt';
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

    /** After a single reference was removed: renumber surviving tokens, drop tokens for the removed one. */
    renumberTokensAfterRemoval(type, mapping) {
        let box = this.promptBox;
        let original = box.value;
        let aliases = MiniMaxH3ReferenceTypes[type].aliases.join('|');
        let tokenRegex = new RegExp(`(?<![\\w@])@(?:${aliases})#?(\\d{1,2})(?![0-9a-zA-Z]) ?`, 'gi');
        let updated = original.replace(tokenRegex, (match, num) => {
            let n = parseInt(num);
            let trailing = match.endsWith(' ') ? ' ' : '';
            if (mapping[n]) {
                return `@${type}${mapping[n]}${trailing}`;
            }
            return '';
        });
        if (type !== 'audio') {
            // Legacy audio labels are offset by the video count, so only image/video legacy labels renumber safely.
            let label = MiniMaxH3ReferenceTypes[type].label;
            let legacyRegex = new RegExp(`<${label}[ ]?(\\d{1,2})> ?`, 'gi');
            updated = updated.replace(legacyRegex, (match, num) => {
                let n = parseInt(num);
                let trailing = match.endsWith(' ') ? ' ' : '';
                if (mapping[n]) {
                    return `<${label} ${mapping[n]}>${trailing}`;
                }
                return '';
            });
        }
        if (updated !== original) {
            box.value = updated;
            box.dispatchEvent(new Event('input'));
        }
    }

    // ==================== Prompt pill overlay ====================

    createOverlay() {
        this.overlay = document.createElement('div');
        this.overlay.id = 'minimax_h3_prompt_highlight';
        this.overlay.className = 'minimax-h3-prompt-highlight';
        this.overlay.setAttribute('aria-hidden', 'true');
        this.overlay.style.display = 'none';
        this.promptBox.parentElement.appendChild(this.overlay);
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
        this.overlay.scrollTop = this.promptBox.scrollTop;
        this.overlay.scrollLeft = this.promptBox.scrollLeft;
    }

    /** Mirrors the textarea's exact text metrics and geometry onto the overlay. */
    syncOverlayMetrics() {
        let box = this.promptBox;
        let overlay = this.overlay;
        let cs = getComputedStyle(box);
        for (let prop of ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'fontVariant', 'fontStretch', 'fontKerning',
            'letterSpacing', 'wordSpacing', 'lineHeight', 'textTransform', 'textIndent', 'tabSize', 'direction', 'textAlign']) {
            overlay.style[prop] = cs[prop];
        }
        overlay.style.paddingTop = cs.paddingTop;
        overlay.style.paddingRight = cs.paddingRight;
        overlay.style.paddingBottom = cs.paddingBottom;
        overlay.style.paddingLeft = cs.paddingLeft;
        let host = box.parentElement;
        let hostRect = host.getBoundingClientRect();
        let rect = box.getBoundingClientRect();
        let borderLeft = parseFloat(cs.borderLeftWidth) || 0;
        let borderRight = parseFloat(cs.borderRightWidth) || 0;
        let borderTop = parseFloat(cs.borderTopWidth) || 0;
        let borderBottom = parseFloat(cs.borderBottomWidth) || 0;
        let scrollbarWidth = box.scrollHeight > box.clientHeight + 1
            ? Math.max(0, box.offsetWidth - box.clientWidth - borderLeft - borderRight) : 0;
        let scrollbarHeight = box.scrollWidth > box.clientWidth + 1
            ? Math.max(0, box.offsetHeight - box.clientHeight - borderTop - borderBottom) : 0;
        overlay.style.left = `${rect.left - hostRect.left + borderLeft}px`;
        overlay.style.top = `${rect.top - hostRect.top + borderTop}px`;
        overlay.style.width = `${Math.max(0, rect.width - borderLeft - borderRight - scrollbarWidth)}px`;
        overlay.style.height = `${Math.max(0, rect.height - borderTop - borderBottom - scrollbarHeight)}px`;
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
            html += escapeHtml(text.substring(last, match.index));
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
            html += `<span class="${cls}"${style}>${escapeHtml(match[0])}</span>`;
        }
        html += escapeHtml(text.substring(last));
        this.overlay.innerHTML = html + String.fromCharCode(0x200b);
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
                input.dataset.filedata = reference.dataset.filedata;
                input.dataset.filename = reference.dataset.filename;
                input.dataset.has_data = 'true';
            }
            else {
                delete input.dataset.filedata;
                delete input.dataset.filename;
                delete input.dataset.duration;
                delete input.dataset.resolution;
                delete input.dataset.has_data;
                input.value = '';
            }
            let parent = findParentOfClass(input, 'auto-input');
            let label = parent?.querySelector('.auto-file-input-filename');
            if (label) {
                label.textContent = reference?.dataset.filename || '';
            }
        });
    }
}

let minimaxH3PromptReferences = new MiniMaxH3PromptReferences();
sessionReadyCallbacks.push(() => minimaxH3PromptReferences.register());
