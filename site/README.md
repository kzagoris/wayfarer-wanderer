# Wayfarer Wanderer site

The landing page: Hugo + Tailwind CSS v4 (Hugo's `css.TailwindCSS`) + Alpine.js.

```sh
cd site
npm install
hugo server            # http://localhost:1313
hugo --minify          # static output in site/public, hostable anywhere (GitHub Pages, Cloudflare Pages, Netlify)
```

- Product facts (links, downloads, commands, keys, shipped list): `data/product.yaml`. Page: `layouts/home.html`.
- Styles and motion: `assets/css/standard.css`; behaviour: `assets/js/standard.js`.
- Brand: Ridgeline (`docs/brand/ridgeline.svg`), Deep Teal `#103D40` and Trail Amber `#FFBE50`. The favicon and
  `static/img/icon.{png,svg}` are exported from that master.
- App pictures come from `src/WayfarerWanderer.App/Shell/Assets/FirstStart/` (copied into `static/img/`); copy them
  again after `tools/first-start-pictures.py`.
- Scroll motion uses CSS scroll-driven animations, with an IntersectionObserver fallback; reduced motion turns it off.
  Add `?still` to the URL to freeze it for screenshots.
