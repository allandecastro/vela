// Make the Vela favicon resolve everywhere. DocFX emits a single `<link rel="icon" href=".../vela-favicon.svg">`
// with no type. Browsers that do not render SVG favicons (older Safari) then fall back to the domain-root
// /favicon.ico, which on a GitHub Pages project site (/vela/) is not this site's icon. So tag the SVG link with
// its type (modern browsers keep using the crisp SVG) and add a correctly-pathed .ico fallback next to it.
(function ensureFavicon() {
  function fix() {
    var head = document.head;
    if (!head) return;
    var svg = head.querySelector('link[rel~="icon"][href$=".svg"]');
    if (!svg) return;
    svg.setAttribute('type', 'image/svg+xml');
    if (!head.querySelector('link[rel~="icon"][href$="favicon.ico"]')) {
      var ico = document.createElement('link');
      ico.setAttribute('rel', 'icon');
      ico.setAttribute('sizes', 'any');
      // Reuse the SVG link's per-page relative prefix (DocFX rewrites it to ../ on sub-pages).
      ico.setAttribute('href', svg.getAttribute('href').replace(/images\/[^/]+\.svg$/, 'favicon.ico'));
      head.appendChild(ico);
    }
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', fix);
  } else {
    fix();
  }
})();

// Click a Mermaid diagram to see it full screen; click again or press Escape to close. Mermaid renders the SVG after
// the page loads, so the click is caught on the document rather than bound to each diagram.
(function zoomDiagrams() {
  function close() {
    var overlay = document.querySelector('.vela-zoom');
    if (overlay) overlay.remove();
    document.removeEventListener('keydown', onKey);
  }
  function onKey(e) {
    if (e.key === 'Escape') close();
  }
  document.addEventListener('click', function (e) {
    if (document.querySelector('.vela-zoom')) {
      close();
      return;
    }
    var diagram = e.target.closest && e.target.closest('.mermaid');
    var svg = diagram && diagram.querySelector('svg');
    if (!svg || e.target.closest('a')) return;
    var overlay = document.createElement('div');
    overlay.className = 'vela-zoom';
    overlay.setAttribute('role', 'dialog');
    overlay.setAttribute('aria-label', 'Diagram, full screen (click or press Escape to close)');
    // Keep the .mermaid class so the diagram styles of main.css still apply to the copy.
    var content = document.createElement('div');
    content.className = 'mermaid vela-zoom-content';
    var copy = svg.cloneNode(true);
    copy.removeAttribute('style');
    copy.removeAttribute('width');
    copy.removeAttribute('height');
    content.appendChild(copy);
    overlay.appendChild(content);
    document.body.appendChild(overlay);
    document.addEventListener('keydown', onKey);
  });
})();

// DocFX "modern" template config (read via its `import('./main.js').default`).
export default {
  // Show light mode by default; the visitor can still switch to dark or auto.
  defaultTheme: 'light',
  // Navbar icon links (Bootstrap Icons names).
  iconLinks: [
    {
      icon: 'github',
      href: 'https://github.com/allandecastro/vela',
      title: 'GitHub',
    },
    {
      icon: 'bug',
      href: 'https://github.com/allandecastro/vela/issues/new/choose',
      title: 'Report an issue',
    },
  ],
};
