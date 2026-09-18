// Static exports have no Blazor runtime. Respect the resolved preference as well
// as OS changes, and keep the full-size link paired with the visible image.
function updateImages() {
    const dark = document.documentElement.classList.contains('dark');
    document.querySelectorAll('[data-theme-image-dark]').forEach(source => {
        source.media = dark ? 'all' : 'not all';
    });
    document.querySelectorAll('[data-theme-image-link]').forEach(link => {
        link.href = dark ? link.dataset.darkSrc : link.dataset.lightSrc;
    });
}

window.addEventListener('quark-theme-changed', updateImages);
updateImages();
