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
        if (!megapixels || !megapixelsToggle || !aspect || !width || !height || !sideLength || !sideLengthToggle
            || megapixels.dataset.megapixelResolutionReady == 'true') {
            return;
        }
        megapixels.dataset.megapixelResolutionReady = 'true';

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

        if (megapixelsToggle.checked) {
            apply(true);
        }
    }
}

let megapixelResolutionUI = new MegapixelResolutionUI();
