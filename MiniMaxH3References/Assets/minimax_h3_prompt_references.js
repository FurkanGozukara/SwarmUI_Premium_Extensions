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
        if (!this.region || !this.extraArea || !this.referenceArea || !this.promptBox || !this.addButton) {
            return;
        }

        this.createToolbar();
        this.bindEvents();
        this.observer = new MutationObserver(() => this.syncAll());
        this.observer.observe(this.referenceArea, { childList: true });
        this.enabledInput?.addEventListener('change', () => this.updateActiveState());
        this.modelInput?.addEventListener('change', () => {
            this.deactivateForOtherModels();
            this.updateActiveState();
        });
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

        this.fileInput = document.createElement('input');
        this.fileInput.type = 'file';
        this.fileInput.multiple = true;
        this.fileInput.accept = 'image/*,video/*,audio/*';
        this.fileInput.className = 'minimax-h3-prompt-reference-file-input';
        this.fileInput.setAttribute('aria-label', 'Add MiniMax H3 prompt references');

        this.toolbar.append(this.uploadButton, this.status, this.fileInput);
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
    }

    isReferenceModel() {
        let model = `${this.modelInput?.value || ''}`.toLowerCase();
        return model.includes('minimax_h3_ref2va');
    }

    isActive() {
        return this.isReferenceModel();
    }

    deactivateForOtherModels() {
        if (this.isReferenceModel()) {
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
        if (type === 'image') {
            return this.referenceArea.querySelectorAll('img.alt-prompt-image').length;
        }
        return this.referenceArea.querySelectorAll(
            `.minimax-h3-prompt-reference[data-reference-type="${type}"]`,
        ).length;
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

    syncAll() {
        if (!this.referenceArea || !this.status) {
            return;
        }
        let images = [...this.referenceArea.querySelectorAll('img.alt-prompt-image')];
        images.forEach((image, index) => {
            let container = image.closest('.alt-prompt-image-container');
            if (container) {
                container.dataset.referenceLabel = `<Picture ${index + 1}>`;
            }
        });

        let videos = this.references('video');
        let audios = this.references('audio');
        videos.forEach((reference, index) => {
            reference.querySelector('.minimax-h3-prompt-reference-label').textContent = `<Video ${index + 1}>`;
        });
        audios.forEach((reference, index) => {
            reference.querySelector('.minimax-h3-prompt-reference-label').textContent = `<Audio ${videos.length + index + 1}>`;
        });
        this.syncSlots(videos, this.videoInputIds);
        this.syncSlots(audios, this.audioInputIds);
        this.status.textContent = `${images.length}/${this.maxImages} images | ${videos.length}/${this.maxVideos} videos | ${audios.length}/${this.maxAudios} audio`;
        if (this.clearButton && this.isActive()) {
            this.clearButton.textContent = 'Clear references';
        }
        if (typeof genTabLayout !== 'undefined') {
            genTabLayout.altPromptSizeHandle();
        }
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
