// Static exports do not run Blazor's OnAfterRenderAsync. Initialize the rendered
// Quark CodeEditor through the same interop module used by the component.
const examples = document.querySelectorAll('.code-example');
let interopPromise;

function loadInterop() {
    return interopPromise ??= (async () => {
        const base = '/_content/Soenneker.Quark.Suite/js/';
        const stylesheet = document.createElement('link');
        stylesheet.rel = 'stylesheet';
        stylesheet.href = `${base}monaco-editor/monaco.editor.main.esm.css`;
        const stylesReady = new Promise((resolve, reject) => {
            stylesheet.onload = resolve;
            stylesheet.onerror = reject;
        });
        document.head.append(stylesheet);
        const interop = await import(`${base}monacointerop.js`);
        interop.ensureConfigured(`${base}monaco-editor/monaco.editor.main.esm.js`, {
            editor: `${base}monaco-editor/workers/editor.worker.esm.js`
        });
        await stylesReady;
        return interop;
    })();
}

async function initialize(example) {
    const fallback = example.querySelector('.code-fallback');
    const host = example.querySelector('.quark-editor');
    const container = host.querySelector('[data-slot="code-editor"]');
    const text = fallback.textContent;
    try {
        const interop = await loadInterop();
        container.style.height = `${text.split('\n').length * 21 + 36}px`;
        host.hidden = false;
        await interop.createEditor(container, JSON.stringify({
            value: text,
            language: example.dataset.language,
            ariaLabel: example.dataset.label,
            theme: 'quarkLight',
            readOnly: true,
            domReadOnly: true,
            minimap: { enabled: false },
            automaticLayout: true,
            fontSize: 12,
            lineHeight: 21,
            fontFamily: 'Consolas, monospace',
            lineNumbers: 'off',
            glyphMargin: false,
            folding: false,
            lineDecorationsWidth: 16,
            overviewRulerLanes: 0,
            renderLineHighlight: 'none',
            scrollBeyondLastLine: false,
            padding: { top: 12, bottom: 12 },
            scrollbar: { alwaysConsumeMouseWheel: false, vertical: 'hidden', horizontalScrollbarSize: 6 },
            wordWrap: 'off'
        }));
        fallback.hidden = true;
        example.dataset.ready = 'true';
        const copy = host.querySelector('button');
        copy.setAttribute('aria-label', `Copy ${example.dataset.label.toLowerCase()}`);
        copy.addEventListener('click', async () => {
            const status = example.querySelector('.copy-status');
            try {
                await navigator.clipboard.writeText(text);
                status.textContent = 'Copied';
            } catch {
                status.textContent = 'Select the code and copy it with your keyboard.';
            }
            setTimeout(() => { status.textContent = ''; }, 2500);
        });
    } catch (error) {
        host.hidden = true;
        fallback.hidden = false;
        console.error('Unable to initialize code example.', error);
    }
}

// Load Monaco only when examples approach the viewport; keep readable HTML
// for search engines, disabled JavaScript, and failed resource requests.
const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        observer.unobserve(entry.target);
        void initialize(entry.target);
    }
}, { rootMargin: '300px' });
examples.forEach(example => observer.observe(example));
