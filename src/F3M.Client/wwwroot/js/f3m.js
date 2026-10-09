window.f3m = {
    /**
     * Triggers a browser file download from a base64-encoded byte array.
     * Called from Blazor via IJSRuntime.
     */
    downloadFile: function (fileName, base64Data) {
        const byteChars = atob(base64Data);
        const byteArrays = [];
        for (let offset = 0; offset < byteChars.length; offset += 512) {
            const slice = byteChars.slice(offset, offset + 512);
            const byteNums = new Array(slice.length);
            for (let i = 0; i < slice.length; i++) {
                byteNums[i] = slice.charCodeAt(i);
            }
            byteArrays.push(new Uint8Array(byteNums));
        }
        const blob = new Blob(byteArrays, { type: 'application/octet-stream' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    },

    /**
     * Programmatically click the hidden file input (for the custom dropzone).
     */
    triggerFileInput: function (inputId) {
        const el = document.getElementById(inputId);
        if (el) el.click();
    },

    /**
     * Hands a f3m:// link to the desktop app. Resolves true when the browser lost focus or the page was hidden
     * (the OS took over), false when nothing happened within the wait. Called from Blazor via IJSRuntime.
     */
    openInDesktop: function (uri) {
        return new Promise(function (resolve) {
            let handled = false;
            const markHandled = function () { handled = true; };
            const onVisibility = function () { if (document.hidden) markHandled(); };
            document.addEventListener('visibilitychange', onVisibility);
            window.addEventListener('blur', markHandled);

            const frame = document.createElement('iframe');
            frame.style.display = 'none';
            frame.src = uri;
            document.body.appendChild(frame);

            setTimeout(function () {
                document.removeEventListener('visibilitychange', onVisibility);
                window.removeEventListener('blur', markHandled);
                frame.remove();
                resolve(handled);
            }, 1500);
        });
    },

    /**
     * Copies text to the clipboard. Resolves true on success, false when the browser refuses.
     */
    copyText: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },

    /** Reads a value from localStorage. Returns null when storage is blocked or empty. */
    getItem: function (key) {
        try { return window.localStorage.getItem(key); } catch { return null; }
    },

    /** Writes a value to localStorage. Silently does nothing when storage is blocked. */
    setItem: function (key, value) {
        try { window.localStorage.setItem(key, value); } catch { /* storage blocked */ }
    }
};
