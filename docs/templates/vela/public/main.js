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
