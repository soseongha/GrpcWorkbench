window.workbench = window.workbench || {};

window.workbench.downloadText = (fileName, content, mimeType) => {
    const blob = new Blob([content], { type: mimeType || "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
};

window.workbench.resizableSplit = (() => {
    const states = new WeakMap();

    const parsePx = (value, fallback) => {
        const parsed = Number.parseFloat(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    };

    const clamp = (root, value, options) => {
        const width = root.getBoundingClientRect().width;
        const handle = root.querySelector(".wb-resizable-handle");
        const handleWidth = handle ? handle.getBoundingClientRect().width : 10;
        const max = Math.max(options.minPrimary, width - handleWidth - options.minSecondary);
        return Math.min(Math.max(value, options.minPrimary), max);
    };

    const save = (options, value) => {
        if (!options.storageKey)
            return;

        try {
            window.localStorage.setItem(options.storageKey, String(Math.round(value)));
        } catch {
        }
    };

    const setPrimary = (root, value, options, persist) => {
        const clamped = clamp(root, value, options);
        root.style.setProperty("--wb-split-primary", `${Math.round(clamped)}px`);
        if (persist)
            save(options, clamped);
        return clamped;
    };

    const readCurrent = (root, options) => {
        const raw = root.style.getPropertyValue("--wb-split-primary")
            || getComputedStyle(root).getPropertyValue("--wb-split-primary");
        return parsePx(raw, options.defaultPrimary);
    };

    const dispose = (root) => {
        const state = states.get(root);
        if (!state)
            return;

        state.abort.abort();
        states.delete(root);
    };

    const init = (root, options) => {
        if (!root)
            return;

        dispose(root);

        const normalized = {
            storageKey: options?.storageKey || "",
            defaultPrimary: Number(options?.defaultPrimary || 280),
            minPrimary: Number(options?.minPrimary || 180),
            minSecondary: Number(options?.minSecondary || 240)
        };

        let initial = normalized.defaultPrimary;
        if (normalized.storageKey) {
            try {
                initial = parsePx(window.localStorage.getItem(normalized.storageKey), normalized.defaultPrimary);
            } catch {
            }
        }

        setPrimary(root, initial, normalized, false);

        const handle = root.querySelector(".wb-resizable-handle");
        const abort = new AbortController();
        const state = { abort, options: normalized };
        states.set(root, state);

        if (!handle)
            return;

        let drag = null;
        let pending = null;
        let frame = 0;

        const flush = () => {
            frame = 0;
            if (!drag || pending == null)
                return;
            setPrimary(root, pending, normalized, false);
        };

        const onPointerMove = (event) => {
            if (!drag)
                return;

            pending = drag.startWidth + event.clientX - drag.startX;
            if (!frame)
                frame = window.requestAnimationFrame(flush);
        };

        const endDrag = () => {
            if (!drag)
                return;

            if (frame) {
                window.cancelAnimationFrame(frame);
                frame = 0;
            }

            const finalWidth = pending ?? readCurrent(root, normalized);
            setPrimary(root, finalWidth, normalized, true);
            root.classList.remove("is-resizing");
            drag = null;
            pending = null;
        };

        handle.addEventListener("pointerdown", (event) => {
            if (event.button !== 0)
                return;

            event.preventDefault();
            drag = {
                startX: event.clientX,
                startWidth: readCurrent(root, normalized)
            };
            pending = drag.startWidth;
            root.classList.add("is-resizing");
            handle.setPointerCapture?.(event.pointerId);
        }, { signal: abort.signal });

        handle.addEventListener("pointermove", onPointerMove, { signal: abort.signal });
        handle.addEventListener("pointerup", endDrag, { signal: abort.signal });
        handle.addEventListener("pointercancel", endDrag, { signal: abort.signal });
        window.addEventListener("resize", () => {
            setPrimary(root, readCurrent(root, normalized), normalized, true);
        }, { signal: abort.signal });
    };

    const withState = (root, action) => {
        const state = states.get(root);
        if (!root || !state)
            return;
        action(state.options);
    };

    return {
        init,
        dispose,
        nudge: (root, delta) => withState(root, options => {
            setPrimary(root, readCurrent(root, options) + Number(delta || 0), options, true);
        }),
        reset: (root) => withState(root, options => {
            if (options.storageKey) {
                try {
                    window.localStorage.removeItem(options.storageKey);
                } catch {
                }
            }
            setPrimary(root, options.defaultPrimary, options, false);
        }),
        setToMin: (root) => withState(root, options => {
            setPrimary(root, options.minPrimary, options, true);
        }),
        setToMax: (root) => withState(root, options => {
            setPrimary(root, Number.MAX_SAFE_INTEGER, options, true);
        })
    };
})();
