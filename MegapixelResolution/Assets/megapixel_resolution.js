class MegapixelResolutionUI {

    constructor() {
        postParamBuildSteps.push(() => this.install());
    }

    install() {
        let megapixels = document.getElementById('input_megapixels');
        let megapixelsToggle = document.getElementById('input_megapixels_toggle');
        let aspect = document.getElementById('input_aspectratio');
        let width = document.getElementById('input_width');
        let height = document.getElementById('input_height');
        let sideLength = document.getElementById('input_sidelength');
        let sideLengthToggle = document.getElementById('input_sidelength_toggle');
        let resolutionGroup = document.getElementById('input_group_content_resolution');
        if (!megapixels || !megapixelsToggle || !aspect || !width || !height || !sideLength || !sideLengthToggle
            || !resolutionGroup
            || megapixels.dataset.megapixelResolutionReady == 'true') {
            return;
        }
        megapixels.dataset.megapixelResolutionReady = 'true';

        let preview = document.createElement('div');
        preview.id = 'megapixel_resolution_preview';
        preview.className = 'megapixel-resolution-preview';
        preview.setAttribute('role', 'figure');
        preview.setAttribute('aria-live', 'polite');
        preview.setAttribute('aria-atomic', 'true');
        preview.innerHTML = `
            <div class="megapixel-resolution-preview-header">
                <strong class="megapixel-resolution-preview-aspect"></strong>
                <span class="megapixel-resolution-preview-mp"></span>
            </div>
            <div class="megapixel-resolution-preview-diagram" aria-hidden="true">
                <div class="megapixel-resolution-preview-width"><span></span></div>
                <div class="megapixel-resolution-preview-frame"></div>
                <div class="megapixel-resolution-preview-height"><span></span></div>
            </div>
            <div class="megapixel-resolution-preview-footer">
                <strong class="megapixel-resolution-preview-dimensions"></strong>
                <span class="megapixel-resolution-preview-pixels"></span>
            </div>`;
        resolutionGroup.appendChild(preview);

        let applying = false;
        let changingAspect = false;

        let disableMegapixels = () => {
            if (!applying && megapixelsToggle.checked) {
                megapixelsToggle.checked = false;
                doToggleEnable(megapixels.id);
            }
        };

        let apply = (normalize = false) => {
            if (applying || !megapixelsToggle.checked) {
                return;
            }
            let value = parseFloat(megapixels.value);
            let minimum = parseFloat(megapixels.min);
            let maximum = parseFloat(megapixels.max);
            if (!Number.isFinite(value)) {
                return;
            }
            if (normalize) {
                value = Math.max(minimum, Math.min(maximum, value));
                megapixels.value = formatNumberClean(value, 2);
                autoNumberWidth(megapixels);
            }
            else if (value < minimum || value > maximum) {
                return;
            }

            let ratio;
            let selectedRatio = aspectRatios.find(item => item.id == aspect.value);
            if (selectedRatio) {
                ratio = selectedRatio.ratio;
            }
            else {
                let currentWidth = parseFloat(width.value);
                let currentHeight = parseFloat(height.value);
                ratio = currentWidth > 0 && currentHeight > 0 ? currentWidth / currentHeight : 1;
            }

            let targetPixels = value * 1000000;
            let precision = getResolutionPrecision();
            let targetWidth = Math.max(64, Math.min(16384, roundTo(Math.sqrt(targetPixels * ratio), precision)));
            let targetHeight = Math.max(64, Math.min(16384, roundTo(Math.sqrt(targetPixels / ratio), precision)));

            applying = true;
            if (sideLengthToggle.checked) {
                sideLengthToggle.checked = false;
                doToggleEnable(sideLength.id);
            }
            width.value = targetWidth;
            height.value = targetHeight;
            triggerChangeFor(width);
            triggerChangeFor(height);
            applying = false;
        };

        aspect.addEventListener('change', () => {
            changingAspect = true;
        }, true);
        aspect.addEventListener('change', () => {
            if (!applying) {
                apply();
            }
            changingAspect = false;
        });

        let manualResolutionChange = () => {
            if (!applying && !changingAspect) {
                disableMegapixels();
            }
        };
        for (let input of [width, height, sideLength, sideLengthToggle]) {
            input.addEventListener('input', manualResolutionChange, true);
            input.addEventListener('change', manualResolutionChange, true);
        }

        megapixels.addEventListener('input', () => apply());
        megapixels.addEventListener('change', () => apply(true));
        megapixelsToggle.addEventListener('change', () => apply(true));

        let renderTimer = null;
        let fitPreview = (previewWidth, previewHeight) => {
            let containerWidth = preview.clientWidth;
            if (containerWidth <= 0 || previewWidth <= 0 || previewHeight <= 0) {
                return;
            }
            let availableWidth = Math.max(80, containerWidth - 70);
            let availableHeight = Math.max(96, Math.min(200, availableWidth * 0.6));
            let scale = Math.min(availableWidth / previewWidth, availableHeight / previewHeight);
            preview.style.setProperty('--megapixel-preview-width', `${Math.max(2, Math.floor(previewWidth * scale))}px`);
            preview.style.setProperty('--megapixel-preview-height', `${Math.max(2, Math.floor(previewHeight * scale))}px`);
        };

        let renderPreview = () => {
            let previewWidth = parseInt(width.value);
            let previewHeight = parseInt(height.value);
            if (!Number.isFinite(previewWidth) || !Number.isFinite(previewHeight)
                || previewWidth <= 0 || previewHeight <= 0) {
                preview.hidden = true;
                return;
            }
            preview.hidden = false;
            let selectedLabel = aspect.options[aspect.selectedIndex]?.textContent?.trim() || aspect.value;
            if (aspect.value == 'Custom') {
                selectedLabel = `${describeAspectRatio(previewWidth, previewHeight)} (Custom)`;
            }
            let totalPixels = previewWidth * previewHeight;
            let actualMegapixels = totalPixels / 1000000;
            let megapixelText = formatNumberClean(actualMegapixels, actualMegapixels < 1 ? 3 : 2);
            preview.querySelector('.megapixel-resolution-preview-aspect').textContent = selectedLabel;
            preview.querySelector('.megapixel-resolution-preview-mp').textContent = `${megapixelText} MP`;
            preview.querySelector('.megapixel-resolution-preview-width span').textContent = `${previewWidth} px`;
            preview.querySelector('.megapixel-resolution-preview-height span').textContent = `${previewHeight} px`;
            preview.querySelector('.megapixel-resolution-preview-dimensions').textContent = `${previewWidth} x ${previewHeight}`;
            preview.querySelector('.megapixel-resolution-preview-pixels').textContent = `${new Intl.NumberFormat().format(totalPixels)} pixels`;
            preview.setAttribute('aria-label', `${selectedLabel}: ${previewWidth} by ${previewHeight} pixels, ${megapixelText} megapixels`);
            preview.dataset.width = previewWidth;
            preview.dataset.height = previewHeight;
            fitPreview(previewWidth, previewHeight);
        };

        let schedulePreview = (immediate = false) => {
            window.clearTimeout(renderTimer);
            if (immediate) {
                renderPreview();
            }
            else {
                renderTimer = window.setTimeout(renderPreview, 1000);
            }
        };
        for (let input of [megapixels, megapixelsToggle, aspect, width, height, sideLength, sideLengthToggle]) {
            input.addEventListener('input', () => schedulePreview());
            input.addEventListener('change', () => schedulePreview());
        }

        let resizeObserver = new ResizeObserver(() => {
            if (!preview.isConnected) {
                resizeObserver.disconnect();
                return;
            }
            fitPreview(parseInt(preview.dataset.width), parseInt(preview.dataset.height));
        });
        resizeObserver.observe(preview);

        if (megapixelsToggle.checked) {
            apply(true);
        }
        schedulePreview(true);
    }
}

let megapixelResolutionUI = new MegapixelResolutionUI();
