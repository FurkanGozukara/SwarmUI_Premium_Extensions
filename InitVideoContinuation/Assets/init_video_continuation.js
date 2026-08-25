class InitVideoContinuationUI {

    /** Installs broader Init Image video selection and a fallback for formats the browser cannot preview. */
    constructor() {
        this.mimeTypes = {
            mp4: 'video/mp4',
            webm: 'video/webm',
            mov: 'video/quicktime',
            m4v: 'video/x-m4v',
            mkv: 'video/x-matroska',
            avi: 'video/x-msvideo',
            mpeg: 'video/mpeg',
            mpg: 'video/mpeg',
            ts: 'video/mp2t',
            m2ts: 'video/mp2t',
            mts: 'video/mp2t',
            wmv: 'video/x-ms-wmv',
            flv: 'video/x-flv',
            ogv: 'video/ogg',
            '3gp': 'video/3gpp'
        };
        this.baseSetMediaFileInput = setMediaFileInput;
        setMediaFileInput = (elem, file, type) => {
            this.setMediaFileInput(elem, file, type);
        };
        this.configureUI();
        sessionReadyCallbacks.push(() => {
            this.configureUI();
        });
        this.inputObserver = new MutationObserver(() => {
            this.configureUI();
        });
        this.inputObserver.observe(document.documentElement, { childList: true, subtree: true });
    }

    /** Keeps the optional context selector beside, and gated by, its continuation toggle. */
    configureUI() {
        this.configureInitInput();
        this.configureContinuationControls();
    }

    configureContinuationControls() {
        let toggle = document.getElementById('input_continuefromlastvideoframes');
        let select = document.getElementById('input_continuationcontextframes');
        if (!toggle || !select) {
            return;
        }

        let toggleRow = toggle.closest('.auto-input');
        let selectRow = select.closest('.auto-input');
        if (!toggleRow || !selectRow) {
            return;
        }

        let wrapper = document.getElementById('init-video-continuation-controls');
        if (!wrapper) {
            wrapper = document.createElement('div');
            wrapper.id = 'init-video-continuation-controls';
            wrapper.className = 'init-video-continuation-controls';
            toggleRow.before(wrapper);
            wrapper.append(toggleRow, selectRow);
            toggleRow.classList.add('init-video-continuation-toggle');
            selectRow.classList.add('init-video-continuation-context');
            select.setAttribute('aria-label', 'Continuation context frames');
            select.title = 'Continuation context frames';

            let style = document.createElement('style');
            style.id = 'init-video-continuation-style';
            style.textContent = `
                .init-video-continuation-controls {
                    display: grid;
                    grid-template-columns: minmax(0, 1fr) 56px;
                    align-items: center;
                    column-gap: 8px;
                }
                .init-video-continuation-controls > .auto-input {
                    min-width: 0;
                    margin: 0;
                }
                .init-video-continuation-context > label {
                    display: none;
                }
                .init-video-continuation-context select {
                    width: 100% !important;
                    min-width: 50px;
                }
                .init-video-continuation-context[data-disabled="true"] {
                    opacity: 0.45;
                }
            `;
            if (!document.getElementById(style.id)) {
                document.head.append(style);
            }

            toggle.addEventListener('change', () => this.syncContinuationControls(toggle, select, wrapper));
            new MutationObserver(() => this.syncContinuationControls(toggle, select, wrapper)).observe(
                toggleRow,
                { attributes: true, attributeFilter: ['style', 'data-disabled'] }
            );
        }
        this.syncContinuationControls(toggle, select, wrapper);
    }

    syncContinuationControls(toggle, select, wrapper) {
        let toggleRow = toggle.closest('.auto-input');
        let selectRow = select.closest('.auto-input');
        let enabled = toggle.checked && !toggle.disabled;
        select.disabled = !enabled;
        selectRow.dataset.disabled = enabled ? 'false' : 'true';
        wrapper.style.display = toggleRow.style.display == 'none' || toggleRow.hidden ? 'none' : '';
    }

    /** Adds the extra video formats to the Init Image file chooser once that input exists. */
    configureInitInput() {
        let input = document.getElementById('input_initimage');
        if (!input) {
            return;
        }
        if (input.dataset.initVideoContinuationFormats == 'true') {
            return;
        }
        let extraExtensions = Object.keys(this.mimeTypes).map(extension => `.${extension}`).join(',');
        input.accept = `${input.accept},video/*,${extraExtensions}`;
        input.dataset.initVideoContinuationFormats = 'true';
    }

    /** Returns the lowercase extension of a selected file. */
    getExtension(file) {
        let parts = (file?.name || '').toLowerCase().split('.');
        return parts.length > 1 ? parts.pop() : '';
    }

    /** Routes supported Init Image video files through MIME normalization before the standard preview handler. */
    setMediaFileInput(elem, file, type) {
        let extension = this.getExtension(file);
        let isVideo = file && (file.type.startsWith('video/') || extension in this.mimeTypes);
        if (!file || elem.id != 'input_initimage' || !isVideo) {
            this.baseSetMediaFileInput(elem, file, type);
            return;
        }

        let reader = new FileReader();
        reader.addEventListener('load', () => {
            let source = `${reader.result}`;
            let mimeType = file.type.startsWith('video/') ? file.type : this.mimeTypes[extension];
            if (!source.startsWith('data:video/')) {
                source = source.replace(/^data:[^;,]*/, `data:${mimeType}`);
            }
            delete elem.dataset.filename;
            delete elem.dataset.width;
            delete elem.dataset.height;
            delete elem.dataset.resolution;
            delete elem.dataset.duration;
            setMediaFileDirect(elem, source, 'video', file.name, file.name);
            this.installPreviewFallback(elem, file.name);
        }, false);
        reader.readAsDataURL(file);
    }

    /** Finalizes an upload even when Chromium cannot decode the selected container for its local preview. */
    installPreviewFallback(elem, fileName) {
        let parent = findParentOfClass(elem, 'auto-input');
        if (!parent) {
            return;
        }
        let video = parent?.querySelector('.auto-input-preview video');
        let source = video?.querySelector('source');
        let finished = false;
        let finalize = () => {
            if (finished || elem.dataset.filename) {
                return;
            }
            finished = true;
            let label = parent.querySelector('.auto-file-input-filename');
            let shortName = fileName.length > 30 ? `${fileName.substring(0, 27)}...` : fileName;
            label.textContent = shortName;
            elem.dataset.filename = fileName;
            loadMediaFileDedup = true;
            triggerChangeFor(elem);
            loadMediaFileDedup = false;
        };
        if (video) {
            video.addEventListener('error', finalize, { once: true });
        }
        if (source) {
            source.addEventListener('error', finalize, { once: true });
        }
        window.setTimeout(finalize, 5000);
    }
}

let initVideoContinuationUI = new InitVideoContinuationUI();
