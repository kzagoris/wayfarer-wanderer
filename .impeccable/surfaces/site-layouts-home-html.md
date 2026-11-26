---
version: 1
slug: "site-layouts-home-html"
primary_target: "site/layouts/home.html"
related_targets: ["site/assets/css/standard.css","site/assets/js/standard.js"]
---

## Surface

Landing page (`site/layouts/home.html`, served at `/`). Mode: Persuade. Visitor: a Java/Kotlin developer who likes
IntelliJ IDEA's workflow and is deciding whether to try a free, open IDE. Action: Download for their OS (GitHub
Releases, first release pending), secondary View on GitHub / build from source. Proof: the real window rendered by the
test suite in every theme, the real fixture code, what it is built on, the commit log. Constraints: no invented users,
stars, benchmarks or quotes; Spring Boot and the IntelliJ backend are stated as not in this version.

## Direction contract

THESIS: The category standard played straight (Zed, IntelliJ IDEA, Linear, Cursor): centered product-first hero, two
specific actions, the real window as proof, problem-first features, how it works, get started, shipping log, FAQ, one-goal
close. It refuses logo walls and invented social proof.

OWN-WORLD: Night-teal ground derived from Deep Teal #103D40 (darker teal-black page, teal panels), Trail Amber #FFBE50 as
the one action colour (amber fill, deep-teal label, exactly as the app's dark primary action), the Ridgeline W's zigzag as
the only ornament: a ridgeline stroke that draws itself. Archivo heavy display, Geist text, Geist Mono code. Java in
cool teal-cyan, Kotlin in violet.

STORY: The visitor sees it is the IntelliJ way of working without a subscription, sees the real window in its own
colours, understands it drives existing tools (wrapper, JDT LS, kotlin-lsp, Passenger), learns how to start, and
downloads.

FIRST VIEWPORT: Header with the Ridgeline mark, wordmark, links and an amber Download. Centered display headline
(max 17ch), lede, amber "Download for <OS>" + ghost "View on GitHub", release note line; below, theme/appearance
switchers and the real window tilting toward flat, its top edge visible above the fold. A faint ridgeline drawn across
the hero behind the headline.

FORM: Category canon, chosen by the user from four built directions (canon card). seed: 55ef4b94b75bc873

Signature interaction and motion grammar: the ridgeline draws on load; scrolling flattens the window, steps light up in
order as they cross the viewport centre, bento cards rise in a stagger, the how-it-works ridgeline draws with scroll and
the three layers stack, the close's amber band opens from the centre. Reduced motion and unsupported browsers show
everything static.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
