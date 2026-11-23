/* Rasterise with tools/make-icon.ps1 -Pairside. Geometry matches the native tray mark. */
(() => {
  window.WPods = {
    appIcon(size) {
      const crescent = 'M8.9 4.8C10.7 2.7 12.7 1.8 15.2 1.8C19.8 1.8 23 5.5 23 10C23 14.4 19.5 18.3 15.1 18.3C12.1 18.3 9.8 16.7 8.7 14.3H12C15.2 14.3 17.4 12.1 17.4 9.4C17.4 6.7 15.2 4.8 12.3 4.8Z';
      return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 24 24">
        <rect x=".4" y=".4" width="23.2" height="23.2" rx="5.7" fill="#101113"/>
        <g fill="#fafafa" transform="translate(2 2) scale(.833333)">
          <path d="${crescent}"/><path d="${crescent}" transform="rotate(180 12 12)"/>
        </g>
      </svg>`;
    },
  };
})();
