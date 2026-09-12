window.shoppingList = {
    createDraft: async function (text) {
        let copied = false;

        try {
            await navigator.clipboard.writeText(text);
            copied = true;
        } catch {
            const textarea = document.createElement('textarea');
            textarea.value = text;
            textarea.setAttribute('readonly', '');
            textarea.style.position = 'fixed';
            textarea.style.opacity = '0';
            document.body.appendChild(textarea);
            textarea.select();
            try {
                copied = document.execCommand('copy');
            } catch {
                copied = false;
            } finally {
                textarea.remove();
            }
        }

        const subject = encodeURIComponent('Shopping list');
        const body = encodeURIComponent(text);
        window.location.href = `mailto:?subject=${subject}&body=${body}`;
        return copied;
    }
};
