# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Hugo (static site), Tailwind CSS v4 through Hugo's `css.TailwindCSS`, Alpine.js for small interactions. The site lives in
`site/` in this repository. Hosting is undecided, so the output must stay a plain static folder (`hugo` → `site/public`).

## Users

Working Java and Kotlin developers who like IntelliJ IDEA's daily workflow (keyboard-driven editing, navigation, run
configurations, debugging) and want an open, free desktop IDE without a subscription. They work on Maven and Gradle
applications, often mixing Java and Kotlin in one repository, on Windows, macOS or Linux.

## Product Purpose

Wayfarer Wanderer is a standalone desktop IDE for Java and Kotlin/JVM projects. It opens existing Maven and Gradle
repositories, creates new Java or Kotlin projects, and runs the edit, test and debug loop from one window. Success is a
developer who opens their real project, trusts it, and keeps working in it day to day.

## Positioning

IntelliJ IDEA's workflow as the reference, on an open language backend by default: Eclipse JDT LS for Java and the
standalone JetBrains Kotlin language server for Kotlin, with no subscription. Java and Kotlin in the same project work
together (on Maven, Kotlin is compiled with the wrapper and attached to JDT LS so Java sees Kotlin declarations). It is a
.NET/Avalonia application built on the Passenger editor and libraries.

## Operating Context

- Open a workspace (File › Open Workspace), trust it; until trusted nothing in it is executed (no wrapper, daemon or
  language-server import).
- Import Maven reactors and Gradle builds (Groovy or Kotlin DSL) through the repository wrapper; modules, packages,
  production and test sources appear in the Project view.
- New Project: Java (IntelliJ, Maven or Gradle build) or Kotlin (Maven or Gradle), with a JDK drop-down that lists kept
  and detected JDKs, Download JDK… and Add JDK from Disk…. Projects are staged and published only when complete.
- Build, run named launch configurations, run JUnit tests through the build, debug with breakpoints (F9), step,
  stack and locals.
- Editor: completion, diagnostics, hover cards, parameter and type name hints, Quick Documentation, rename.
- Themes: Wayfarer, Cubicle, High Contrast, each dark and light. Version control, terminal, Build window.
- Kotlin needs the kotlin-lsp standalone build unpacked locally; Java uses JDT LS (`WAYFARER_JDTLS_HOME`) or VS Code's
  `redhat.java` extension. Wayfarer ships no language server today.

## Capabilities and Constraints

- Version 0.1.0. No public release has shipped yet; the release pipeline (Windows x64, Linux x64, macOS arm64 via GitHub
  Releases) is planned. Download buttons point to GitHub Releases as placeholders until then.
- Spring Boot generation is not offered in the New Project dialog for now (on the to-do list); do not advertise it as
  available.
- IntelliJ backend (JetBrains' "Java and Kotlin by IntelliJ IDEA" language server with the user's own licence) is a
  planned later release, not available now.
- Undecided: licence wording for Wayfarer itself, supported JDK/Gradle/Maven version ranges, performance figures.

## Brand Commitments

- Name: Wayfarer Wanderer (short: Wayfarer).
- App icon: Ridgeline in Trail Amber (`docs/brand/ridgeline.svg`) — a solid Trail Amber W (`#FFBE50`) on a Deep Teal
  rounded-square tile (`#103D40`) with transparent corners, asymmetric valleys and a rising diagonal right tip.
  Generated into `src/WayfarerWanderer.App/Shell/Assets/wayfarer.{png,ico}` and `site/static/{favicon.png,img/icon.png}`
  by `tools/app-icon.py`.
- The page may say it is built on Passenger (https://github.com/Zaneris/passenger). The app's own UI never shows the
  word "Passenger".
- Public claims confirmed by the owner: open source and free; mixed Java + Kotlin projects; Passenger origin.

## Evidence on Hand

- Real headless renders of the app window in each theme: `src/WayfarerWanderer.App/Shell/Assets/FirstStart/*.webp`
  (Wayfarer, Cubicle, High Contrast; dark and light), showing a Maven multi-module fixture with Java calling Kotlin
  (`GreetingKt.message`).
- Editor renders: `docs/validation/milestone-1/product/editor-{dark,light}.png`.
- Source: https://github.com/kzagoris/wayfarer-wanderer.
- Absent and never to be fabricated: users, testimonials, customer logos, download counts, GitHub star counts,
  benchmarks (startup time, memory), release dates.

## Product Principles

1. IntelliJ's workflow is the reference for how daily work should feel; copy the behaviour developers already know.
2. Open by default: the free backend is the default, a subscription is never required.
3. Nothing in an untrusted repository runs until the developer trusts it.
4. Unsaved work survives everything: imports, failed builds, crashed language servers.
5. Honest about state: say what is not done yet rather than implying it.
