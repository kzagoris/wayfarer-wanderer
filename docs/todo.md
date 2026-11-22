# To do

Work left for later, each with what it needs.

## New Project: Spring Boot is unsupported

**Status:** not offered in the New Project dialog (since 2026-11-21).

The dialog lists Java and Kotlin only. `ProjectGenerator` still has the Spring Boot starter (`NewProjectKind.SpringBoot`,
Maven or Gradle, Java or Kotlin) and its tests, but nothing in the UI reaches it.

To bring it back as IntelliJ's Spring Boot generator does (screenshot reference: IntelliJ › New Project › Generators ›
Spring Boot):

- Its own page in the dialog under a **Generators** heading, apart from the plain **New Project** families.
- Fields: Server URL (start.spring.io), Name, Location, Language (Java / Kotlin / Groovy), Type (Gradle - Groovy,
  Gradle - Kotlin, Maven), Group, Artifact, Package name, JDK, Java version, Packaging (Jar / War), Configuration
  (Properties / YAML).
- A **Next** step for choosing the Spring Boot version and dependencies, instead of Create on the first page.
- Decide whether the starter comes from start.spring.io (network, the person's choice of dependencies) or stays the
  bundled template (offline, fixed dependencies).
- Re-add the family in `StarterFamily.All` (`src/WayfarerWanderer.App/NewProjectDialog.cs`) and the dialog tests.

## New Project: closer to IntelliJ

- Build system as segmented buttons (IntelliJ / Maven / Gradle) instead of a drop-down.
- "Create Git repository" and "Add sample code" check boxes.
- A one-click "Download <vendor> <version>" row at the top of the JDK drop-down, as IntelliJ shows for the newest JDK
  (needs a catalogue call when the dialog opens).
- A `<No JDK>` choice.
- An IntelliJ build for Kotlin projects (IntelliJ offers it; Wayfarer's Kotlin family has Gradle and Maven only).

## Status bar: the inspection widget

**Status:** missing (since 2026-11-22). The status bar has the path, the caret, the line separator, the encoding, the
indentation, the language with its server's state and the read-only lock (`EditorWindow.StatusBar.cs`), but not
Passenger's ✓ / IntelliJ's error and warning counts.

It needs the language servers' diagnostics first:

- `JvmLanguageClient` ignores `textDocument/publishDiagnostics`; keep each file's latest list and raise a change.
- Draw them in the editor (squiggles, the error stripe) as Passenger's `CodeEditor` already can.
- A Problems tool window (`ToolWindowIds.Problems` is reserved) listing them per file.
- Then the widget: the shown file's errors and warnings, a click opening Problems, as Passenger's `AnalysisButton`.
