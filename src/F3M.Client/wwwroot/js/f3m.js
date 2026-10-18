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
     * How many cards of a CSS grid fit on the screen: its columns times the rows the window is tall, measured on the
     * cards (or placeholders) it shows. Called from Blazor via IJSRuntime.
     */
    gridScreenful: function (grid) {
        if (!grid || !grid.firstElementChild) return 0;
        const style = getComputedStyle(grid);
        const columns = style.gridTemplateColumns.split(' ').filter(Boolean).length || 1;
        const rowHeight = grid.firstElementChild.getBoundingClientRect().height + (parseFloat(style.rowGap) || 0);
        const rows = rowHeight > 0 ? Math.ceil(window.innerHeight / rowHeight) : 1;
        return columns * Math.max(1, rows);
    },

    /**
     * Infinite scroll: calls dotNet.LoadMore() whenever `sentinel` (an element after the list) comes within
     * `margin` pixels of the screen, and again after each batch while it is still that close. LoadMore resolves
     * to false when there is nothing more to load. Returns { check, dispose }: check() looks again (after the list
     * was replaced), dispose() stops watching.
     */
    watchListEnd: function (sentinel, dotNet, margin) {
        let busy = false;
        const near = () => sentinel.isConnected && sentinel.getBoundingClientRect().top < window.innerHeight + margin;
        const load = async () => {
            if (busy) return;
            busy = true;
            try {
                let more = true;
                while (more && near()) more = await dotNet.invokeMethodAsync('LoadMore');
            } catch { /* the page is going away */ }
            finally { busy = false; }
        };
        const observer = new IntersectionObserver(
            entries => { if (entries.some(e => e.isIntersecting)) load(); },
            { rootMargin: margin + 'px 0px' });
        observer.observe(sentinel);
        return { check: load, dispose: () => observer.disconnect() };
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
     * POSTs a request body with XMLHttpRequest, which (unlike fetch) reports how much of it was sent. Calls
     * dotNet.invokeMethodAsync('OnUploadProgress', sent, total) at most once per percent. Resolves to
     * { status, body, warnings } (warnings: the X-F3M-Warnings header, or null); rejects when the connection fails.
     */
    uploadWithProgress: function (url, contentType, body, dotNet) {
        return new Promise(function (resolve, reject) {
            const xhr = new XMLHttpRequest();
            xhr.open('POST', url);
            xhr.withCredentials = true;
            xhr.setRequestHeader('Content-Type', contentType);
            xhr.setRequestHeader('X-Requested-With', 'XMLHttpRequest');

            let lastPercent = -1;
            xhr.upload.onprogress = function (e) {
                if (!e.lengthComputable) return;
                const percent = Math.floor(e.loaded * 100 / e.total);
                if (percent === lastPercent) return;
                lastPercent = percent;
                dotNet.invokeMethodAsync('OnUploadProgress', e.loaded, e.total).catch(function () { /* the page is going away */ });
            };
            xhr.onload = function () {
                resolve({ status: xhr.status, body: xhr.responseText, warnings: xhr.getResponseHeader('X-F3M-Warnings') });
            };
            xhr.onerror = xhr.onabort = xhr.ontimeout = function () {
                reject(new Error('The upload was interrupted.'));
            };
            xhr.send(body);
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

    /** Closes the navigation menu when it is open (small screens), after the visitor picked a page in it. */
    closeNavMenu: function () {
        const menu = document.getElementById('navMenu');
        if (menu && menu.classList.contains('show') && window.bootstrap) {
            window.bootstrap.Collapse.getOrCreateInstance(menu, { toggle: false }).hide();
        }
    },

    /** Reads a value from localStorage. Returns null when storage is blocked or empty. */
    getItem: function (key) {
        try { return window.localStorage.getItem(key); } catch { return null; }
    },

    /** Writes a value to localStorage. Silently does nothing when storage is blocked. */
    setItem: function (key, value) {
        try { window.localStorage.setItem(key, value); } catch { /* storage blocked */ }
    },

    /**
     * A random id kept in this browser, so the statistics page counts a visitor once per day. Null when the browser
     * asks not to be tracked (Do Not Track, Global Privacy Control) or storage is blocked.
     */
    visitorId: function () {
        if (navigator.doNotTrack === '1' || navigator.globalPrivacyControl === true) return null;
        try {
            let id = window.localStorage.getItem('f3m.visitor');
            if (!id) {
                id = window.crypto && crypto.randomUUID
                    ? crypto.randomUUID()
                    : Array.from({ length: 4 }, () => Math.random().toString(36).slice(2, 10)).join('');
                window.localStorage.setItem('f3m.visitor', id);
            }
            return id;
        } catch {
            return null;
        }
    },

    /**
     * Calls dotNet.invokeMethodAsync('OnWidth', width) with the element's width now and whenever it changes (charts
     * draw at their real size). Returns { dispose }.
     */
    watchWidth: function (element, dotNet) {
        const observer = new ResizeObserver(entries => {
            const width = Math.round(entries[0].contentRect.width);
            if (width > 0) dotNet.invokeMethodAsync('OnWidth', width).catch(() => { /* the page is going away */ });
        });
        observer.observe(element);
        return { dispose: () => observer.disconnect() };
    }
};
