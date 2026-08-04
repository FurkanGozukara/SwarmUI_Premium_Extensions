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
        sessionReadyCallbacks.push(() => {
            this.configureInitInput();
        });
    }

    /** Adds the extra video formats to the Init Image file chooser once that input exists. */
    configureInitInput() {
        let input = document.getElementById('input_initimage');
        if (!input || input.dataset.initVideoContinuationFormats == 'true') {
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
