# Wayfarer icon and theme handoff

![Ridgeline in Trail Amber](ridgeline.svg)

## Approved artwork

Use [ridgeline.svg](ridgeline.svg) as the master. The mark is a solid Trail Amber W (`#FFBE50`) on a Deep Teal tile (`#103D40`), with transparent corners. Preserve the 100-unit viewBox, 22-unit corner radius, asymmetric W and diagonal right tip at every size. Keep this color arrangement in both appearances.

## Install the icon

1. Update [the icon generator](../../tools/app-icon.py) to render the SVG master. The current script redraws the placeholder compass and would restore it on the next run.
2. Regenerate the 256 px PNG and the ICO in `src/WayfarerWanderer.App/Shell/Assets/`. Include 16, 20, 24, 32, 40, 48, 64, 128 and 256 px ICO frames. Keep the existing filenames so the executable, window style, first-start screen and welcome screen pick them up.
3. In the website checkout, export the same mark to `site/static/favicon.png` and `site/static/img/icon.png`. Update the compass description in the Brand Commitments section of `PRODUCT.md`.

## Apply the palette to the default Wayfarer theme

The following UI assignments are proposed. The two master icon colors and geometry are approved.

Edit the Dark and Light dictionaries in [Tokens.axaml](../../src/WayfarerWanderer.App/Shell/Theme/Tokens.axaml). Keep controls bound to the existing token keys. Limit palette edits to the default Wayfarer appearance and preserve the independent Cubicle, High Contrast and imported themes.

| Surface | Dark appearance | Light appearance |
| --- | --- | --- |
| Title bar | Anchor the bar in Deep Teal. Check title text, caption buttons and their hover states. | Use the same teal for a branded header with light lettering. |
| Primary action | Try Trail Amber fill and Deep Teal label. Tune hover and pressed fills with the same label. | Use Deep Teal fill with light text. |
| Keyboard focus | Try Trail Amber on dark panels and the title bar. | Use Deep Teal on pale surfaces and Trail Amber on the teal header. |
| Editor and diagnostics | Retain the editor’s neutral ground and semantic syntax, warning and error roles. | Retain the light editor ground and independently legible diagnostic roles. |

Start with the `Passenger.TitleBar`, `Passenger.Accent`, `Passenger.AccentText` and `Passenger.Focus` token families. Check every surface sharing `AccentText`, including `Welcome.Badge`. Verify actual text/background combinations at 4.5:1 and meaningful focus/state indicators at 3:1. Use the existing contrast annotations to identify the surfaces each token must work on.

## Verify the integration

Inspect the exported 16 and 32 px icons at native size on light and dark desktops. Check that both valleys are open, the right tip rises above the left, and there is no fringe around the tile. Verify the executable icon, a window icon on a platform that displays it, the welcome mark and the favicon. Switch Wayfarer between Dark and Light, tab through the primary action and editor, then switch to High Contrast and Cubicle to check theme isolation. Refresh the Wayfarer first-start preview images after the themes are implemented.
