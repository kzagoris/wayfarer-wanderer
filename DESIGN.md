---
name: Wayfarer Wanderer
description: Night-teal ground, Trail Amber action colour, and the Ridgeline zigzag for the open Java and Kotlin IDE.
colors:
  amber: "oklch(0.85 0.14 75)"
  amber-deep: "oklch(0.8 0.145 70)"
  amber-ink: "oklch(0.33 0.052 201)"
  teal: "oklch(0.335 0.052 201)"
  java: "oklch(0.82 0.085 190)"
  kotlin: "oklch(0.76 0.13 300)"
  code-string: "oklch(0.82 0.11 145)"
  night: "oklch(0.17 0.024 205)"
  night-2: "oklch(0.205 0.03 204)"
  night-3: "oklch(0.245 0.036 203)"
  line: "oklch(0.32 0.034 203)"
  fog: "oklch(0.77 0.025 198)"
  snow: "oklch(0.965 0.008 190)"
typography:
  display:
    fontFamily: "Archivo, Geist, ui-sans-serif, sans-serif"
    fontSize: "clamp(2.25rem, 4.9vw, 4.15rem)"
    fontWeight: 800
    lineHeight: 1
    letterSpacing: "-0.035em"
  headline:
    fontFamily: "Archivo, Geist, ui-sans-serif, sans-serif"
    fontSize: "clamp(2rem, 3.8vw, 3rem)"
    fontWeight: 800
    lineHeight: 1.02
    letterSpacing: "-0.035em"
  title:
    fontFamily: "Archivo, Geist, ui-sans-serif, sans-serif"
    fontSize: "1.5rem"
    fontWeight: 800
    lineHeight: 1.2
    letterSpacing: "-0.035em"
  lede:
    fontFamily: "Geist, ui-sans-serif, system-ui, sans-serif"
    fontSize: "clamp(1.05rem, 1.5vw, 1.2rem)"
    fontWeight: 400
    lineHeight: 1.6
  body:
    fontFamily: "Geist, ui-sans-serif, system-ui, sans-serif"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "Geist, ui-sans-serif, system-ui, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 500
    lineHeight: 1.4
  code:
    fontFamily: "Geist Mono, ui-monospace, monospace"
    fontSize: "clamp(0.76rem, 2.2vw, 0.86rem)"
    fontWeight: 400
    lineHeight: 1.75
rounded:
  keycap: "0.45rem"
  control: "0.5rem"
  button: "0.65rem"
  step: "0.75rem"
  window: "0.9rem"
  panel: "1rem"
spacing:
  gutter: "clamp(1rem, 4vw, 2rem)"
  card: "1.5rem"
  section-sm: "4rem"
  section-lg: "6rem"
components:
  button-primary:
    backgroundColor: "{colors.amber}"
    textColor: "{colors.amber-ink}"
    rounded: "{rounded.button}"
    padding: "0.78rem 1.2rem"
  button-primary-hover:
    backgroundColor: "{colors.amber-deep}"
    textColor: "{colors.amber-ink}"
  button-ghost:
    backgroundColor: "{colors.night}"
    textColor: "{colors.snow}"
    rounded: "{rounded.button}"
    padding: "0.78rem 1.2rem"
  button-ghost-hover:
    backgroundColor: "{colors.night-3}"
    textColor: "{colors.snow}"
  button-on-amber:
    backgroundColor: "{colors.amber-ink}"
    textColor: "{colors.snow}"
    rounded: "{rounded.button}"
    padding: "0.78rem 1.2rem"
  segmented-control:
    backgroundColor: "{colors.night-2}"
    textColor: "{colors.fog}"
    rounded: "{rounded.button}"
    padding: "3px"
  segmented-control-active:
    backgroundColor: "{colors.teal}"
    textColor: "{colors.snow}"
    rounded: "{rounded.control}"
    padding: "0.45rem 0.85rem"
  panel:
    backgroundColor: "{colors.night-2}"
    textColor: "{colors.snow}"
    rounded: "{rounded.panel}"
    padding: "1.5rem"
  keycap:
    backgroundColor: "{colors.night-3}"
    textColor: "{colors.snow}"
    rounded: "{rounded.keycap}"
    padding: "0.4rem 0.55rem"
  step-key:
    backgroundColor: "{colors.teal}"
    textColor: "{colors.snow}"
    rounded: "{rounded.step}"
    padding: "0.85rem 1rem"
  layer:
    backgroundColor: "{colors.night-3}"
    textColor: "{colors.snow}"
    rounded: "{rounded.window}"
    padding: "1.25rem 1.4rem"
  layer-top:
    backgroundColor: "{colors.teal}"
    textColor: "{colors.snow}"
    rounded: "{rounded.window}"
    padding: "1.25rem 1.4rem"
---

# Design System: Wayfarer Wanderer

## Overview

**Creative North Star: "Ridgeline in Night Teal"**

Wayfarer Wanderer's web presence inherits its exact visual language from the desktop IDE's Ridgeline mark (`docs/brand/ridgeline.svg`) and default Wayfarer dark theme. Deep Teal anchors the canvas and elevated chrome; Trail Amber serves as the single high-contrast action colour; the W's asymmetric zigzag is the page's sole geometric ornament.

The design plays the modern developer-tool category canon straight—centered product-first hero, real headless renders of the app window as proof, problem-first feature blocks, architectural stack, install tabs, commit log, FAQ, and a single-goal close—while refusing fabricated social proof, logo walls, neon glows, and decorative motion clutter. Every surface reads at WCAG AA or AAA contrast from frame zero.

**Key Characteristics:**
- Deep Teal-derived dark surfaces (`night`, `night-2`, `night-3`, `teal`) with zero neutral gray slate drift
- Trail Amber (`amber`) paired exclusively with Deep Teal ink (`amber-ink`) on primary buttons and the closing CTA band, matching the desktop app's primary action
- Heavy geometric display headings (Archivo 800) paired with crisp UI copy (Geist) and real-fixture code/keycaps (Geist Mono)
- One authored ornament—the Ridgeline zigzag stroke—and six restrained scroll/load moments instead of generic fade-ins

## Colors

A dark night-teal ground derived from the Ridgeline icon's Deep Teal tile (`#103D40`), punctuated by Trail Amber (`#FFBE50`) for primary actions and restrained semantic language accents for Java and Kotlin.

### Primary
- **Trail Amber** (`colors.amber`): Primary CTA button fills, hero headline emphasis (`<em>`), active tab underlines, focus rings, text selection highlight, ridgeline stroke, key trust-step label, shipping dates, and the full-bleed closing CTA band.
- **Deep Amber** (`colors.amber-deep`): Hover fill for primary Trail Amber buttons, preserving Deep Teal label legibility.
- **Amber Ink** (`colors.amber-ink`): Deep Teal text and icon colour placed on Trail Amber buttons, the closing CTA band, and text selection backgrounds (7.35:1 AAA on Trail Amber).

### Secondary
- **Deep Teal** (`colors.teal`): Branded active-state fill—selected segmented controls, active install tabs, the highlighted `trust?` security step, the top Wayfarer Wanderer architecture layer, and the translucent ridgeline fill silhouette.
- **Java Cyan** (`colors.java`): Language accent naming Java in prose and the architecture stack (9.52:1 AAA on `night-3`).
- **Kotlin Violet** (`colors.kotlin`): Language accent naming Kotlin in prose, the architecture stack, and code keywords (7.17:1 AAA on `night-3`).
- **Code String Green** (`colors.code-string`): String literal syntax token inside real-fixture code panels (10.59:1 AAA on `night-2`).

### Neutral
- **Night Teal** (`colors.night`): Root page canvas and scrollbar track.
- **Night Teal 2** (`colors.night-2`): Elevated panels, bento cards, segmented-control tracks, code frames, IDE window shell, and the alternating "How it works" band.
- **Night Teal 3** (`colors.night-3`): Nested surface fills—download option cards, architecture sub-layers, keycap bodies, inactive test-run pills, ghost-button hover fill, and the viewport-centre highlight tint on normal trust steps.
- **Teal Hairline** (`colors.line`): 1px inset borders, section hairlines, list dividers, and ghost-button outlines.
- **Mist Fog** (`colors.fog`): Secondary body copy, ledes, captions, inactive tabs, navigation links, and metadata (9.28:1 AAA on `night`, 8.67:1 AAA on `night-2`, 5.71:1 AA on `teal`).
- **Crisp Snow** (`colors.snow`): Primary headings, body text, active navigation/tab labels, and keycap legends (17.26:1 AAA on `night`, 10.62:1 AAA on `teal`).

### Named Rules
**The One Action Colour Rule.** Trail Amber is reserved for primary actions, the closing CTA band, active markers, and the Ridgeline stroke. Never introduce a competing CTA hue.

**The Night-Teal Ground Rule.** Every dark neutral carries the teal hue angle (201°–205°). Never drop in pure `#000`, Tailwind `zinc`/`slate`, or neutral gray borders that break the teal field.

**The Text-Always-Readable Rule.** Scroll states may shift background tint or border emphasis, never fade readable text below 4.5:1 contrast.

## Typography

**Display Font:** Archivo (fallback: Geist, ui-sans-serif, sans-serif)
**Body Font:** Geist (fallback: ui-sans-serif, system-ui, sans-serif)
**Mono Font:** Geist Mono (fallback: ui-monospace, monospace)

**Character:** Heavy, tightly tracked Archivo display type gives headlines unmistakable architectural weight, while Geist keeps dense developer explanations effortless to scan and Geist Mono anchors code snippets, CLI commands, keycaps, and commit dates. All three variable font families are self-hosted in `site/static/fonts/` (`font-display: swap`).

### Hierarchy
- **Display** (`800`, `clamp(2.25rem, 4.9vw, 4.15rem)`, line-height `1`, tracking `-0.035em`): Hero `<h1>` only, balanced across lines (`text-wrap: balance`), max measure `17ch`.
- **Headline** (`800`, `clamp(2rem, 3.8vw, 3rem)`, line-height `1.02`, tracking `-0.035em`): Section `<h2>` headings (`sec-title`), balanced, max measure `22ch` on multi-line statements.
- **Title** (`800`, `1.5rem`, line-height `1.2`, tracking `-0.035em`): Bento card `<h3>` titles (`text-2xl`). Layer titles in the architecture stack step to `1.125rem` (`font-bold`).
- **Lede** (`400`, `clamp(1.05rem, 1.5vw, 1.2rem)`, line-height `1.6`): Section introductions and hero subtitle in `fog`, max measure `58ch`–`62ch` (`text-wrap: pretty`).
- **Body** (`400`, `1rem`, line-height `1.5`): Feature copy, trust steps, architecture descriptions, FAQ answers (`max-w-[68ch]`), and shipping entries.
- **Label** (`500`, `0.8125rem`–`0.875rem`): Navigation links, segmented buttons, install tabs, captions, and release notes.
- **Code** (`400`, `clamp(0.76rem, 2.2vw, 0.86rem)`, line-height `1.75`–`1.8`): Real-fixture code snippets, terminal commands, file tabs (`0.78rem`), keycaps (`0.8rem`, weight `500`), and step keys (`0.85rem`).

### Named Rules
**The Real-Mono Rule.** Geist Mono is used strictly for code, CLI commands, filenames, keycaps, dates, and step identifiers—never as a decorative heading or eyebrow font.

**The No-Eyebrow Rule.** Headings carry their own weight without uppercase kickers, pills, or section numbers above them.

## Layout

Content lives inside a centered fluid container (`max-width: 76rem`, horizontal padding `clamp(1rem, 4vw, 2rem)`). Sections follow a deliberate vertical cadence: compact header (`py-5`), tight product-first hero (`pt-8 pb-14`, `sm:pt-12 sm:pb-20`) keeping the real IDE window visible above the fold on both `1440×900` desktop and `390×844` mobile, generous feature separation (`space-y-28 sm:space-y-32`), and full-bleed tonal bands (`#how` in `night-2`, closing CTA in `amber`) to break long-page monotony.

- **Two-column feature pairs:** Stack vertically on mobile/tablet, split `1:1` (`lg:grid-cols-2`, `gap-10`) at `1024px`, alternating text/visual sides for rhythm.
- **Bento grid:** 6-column grid at `768px` (`md:grid-cols-6`, `gap-4`): Row 1 splits `4 + 2` (6-shortcut Keymap card + Tests card); Row 2 splits `3 + 3` (New project + Debug); Row 3 spans full width `6` (Accessibility). On mobile all cards stack in a single column; Keymap shortcuts reflow `2×3` on phones and `3×2` at `640px+` so no orphan item appears.
- **Architecture stack:** Full-width top (`Wayfarer Wanderer`) and bottom (`Your build`) layers sandwiching a `2`-column `Java` / `Kotlin` middle row at `768px+`.
- **Get started & Shipping:** Single-row install tabs on all viewports (`390px` included); 3-column OS download grid at `640px+`; two-column date + description shipping rows (`sm:grid-cols-[8rem_1fr]`).

## Elevation & Depth

Depth relies primarily on tonal stepping (`night` → `night-2` → `night-3` → `teal`) paired with crisp `1px` inset borders (`box-shadow: inset 0 0 0 1px var(--color-line)`). Ambient elevation shadows are strictly neutral dark teal-black—never colored glows—and appear only on floating focal surfaces (the IDE window frame and primary buttons).

### Shadow Vocabulary
- **Inset Border** (`box-shadow: inset 0 0 0 1px var(--color-line)`): Structural edge on panels, ghost buttons, segmented tracks, and architecture layers.
- **Amber Accent Ring** (`box-shadow: inset 0 0 0 1px oklch(0.85 0.14 75 / 0.55)`): Emphasis border on the `trust?` step, top architecture layer (`0.5` alpha), and highlighted `message` jump token (`0.75` alpha).
- **Window Elevation** (`box-shadow: 0 0 0 1px var(--color-line), 0 42px 84px -24px oklch(0.04 0.015 205 / 0.9)`): Deep grounded shadow anchoring the real IDE screenshot frame.
- **Primary Button Lift** (`box-shadow: 0 10px 24px -10px oklch(0.04 0.015 205 / 0.75)`): Subtle neutral elevation beneath Trail Amber CTAs.
- **Keycap Bevel** (`box-shadow: inset 0 -2px 0 oklch(0 0 0 / 0.35), inset 0 0 0 1px var(--color-line)`): Tactile bottom edge on keyboard shortcut caps.

### Named Rules
**The No-Neon-Glow Rule.** Never place colored blurred shadows or radial background halos on dark surfaces. Ground depth with neutral dark shadows and tonal stepping.

## Shapes

Corners follow a calm, nested radius hierarchy so outer containers smoothly cradle inner controls: `1rem` (`panel`), `0.9rem` (`window`, `layer`), `0.75rem` (`step`), `0.65rem` (`button`, `seg`), `0.5rem` (`seg button`), `0.45rem` (`kc`). Focus rings use a `2px solid var(--color-amber)` outline with `3px` offset and `4px` radius.

The page's only geometric ornament is the **Ridgeline zigzag**—extracted directly from the W mark's asymmetric valleys and rising right diagonal. It appears three times with distinct roles:
1. **Hero backdrop:** Sweeps behind the display headline with a translucent Deep Teal fill (`ridge-fill`), bottom gradient mask (`ridge-mask`), and Trail Amber stroke (`stroke-width: 2.5`, `opacity: 0.34`, `vector-effect="non-scaling-stroke"`). Draws on load (`2400ms`) and drifts gently on scroll.
2. **How it works crown:** Sits in a dedicated upper band (`h-24 sm:h-28`) cleanly above the section heading (`pt-32 sm:pt-40`), drawing with scroll progress (`draw-scroll`).
3. **Closing band silhouette:** Subtle `12%` Deep Teal fill along the bottom of the Trail Amber close band.

Browser surfaces are explicitly themed: `color-scheme: dark`, custom `scrollbar-color`, Trail Amber `::selection` with Deep Teal ink (inverted inside the closing amber band), and amber-tinted link underlines (`text-underline-offset: 0.2em`).

## Components

### Buttons
Tactile, crisp-edged actions with `160ms` transitions and subtle vertical press feedback.
- **Shape:** Medium-rounded (`0.65rem`), inline-flex with `0.55rem` icon gap.
- **Primary (`btn-amber`):** Trail Amber fill (`colors.amber`), Deep Teal text (`colors.amber-ink`), `600` weight, `0.78rem 1.2rem` padding, neutral dark elevation shadow. Header compact variant uses `0.5rem 1rem` at `0.875rem`.
- **Hover / Active / Focus:** Shifts to `colors.amber-deep` and lifts `-1px` on hover; depresses `1px` on active; `2px` Trail Amber focus outline (`3px` offset).
- **Ghost (`btn-ghost`):** Transparent fill on dark surfaces, `colors.snow` label, `1px` inset `colors.line` border; fills with `colors.night-3` on hover.
- **Inverted (`close-band`):** On the Trail Amber closing band, the primary CTA uses `colors.amber-ink` fill with `colors.snow` text (`hover:bg-night-3`); the secondary link uses `colors.amber-ink` text with a `1px` `amber-ink/55` ring.

### Segmented Controls
Compact pill-track switches for previewing the IDE's real theme and appearance renders.
- **Style:** `colors.night-2` track (`0.65rem` radius, `3px` padding, `1px` inset `colors.line` border); buttons use `0.5rem` radius, `0.45rem 0.85rem` padding, `0.86rem` size, `520` weight, `colors.fog` text.
- **State:** Selected (`aria-pressed="true"`) switches to `colors.teal` fill, `colors.snow` text, and a `1px` top inner highlight (`oklch(1 0 0 / 0.08)`).

### Panels & Bento Cards
Flat tonal containers that organize features, code demos, and installation flows.
- **Corner Style:** `1rem` radius (`rounded.panel`).
- **Background:** `colors.night-2` with `1px` inset `colors.line` border; nested items step to `colors.night-3`.
- **Internal Padding:** `1.5rem` (`p-6`) on bento cards and tab panels; `1rem` (`p-4`) on the trust-step list.
- **Motion:** Bento cards enter with a staggered vertical scroll rise (`std-rise`, `translateY(32px)` → `none`) keyed by row index `--i`.

### Code Demo & Keycaps
Real fixture code (`tests/fixtures/jvm/maven`) and hardware-inspired shortcut badges.
- **Code Panel:** Interactive file tabs (`role="tab"`, `colors.amber` `2px` bottom border when selected) over scrollable `<pre>` blocks (`Geist Mono`, responsive `clamp(0.76rem, 2.2vw, 0.86rem)` so `GreetingKt.message("Wayfarer")` stays uncut on `390px` phones). Clicking either tab switches immediately and pauses auto-cycling.
- **Keycap (`kc`):** `colors.night-3` fill, `colors.snow` Geist Mono label, `0.45rem` radius, tactile `2px` bottom inset shadow. Inside prose (`p .kc`), caps compress (`min-width: 1.5rem`, `padding: 0.14rem 0.42rem`, `0.78em`) so line rhythm stays even.

### Trust Steps
Ordered four-step workspace-trust flow (`open`, `trust?`, `import`, `work`).
- **Style:** Horizontal row (`0.75rem` radius, `0.85rem 1rem` padding) with fixed `4.2rem` Geist Mono step key and full-opacity body description.
- **Key Step (`is-key`):** Highlighted with `colors.teal` fill, amber inset border, and Trail Amber step key.
- **Motion:** Scroll-driven background-tint highlight (`std-step` / `std-step-key`) as each step crosses viewport centre; text stays at `100%` opacity at all scroll positions.

### Architecture Layers
Three-tier stack explaining Wayfarer Wanderer, Java/Kotlin language servers, and the project's own build wrapper.
- **Style:** `0.9rem` radius, `1.25rem 1.4rem` padding; `colors.teal` with amber border for the top Wayfarer layer, `colors.night-3` with `colors.line` border for language servers and build wrapper.
- **Motion:** Staggered scroll stack entrance (`std-stack`) indexed by `--i`.

### Navigation & FAQ
- **Header:** Single-row flexbox with Ridgeline SVG mark + wordmark on left, 5 centered links on `md:`+ (`Features`, `How it works`, `Get started`, `FAQ`, `GitHub`), and compact Trail Amber `Download` button on right; keyboard skip-to-content link precedes the header.
- **FAQ Accordion:** Native `<details>`/`<summary>` rows divided by `colors.line`; `+` SVG icon rotates `45deg` into Trail Amber when open (`240ms var(--ease-out)`).

### Closing Amber Band
Full-bleed Trail Amber section (`close-band`) that opens horizontally via scroll-driven `clip-path: inset(0 22% round 2rem)` → `inset(0 0 round 0)` (`std-open`), anchored by a subtle Deep Teal ridgeline silhouette at its base.

### Motion Budget
Everything renders visible at load (`opacity: 1` on hero headline, lede, CTAs, and body sections). Only six authored moments animate under `prefers-reduced-motion: no-preference`: hero & section ridgelines (`draw-on-load`, `hero-drift`, `draw-scroll`), hero window tilt (`tilt`), trust-step background tint (`step`), bento card stagger (`rise`), architecture layer stack (`stack`), and closing amber band reveal (`open-band`). Browsers without CSS scroll-driven animations receive an `IntersectionObserver` fallback (`html.io`); `prefers-reduced-motion: reduce` and `?still` disable all motion cleanly.

## Do's and Don'ts

### Do:
- **Do** use Trail Amber (`colors.amber`) with Deep Teal text (`colors.amber-ink`) for primary actions, matching the desktop app's dark primary button.
- **Do** keep the hero visible at `opacity: 1` on initial paint and show the real IDE window render above the fold on both desktop and mobile.
- **Do** animate background tint or border emphasis rather than fading text opacity when highlighting scrolled items, preserving ≥4.5:1 text contrast everywhere.
- **Do** keep the Ridgeline zigzag SVG clearly separated from section headings and use `vector-effect="non-scaling-stroke"` on stretched paths.
- **Do** format code examples so highlighted symbols fit completely inside `390px` mobile viewports without horizontal clipping.
- **Do** use real headless renders from the test suite and honest capability statements (no invented release dates, star counts, or unshipped starters).

### Don't:
- **Don't** add colored neon glow shadows or radial-gradient background washes on dark night-teal surfaces.
- **Don't** add generic fade-ins to headings, slide-ins to list rows, or a fixed reading progress bar; keep motion strictly to the six signature moments.
- **Don't** add logo walls, "built on tools you already trust" strips, or fabricated social proof.
- **Don't** place kickers, eyebrows, or decorative section numbers above headings.
- **Don't** use non-interactive `<span>` elements with `role="tab"`; every ARIA tab must be a real focusable `<button>`.
