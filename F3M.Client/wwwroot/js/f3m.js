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
    }
};
