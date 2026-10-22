/** Keep the extension's controls scoped to Bria's Fibo 2.0 model family. */
let fibo2FeatureSeen = false;
featureSetChangers.push(() => {
    let flag = 'secourses_fibo2';
    fibo2FeatureSeen ||= currentBackendFeatureSet.includes(flag);
    if (!fibo2FeatureSeen) {
        return [[], []];
    }
    return currentModelHelper?.curCompatClass == 'fibo-2' ? [[flag], []] : [[], [flag]];
});

/** Sequential prompt batches for Fibo 2.0, with the same captured settings and attached images. */
class Fibo2UI {
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
        let button = this.element('button', 'btn btn-secondary fibo2-button', label);
        button.type = 'button';
        button.addEventListener('click', action);
        parent.appendChild(button);
        return button;
    }

    install() {
        this.listInput = document.getElementById('input_fibopromptlist');
        if (!this.listInput || this.listInput.dataset.fibo2Ready) {
            return;
        }
        this.listInput.dataset.fibo2Ready = 'true';
        this.listInput.hidden = true;
        this.batch = this.element('div', 'fibo2-batch');
        this.listInput.after(this.batch);
        this.batch.appendChild(this.element('p', 'fibo2-help', 'Generate a series with the same settings and attached images. The main prompt runs first, followed by these extra prompts. Each .txt file is one prompt (JSON captions included).'));
        this.promptRows = this.element('div', 'fibo2-prompt-rows');
        this.batch.appendChild(this.promptRows);
        let toolbar = this.element('div', 'fibo2-toolbar');
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
            this.batchStatus.textContent = 'Stopping after the current prompt finishes.';
        }, toolbar);
        this.batchStop.disabled = true;
        this.batchStatus = this.element('div', 'fibo2-batch-status');
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
            let row = this.element('div', 'fibo2-prompt-row');
            row.appendChild(this.element('strong', '', prompt.name || `Prompt ${i + 2}`));
            let text = this.element('textarea', 'fibo2-description');
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
        if (!captured.fiboadvanced || currentModelHelper?.curCompatClass != 'fibo-2' || !prompts.length || prompts.length > 64) {
            this.batchStatus.textContent = 'Select a Fibo 2.0 preset and enter between 1 and 64 prompts.';
            return;
        }
        // Reuse native progress, history and image display while isolating the batch's websocket.
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
                this.batchStatus.textContent = `Generating prompt ${completed + 1} of ${prompts.length}…`;
                await handler.one({...captured, prompt});
                completed++;
            }
            this.batchStatus.textContent = `${this.stopRequested ? 'Stopped' : 'Complete'}: ${completed} of ${prompts.length} prompts generated.`;
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

let fibo2UI = new Fibo2UI();
