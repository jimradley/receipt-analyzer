// WebAuthn browser interop. Plain-JS transpile of Fido2.BlazorWebAssembly's WebAuthn.ts
// (the NuGet package ships only the .ts, which our build doesn't compile — so we host our own).
// Handles the base64url <-> ArrayBuffer coercion the WebAuthn API requires.
//
// createCreds/verify take and return a JSON *string*, not an object. The C# side serialises the
// Fido option types with the source-generated FidoBlazorSerializerContext and deserialises the
// result the same way — keeping reflection-based System.Text.Json (which the WASM trimmer breaks by
// stripping the Fido model constructors' parameter names) entirely off the interop boundary.

export function isWebAuthnPossible() {
    return !!window.PublicKeyCredential;
}

function toBase64Url(arrayBuffer) {
    return btoa(String.fromCharCode(...new Uint8Array(arrayBuffer)))
        .replace(/\+/g, "-").replace(/\//g, "_").replace(/=*$/g, "");
}

function fromBase64Url(value) {
    return Uint8Array.from(atob(value.replace(/-/g, "+").replace(/_/g, "/")), c => c.charCodeAt(0));
}

function base64StringToUrl(base64String) {
    return base64String.replace(/\+/g, "-").replace(/\//g, "_").replace(/=*$/g, "");
}

export async function createCreds(optionsJson) {
    const options = JSON.parse(optionsJson);
    if (typeof options.challenge === 'string')
        options.challenge = fromBase64Url(options.challenge);
    if (typeof options.user.id === 'string')
        options.user.id = fromBase64Url(options.user.id);
    if (options.rp.id === null)
        options.rp.id = undefined;
    for (const cred of (options.excludeCredentials || [])) {
        if (typeof cred.id === 'string')
            cred.id = fromBase64Url(cred.id);
    }

    const newCreds = await navigator.credentials.create({ publicKey: options });
    const response = newCreds.response;
    return JSON.stringify({
        id: base64StringToUrl(newCreds.id),
        rawId: toBase64Url(newCreds.rawId),
        type: newCreds.type,
        clientExtensionResults: newCreds.getClientExtensionResults(),
        response: {
            attestationObject: toBase64Url(response.attestationObject),
            clientDataJSON: toBase64Url(response.clientDataJSON),
            transports: response.getTransports ? response.getTransports() : []
        }
    });
}

export async function verify(optionsJson) {
    const options = JSON.parse(optionsJson);
    if (typeof options.challenge === 'string')
        options.challenge = fromBase64Url(options.challenge);
    if (options.allowCredentials) {
        for (let i = 0; i < options.allowCredentials.length; i++) {
            const id = options.allowCredentials[i].id;
            if (typeof id === 'string')
                options.allowCredentials[i].id = fromBase64Url(id);
        }
    }

    const creds = await navigator.credentials.get({ publicKey: options });
    const response = creds.response;
    return JSON.stringify({
        id: creds.id,
        rawId: toBase64Url(creds.rawId),
        type: creds.type,
        clientExtensionResults: creds.getClientExtensionResults(),
        response: {
            authenticatorData: toBase64Url(response.authenticatorData),
            clientDataJSON: toBase64Url(response.clientDataJSON),
            userHandle: response.userHandle && response.userHandle.byteLength > 0 ? toBase64Url(response.userHandle) : undefined,
            signature: toBase64Url(response.signature)
        }
    });
}
