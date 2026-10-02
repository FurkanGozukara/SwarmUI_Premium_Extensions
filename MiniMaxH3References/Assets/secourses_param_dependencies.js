// SECourses Parameter Dependencies: makes SwarmUI's DependNonDefault rule work when the parameter depended on is a
// checkbox.
//
// A parameter registered with DependNonDefault should stay hidden, and out of the generation request, while the
// parameter it depends on (its master) has its default value. The core compares the master's value with that default
// using `==`, but a checkbox value is a boolean while the default arrives as the string 'false', and in JavaScript
// `false == 'false'` is false. So every option under an enable checkbox (AvatarForever, Qwen Unified, SVI Pro,
// LTX 2.3 Foley, LTX 2.5 Audio To Video, Licon MSR, MiniMax H3 References, Video Face Inpainting, ...) stayed visible
// and was sent with every generation while its feature was off. The server then listed those options under
// "unused_parameters" in the image metadata and listed and hashed every model file they name under "sui_models".
//
// This file applies the core rule with a type-safe comparison for checkbox masters: it hides those options and
// removes them from what getGenInput returns. Other masters are compared exactly as the core does, so once the core
// compares checkboxes correctly this file removes nothing more.

/** Applies SwarmUI's DependNonDefault rule (hide, and do not send, a parameter while its master is at its default)
 * correctly for checkbox masters. */
class SECoursesParamDependencies {

    /** Returns the parameter that a DependNonDefault parameter depends on, or null. */
    getMaster(param) {
        if (!param.depend_non_default) {
            return null;
        }
        return gen_param_types.find(p => p.id == param.depend_non_default) || null;
    }

    /** Returns whether a value equals the parameter's default. Checkbox values are compared as text
     * (the core's `false == 'false'` is false); every other type exactly as the core compares it. */
    isDefaultValue(param, value) {
        if (param.type == 'boolean') {
            return `${value}`.toLowerCase() == `${param.default}`.toLowerCase();
        }
        return value == param.default;
    }

    /** Removes from a getGenInput result each parameter whose master is not sent or has its default value, unless the
     * caller's overrides set the parameter or its master (the core's own condition). Repeats until nothing changes, so a
     * chain such as SVI Source Video -> SVI Continue Video -> SVI Pro Enabled is removed as a whole. */
    removeInactive(input, inputOverrides) {
        let removed = true;
        while (removed) {
            removed = false;
            for (let param of gen_param_types) {
                let master = this.getMaster(param);
                if (!master || !(param.id in input) || param.id in inputOverrides || master.id in inputOverrides) {
                    continue;
                }
                let masterElem = document.getElementById(`input_${master.id}`);
                if (!masterElem || masterElem.dataset.has_data) {
                    continue;
                }
                if (!(master.id in input) || this.isDefaultValue(master, input[master.id])) {
                    delete input[param.id];
                    removed = true;
                }
            }
        }
    }

    /** Returns whether a master currently enables the parameters that depend on it, by the core's visibility rule
     * (a toggleable master needs its toggle on, any other master a non-default value) and then its own master. */
    isMasterActive(master, visited = []) {
        let elem = document.getElementById(`input_${master.id}`);
        if (!elem || elem.dataset.has_data) {
            return true;
        }
        let toggler = document.getElementById(`input_${master.id}_toggle`);
        if (toggler) {
            if (!toggler.checked) {
                return false;
            }
        }
        else if (this.isDefaultValue(master, getInputVal(elem))) {
            return false;
        }
        let parent = this.getMaster(master);
        if (parent && !visited.includes(parent.id)) {
            return this.isMasterActive(parent, [...visited, master.id]);
        }
        return true;
    }

    /** hideParamCallbacks entry: hides every parameter whose master is off, and takes it back out of the group counts
     * the core collected for showing groups and their altered-parameter counters. */
    hideInactive(groups) {
        for (let param of gen_param_types) {
            let master = this.getMaster(param);
            if (!master || this.isMasterActive(master)) {
                continue;
            }
            let elem = document.getElementById(`input_${param.id}`);
            let box = elem ? findParentOfClass(elem, 'auto-input') : null;
            if (!box || box.dataset.visible_controlled || box.style.display == 'none') {
                continue;
            }
            box.style.display = 'none';
            let toggler = document.getElementById(`input_${param.id}_toggle`);
            let isAltered = toggler ? toggler.checked : `${getInputVal(elem)}` != `${param.default}`;
            let toggleGroup = param.original_group || param.group;
            if (toggleGroup && toggleGroup.toggles && !document.getElementById(`input_group_content_${toggleGroup.id}_toggle`)?.checked) {
                isAltered = false;
            }
            let group = param.group;
            while (group) {
                let groupData = groups[group.id];
                if (groupData) {
                    groupData.visible--;
                    if (isAltered) {
                        groupData.altered--;
                    }
                }
                group = group.parent;
            }
        }
    }

    /** Wraps the core getGenInput, so Generate and every other caller (grids, batch tools, Comfy workflow import) get
     * the cleaned input, and registers the visibility callback. Does nothing when already installed.
     * Any error falls back to the core behavior, so this helper can never break generating or the parameter list. */
    install() {
        if (typeof getGenInput != 'function' || getGenInput.secoursesParamDependencies) {
            return;
        }
        let coreGetGenInput = getGenInput;
        let helper = this;
        let wrapped = function (input_overrides = {}, input_preoverrides = {}) {
            let input = coreGetGenInput(input_overrides, input_preoverrides);
            try {
                helper.removeInactive(input, input_overrides || {});
            }
            catch (error) {
                console.warn('SECourses parameter dependencies: sending the unfiltered input', error);
            }
            return input;
        };
        wrapped.secoursesParamDependencies = true;
        getGenInput = wrapped;
        if (typeof hideParamCallbacks != 'undefined') {
            hideParamCallbacks.push(groups => {
                try {
                    this.hideInactive(groups);
                }
                catch (error) {
                    console.warn('SECourses parameter dependencies: could not hide inactive options', error);
                }
            });
        }
    }
}

secoursesParamDependencies = new SECoursesParamDependencies();
secoursesParamDependencies.install();
