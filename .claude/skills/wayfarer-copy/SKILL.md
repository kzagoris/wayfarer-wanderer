---
name: wayfarer-copy
description: Write, edit, or audit natural developer-facing copy for Wayfarer Wanderer (README.md, site/, release notes, and docs). Use when drafting or polishing product descriptions, headings, landing page text, FAQs, or removing stiff/AI-sounding phrasing.
---

# Wayfarer Copy Style Guide

Use this skill whenever writing or revising public-facing text in `README.md`, `site/layouts/home.html`, `site/data/product.yaml`, release notes, or user docs.

## Voice & Audience

- **Audience:** Working Java and Kotlin developers who already know Maven, Gradle, JUnit, and IntelliJ IDEA. Do not explain basics they already know, and do not talk down to them.
- **Tone:** Direct, matter-of-fact, developer-to-developer. Explain what the IDE does and how it behaves instead of pitching it like a SaaS ad.
- **Spelling:** Consistent British English (`colour`, `licence`, `colour-blind`), matching the rest of the repository. Keep literal upstream names intact (e.g., `MIT License` when linking the file if appropriate, CSS `prefers-color-scheme`).

## Patterns to Avoid (Anti-Slop Rules)

1. **No binary contrast slogans:** Never write *"It doesn't X. It Y."* or *"Not just X, but Y."* State directly what the tool does.
2. **No two-sentence punchy ad headings:** Prefer clear descriptive headings (*"Mixed Java and Kotlin projects"*, *"Safe opening with Workspace Trust"*) over dramatic statements (*"Your Java calls Kotlin. Your IDE should follow it there."*, *"Tests that can't lie"*).
3. **No forced triads or rhythmic stunts:** Avoid stacking three dramatic negative clauses (*"no wrapper, no build daemon, no language-server import"*) just for cadence.
4. **No em-dash crutches:** Avoid `—` in body prose when parentheses, a colon, or a second sentence reads cleaner.
5. **No hype or filler vocabulary:** Cut words like *seamless, robust, streamline, elevate, supercharge, leverage, delve, modern, effortless, reimagine*.
6. **No internal dev jargon in user copy:** Translate implementation phrases (*"published only when every file has landed"*, *"ships no language server"*) into plain user-facing language (*"staged first and moved into place only after every file is generated"*, *"does not bundle language servers"*).
7. **No closing slogans:** Let documentation and landing sections end cleanly without cheesy taglines (*"Open your project. Keep your keymap."*).

## Factual Guardrails (from `PRODUCT.md`)

- **Honest about current state:** Do not advertise unfinished features as shipped. Spring Boot project generation and the optional IntelliJ backend (using the user's own JetBrains licence) are planned for future releases.
- **No fabricated proof:** Never invent benchmarks, startup timings, memory numbers, download counts, testimonials, or release dates.
- **Passenger boundary:** Public docs and the website may mention that Wayfarer is built on [Passenger](https://github.com/Zaneris/passenger). The application UI itself must never display the word "Passenger".
- **Preserve technical accuracy:** Never alter CLI commands, environment variable names (`WAYFARER_JDTLS_HOME`, `WAYFARER_KOTLIN_LSP_HOME`), file paths, key bindings, or SDK versions while polishing prose.

## Keeping `README.md` and `site/` in Sync

When updating product copy, keep these sources aligned:
- `README.md` (top showcase half, Features, How it works, Get started, FAQ)
- `site/layouts/home.html` (landing page copy and FAQ)
- `site/data/product.yaml` (version, release note, build prerequisites, language server notes)

## Self-Check Before Finishing

Read the revised text out loud and verify:
- Would a Java/Kotlin developer actually write this sentence in a GitHub README?
- Is every heading immediately scannable without decoding a metaphor?
- Are all commands, shortcuts, anchors, and facts unchanged from the source of truth?
- If `site/` changed, run `hugo` inside `site/` to confirm the build succeeds cleanly.
