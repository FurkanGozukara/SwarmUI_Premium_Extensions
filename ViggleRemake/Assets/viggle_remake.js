/** Viggle Remake: show the controls only for Viggle Remake models (Swarm may class the files as MiniMax H3, so the file
 * name identifies them, as on the server). */
function viggleRemakeModelSelected() {
    let name = `${currentModelHelper?.curModel || ''}`.toLowerCase().replaceAll('-', '_').replaceAll(' ', '_');
    return name.includes('viggle_remake');
}

let viggleRemakeFeatureSeen = false;
featureSetChangers.push(() => {
    let flag = 'secourses_viggle_remake';
    viggleRemakeFeatureSeen ||= currentBackendFeatureSet.includes(flag);
    if (!viggleRemakeFeatureSeen) {
        return [[], []];
    }
    return viggleRemakeModelSelected() ? [[flag], []] : [[], [flag]];
});

/** Whether a getGenInput result carries a source video: the MiniMax H3 uploader's first video, a prompt video, or a
 * video Init Image. */
function viggleRemakeHasVideo(input) {
    return viggleRemakeSourceVideo(input) != null;
}

/** The source video and its trim exactly as the server picks it: the MiniMax H3 uploader's first video (with its trim
 * window), else the first prompt video, else a video Init Image. Start Seconds / Max Seconds narrow the window further. */
function viggleRemakeSourceVideo(input) {
    let data = null, start = 0, end = null;
    if (input.minimaxhreferencevideoone) {
        data = input.minimaxhreferencevideoone;
        let trim = `${input.minimaxhreferencevideotrims || ''}`.split(',').map(t => /^\s*1:(\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)\s*$/.exec(t)).find(m => m);
        if (trim && +trim[2] > +trim[1]) {
            start = +trim[1]; end = +trim[2];
        }
    }
    else if (input.promptvideos?.length) {
        data = input.promptvideos[0];
    }
    else if (`${input.initimage || ''}`.startsWith('data:video/')) {
        data = input.initimage;
    }
    if (!data) {
        return null;
    }
    start += Number(input.viggleremakestartseconds || 0);
    let max = Number(input.viggleremakemaxseconds || 0);
    if (max > 0) {
        end = end == null ? start + max : Math.min(end, start + max);
    }
    return {data, start, end};
}

/** The photos in @image order: prompt images, then a still Init Image (as the server numbers them). */
function viggleRemakePhotos(input) {
    let photos = [...(input.promptimages || [])];
    if (input.initimage && `${input.initimage}`.startsWith('data:image/')) {
        photos.push(input.initimage);
    }
    return photos;
}

/** "Find the people in my video": the attached video is uploaded to the ComfyUI backend (SwarmUI's ComfyBackendDirect
 * proxy), the SECoursesAudioTools find_people route lists every actor by face, and the user picks the photo that replaces
 * each one. The choice is stored in the hidden "Viggle Remake People" parameter (face embeddings and photo numbers), which
 * the Plan node matches against the faces of every keyframe, the same JSON as the ComfyUI button writes. */
class ViggleRemakeCastUI {
    constructor() {
        postParamBuildSteps.push(() => this.install());
    }

    install() {
        this.field = document.getElementById('input_viggleremakepeople');
        if (!this.field || this.field.dataset.viggleCastReady) {
            return;
        }
        this.field.dataset.viggleCastReady = 'true';
        this.field.hidden = true;
        this.root = document.createElement('div');
        this.root.className = 'viggle-cast';
        let toolbar = document.createElement('div');
        toolbar.className = 'viggle-toolbar';
        this.findButton = document.createElement('button');
        this.findButton.type = 'button';
        this.findButton.className = 'btn btn-primary viggle-button';
        this.findButton.textContent = 'Find the people in my video';
        this.findButton.addEventListener('click', () => this.find());
        let clear = document.createElement('button');
        clear.type = 'button';
        clear.className = 'btn btn-secondary viggle-button';
        clear.textContent = 'Clear people';
        clear.addEventListener('click', () => { this.write({}); this.render(); });
        toolbar.append(this.findButton, clear);
        this.hint = document.createElement('div');
        this.hint.className = 'viggle-help';
        this.hint.setAttribute('role', 'status');
        this.rows = document.createElement('div');
        this.rows.className = 'viggle-cast-rows';
        this.root.append(toolbar, this.hint, this.rows);
        this.field.after(this.root);
        this.field.addEventListener('change', () => {
            if (!this.writing) {
                this.render();
            }
        });
        this.render();
    }

    read() {
        try {
            return JSON.parse(this.field.value || '{}') || {};
        }
        catch {
            return {};
        }
    }

    write(data) {
        this.writing = true;
        this.field.value = data.people?.length ? JSON.stringify(data) : '';
        triggerChangeFor(this.field);
        this.writing = false;
    }

    currentPhotos() {
        try {
            return viggleRemakePhotos(getGenInput());
        }
        catch {
            return [];
        }
    }

    render(status = null) {
        let data = this.read();
        let list = data.people || [];
        let photos = this.currentPhotos();
        this.rows.replaceChildren();
        let chosen = list.filter(p => p.photo > 0).length;
        this.hint.textContent = status || (list.length
            ? `Pick the photo for each person to replace (${chosen} chosen). The others stay as they are; your prompt text is optional now.`
            : 'Optional, recommended with 2 or more people: find everyone in your video by face, then choose who becomes which attached photo (@image1, @image2, ...). Without it, your prompt decides (e.g. "Elon Musk (@image1) replaces the man on the right").');
        for (let person of list) {
            let row = document.createElement('div');
            row.className = 'viggle-cast-row' + (person.photo > 0 ? ' viggle-cast-on' : '');
            let face = document.createElement('img');
            face.className = 'viggle-cast-face';
            face.src = person.thumb;
            face.alt = `Person ${person.id}`;
            face.title = `Seen in ${person.count} sampled frames, clearest at ${(person.frame / 24).toFixed(1)} s`;
            let select = document.createElement('select');
            select.setAttribute('aria-label', `Photo for person ${person.id}`);
            select.append(new Option('keep as is', '0'));
            for (let i = 1; i <= Math.max(4, photos.length); i++) {
                select.append(new Option(`→ @image${i}`, `${i}`));
            }
            select.value = `${person.photo || 0}`;
            let thumb = document.createElement('img');
            thumb.className = 'viggle-cast-photo';
            let showThumb = () => {
                let photo = photos[Number(select.value) - 1];
                thumb.hidden = !photo;
                if (photo) {
                    thumb.src = photo;
                }
            };
            showThumb();
            select.addEventListener('change', () => {
                person.photo = Number(select.value);
                this.write(data);
                row.classList.toggle('viggle-cast-on', person.photo > 0);
                showThumb();
                this.hint.textContent = `Pick the photo for each person to replace (${list.filter(p => p.photo > 0).length} chosen). The others stay as they are.`;
            });
            row.append(face, select, thumb);
            this.rows.appendChild(row);
        }
    }

    async backendJson(response) {
        let text = await response.text();
        let result = null;
        try {
            result = JSON.parse(text);
        }
        catch {
            throw new Error(`${response.status} ${response.statusText}: ${text.replace(/<[^>]*>/g, ' ').trim().slice(0, 200)}`);
        }
        if (!response.ok || result.error) {
            throw new Error(result.error || `${response.status} ${response.statusText}`);
        }
        return result;
    }

    async find() {
        let source = viggleRemakeSourceVideo(getGenInput());
        if (!source) {
            this.render('Attach your video to the prompt first (drag it into the prompt box), or set it as the Init Image.');
            return;
        }
        this.findButton.disabled = true;
        this.findButton.textContent = 'Finding people…';
        try {
            this.hint.textContent = 'Uploading the video to the ComfyUI backend…';
            let blob = await (await fetch(source.data)).blob();
            let digest = 'x' + Date.now();
            if (crypto?.subtle) {
                let hash = new Uint8Array(await crypto.subtle.digest('SHA-1', await blob.arrayBuffer()));
                digest = [...hash.slice(0, 8)].map(b => b.toString(16).padStart(2, '0')).join('');
            }
            let extension = (blob.type.split('/')[1] || 'mp4').replace('quicktime', 'mov').replace(/[^a-z0-9]/gi, '');
            let form = new FormData();
            form.append('image', blob, `viggle_${digest}.${extension}`);
            form.append('type', 'input');
            form.append('subfolder', 'swarm_viggle_remake');
            form.append('overwrite', 'true');
            let uploaded = await this.backendJson(await fetch('ComfyBackendDirect/upload/image', {method: 'POST', body: form}));
            let file = uploaded.subfolder ? `${uploaded.subfolder}/${uploaded.name}` : uploaded.name;
            this.hint.textContent = 'Looking for faces in the video…';
            let entry = {file, trim_start: source.start};
            if (source.end != null) {
                entry.trim_end = source.end;
            }
            let result = await this.backendJson(await fetch('ComfyBackendDirect/secourses/viggle_remake/find_people', {
                method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({entry})}));
            // Keep earlier choices for the same actors (same face) when searching again.
            let old = this.read().people || [];
            let dot = (a, b) => a.reduce((s, v, i) => s + v * b[i], 0);
            for (let person of result.people) {
                let previous = old.find(o => o.embedding && dot(o.embedding, person.embedding) > 0.6);
                person.photo = previous ? previous.photo : 0;
            }
            this.write({people: result.people, video: file});
            this.render(`${result.people.length} people found in ${result.seconds} s. Pick the photo for each person to replace; the others stay as they are.`);
        }
        catch (error) {
            this.render(`Find people failed: ${error?.message || error}. It needs the updated SECoursesAudioTools nodes on the ComfyUI backend.`);
        }
        finally {
            this.findButton.disabled = false;
            this.findButton.textContent = 'Find the people in my video';
        }
    }
}

let viggleRemakeCastUI = new ViggleRemakeCastUI();

/** Sequential prompt batches for Viggle Remake: one video per prompt, with the same captured settings, video and photos. */
class ViggleRemakeUI {
    constructor() {
        this.prompts = [];
        this.running = false;
        postParamBuildSteps.push(() => this.install());
    }

    element(tag, className, text) {
        let elem = document.createElement(tag);
        elem.className = className || '';
        if (text != null) {
            elem.textContent = text;
        }
        return elem;
    }

    button(label, action, parent) {
        let button = this.element('button', 'btn btn-secondary viggle-button', label);
        button.type = 'button';
        button.addEventListener('click', action);
        parent.appendChild(button);
        return button;
    }

    install() {
        this.listInput = document.getElementById('input_viggleremakepromptlist');
        if (!this.listInput || this.listInput.dataset.viggleReady) {
            return;
        }
        this.listInput.dataset.viggleReady = 'true';
        this.listInput.hidden = true;
        this.batch = this.element('div', 'viggle-batch');
        this.listInput.after(this.batch);
        this.batch.appendChild(this.element('p', 'viggle-help', 'Remake the same video several ways: the main prompt runs first, followed by these extra prompts, one video each, with the same settings, attached video and photos. Each .txt file is one prompt.'));
        this.promptRows = this.element('div', 'viggle-prompt-rows');
        this.batch.appendChild(this.promptRows);
        let toolbar = this.element('div', 'viggle-toolbar');
        this.batch.appendChild(toolbar);
        this.button('Add prompt', () => {
            this.prompts.push({name: `Prompt ${this.prompts.length + 2}`, text: ''});
            this.savePrompts();
        }, toolbar);
        for (let folder of [false, true]) {
            let picker = this.element('input', '');
            picker.type = 'file'; picker.multiple = true; picker.accept = '.txt'; picker.hidden = true;
            picker.setAttribute('aria-label', folder ? 'Import prompt folder' : 'Import prompt text files');
            if (folder) {
                picker.setAttribute('webkitdirectory', '');
            }
            picker.addEventListener('change', async () => {
                let files = [...picker.files].filter(f => /\.txt$/i.test(f.name)).sort((a, b) => (a.webkitRelativePath || a.name).localeCompare(b.webkitRelativePath || b.name, undefined, {numeric: true}));
                if (files.length + this.prompts.length > 63) {
                    this.batchStatus.textContent = 'Import at most 63 extra prompts per batch.';
                    return;
                }
                for (let file of files) {
                    let text = (await file.text()).replace(/^﻿/, '').trim();
                    if (text) {
                        this.prompts.push({name: file.name, text});
                    }
                }
                picker.value = '';
                this.savePrompts();
            });
            this.batch.appendChild(picker);
            this.button(folder ? 'Import folder' : 'Import .txt', () => picker.click(), toolbar);
        }
        this.button('Clear extra prompts', () => {
            this.prompts = []; this.savePrompts();
        }, toolbar);
        this.batchRun = this.button('Generate prompt batch', () => this.runBatch(), toolbar);
        this.batchRun.classList.replace('btn-secondary', 'btn-primary');
        this.batchStop = this.button('Stop after current', () => {
            this.stopRequested = true;
            this.batchStop.disabled = true;
            this.batchStatus.textContent = 'Stopping after the current video finishes.';
        }, toolbar);
        this.batchStop.disabled = true;
        this.batchStatus = this.element('div', 'viggle-batch-status');
        this.batchStatus.setAttribute('role', 'status');
        this.batch.appendChild(this.batchStatus);
        this.listInput.addEventListener('change', () => {
            if (!this.savingPrompts) {
                this.readPrompts();
            }
        });
        this.readPrompts();
    }

    readPrompts() {
        try {
            let data = JSON.parse(this.listInput.value || '[]');
            if (!Array.isArray(data) || data.some(p => typeof p.text != 'string')) {
                throw new Error('Expected a list of prompts.');
            }
            this.prompts = data;
            this.renderPrompts();
        }
        catch (error) {
            this.batchStatus.textContent = `Prompt list: ${error.message}`;
        }
    }

    savePrompts(render = true) {
        this.savingPrompts = true;
        this.listInput.value = JSON.stringify(this.prompts);
        this.listInput.dispatchEvent(new Event('input', {bubbles: true}));
        this.listInput.dispatchEvent(new Event('change', {bubbles: true}));
        this.savingPrompts = false;
        if (render) {
            this.renderPrompts();
        }
    }

    renderPrompts() {
        this.promptRows.replaceChildren();
        for (let [i, prompt] of this.prompts.entries()) {
            let row = this.element('div', 'viggle-prompt-row');
            row.appendChild(this.element('strong', '', prompt.name || `Prompt ${i + 2}`));
            let text = this.element('textarea', 'viggle-description');
            text.value = prompt.text;
            text.rows = 3;
            text.setAttribute('aria-label', `Extra prompt ${i + 1}`);
            text.addEventListener('input', () => {
                prompt.text = text.value; this.savePrompts(false);
            });
            row.appendChild(text);
            this.button('Remove prompt', () => {
                this.prompts.splice(i, 1); this.savePrompts();
            }, row);
            this.promptRows.appendChild(row);
        }
    }

    async runBatch() {
        if (this.running) {
            return;
        }
        // Selected (not applied) presets are merged on the server after these values,
        // so a preset with its own prompt would replace every batch prompt.
        if (currentPresets.some(p => typeof p.param_map?.prompt == 'string' && !p.param_map.prompt.includes('{value}'))) {
            this.batchStatus.textContent = 'A selected preset sets its own prompt and would replace every batch prompt. Use Direct Apply on the preset (or Apply in the Presets tab) and run the batch again.';
            return;
        }
        let captured = getGenInput();
        let prompts = [captured.prompt || '', ...this.prompts.map(p => p.text)].filter(p => p.trim());
        if (!captured.viggleremakeenabled || !viggleRemakeModelSelected() || !prompts.length || prompts.length > 64) {
            this.batchStatus.textContent = 'Select a Viggle Remake model, switch Viggle Remake Enabled on and enter between 1 and 64 prompts.';
            return;
        }
        if (!viggleRemakeHasVideo(captured)) {
            this.batchStatus.textContent = 'Attach your source video to the prompt first; every video of the batch uses it.';
            return;
        }
        // Reuse native progress, history and video display while isolating the batch's websocket.
        class PromptBatchHandler extends GenerateHandler {
            constructor() {
                super(); this.validateModel = false; this.first = true;
            }
            getGenInput() { return this.nextInput; }
            beforeGenRun() { num_waiting_gens += Number(this.nextInput.images || 1); }
            resetBatchIfNeeded() {
                if (this.first) { super.resetBatchIfNeeded(); this.first = false; }
            }
            hadError(message) {
                super.hadError(message);
                let reject = this.reject; this.resolve = this.reject = null;
                reject?.(new Error(message));
            }
            internalHandleData(...args) {
                super.internalHandleData(...args);
                if (args[0].socket_intention == 'close') {
                    let resolve = this.resolve; this.resolve = this.reject = null; resolve?.();
                }
            }
            one(input) {
                this.nextInput = input;
                return new Promise((resolve, reject) => {
                    this.resolve = resolve; this.reject = reject; this.doGenerate();
                });
            }
        }
        this.running = true;
        this.stopRequested = false;
        this.batchRun.disabled = true;
        this.batchStop.disabled = false;
        let handler = new PromptBatchHandler();
        let completed = 0;
        try {
            for (let prompt of prompts) {
                if (this.stopRequested) {
                    break;
                }
                this.batchStatus.textContent = `Generating video ${completed + 1} of ${prompts.length}…`;
                await handler.one({...captured, prompt});
                completed++;
            }
            this.batchStatus.textContent = `${this.stopRequested ? 'Stopped' : 'Complete'}: ${completed} of ${prompts.length} videos generated.`;
        }
        catch (error) {
            this.batchStatus.textContent = `Batch stopped at prompt ${completed + 1}: ${error.message}`;
        }
        finally {
            this.running = false;
            this.batchRun.disabled = false;
            this.batchStop.disabled = true;
        }
    }
}

let viggleRemakeUI = new ViggleRemakeUI();
