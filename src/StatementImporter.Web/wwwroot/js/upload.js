const maxBytes = 200 * 1024 * 1024;

export function upload(input, dotnet) {
    const file = input.files[0];
    if (!file) return Promise.resolve({ status: -1, body: '' });
    if (file.size > maxBytes) return Promise.resolve({ status: 413, body: '' });

    return new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        xhr.open('POST', `api/imports?fileName=${encodeURIComponent(file.name)}`);
        xhr.setRequestHeader('Content-Type', 'text/csv');
        xhr.upload.onprogress = e => {
            if (e.lengthComputable) dotnet.invokeMethodAsync('OnUploadProgress', e.loaded, e.total);
        };
        xhr.onload = () => resolve({ status: xhr.status, body: xhr.responseText });
        xhr.onerror = () => resolve({ status: 0, body: '' });
        xhr.send(file);
    });
}
