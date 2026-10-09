/** Render the shared picker over the ordinary saved/API parameter. */
function secoursesInstallRefModPicker() {
    let field = document.getElementById('input_minimaxhrefmodselection');
    if (!field || field.dataset.refmodPicker) return;
    field.dataset.refmodPicker = 'true';
    let root = document.createElement('div');
    field.style.display = 'none';
    field.insertAdjacentElement('afterend', root);
    let picker = new globalThis.SECoursesRefModPicker(root, {
        base: '/ComfyBackendDirect',
        getValue: () => field.value,
        setValue: value => {
            field.value = value;
            field.dispatchEvent(new Event('input', { bubbles: true }));
            field.dispatchEvent(new Event('change', { bubbles: true }));
        },
    });
    field.addEventListener('change', () => picker.render());
}

if (typeof featureSetChangers != 'undefined') {
    featureSetChangers.push(() => {
        let flag = 'minimax_h3_refmods';
        let result = minimaxH3NodeGatedFeature(flag);
        if (result[1].includes(flag) && minimaxH3VideoModelSelected()) return [[flag], []];
        return result;
    });
}
new MutationObserver(secoursesInstallRefModPicker).observe(document.body, { childList: true, subtree: true });
secoursesInstallRefModPicker();
