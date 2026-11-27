# To do

Work left for later, each with what it needs.

## Avalonia is held at 12.1.6

**Status:** pinned (since 2026-11-27), while the Passenger copy is at v0.4.6, which moved to Avalonia 12.2.0.

On 12.2.0 Wayfarer's test host died with an access violation (0xC0000005) in HarfBuzz's `hb_shape_full`, reached from
`CodeEditor.Format` → `FormatWhole` → `PrepareFrame` in a layout pass (`OnSizeChanged`). It crashed in 3 of 5 full runs
of `dotnet test tests/WayfarerWanderer.Tests` on Windows x64, and in none of 5 on 12.1.6, two of those on the v0.4.2 copy
and three on v0.4.6's libraries. Upstream's own change to the editor's line shaping (Passenger `43e51aa4`) is not the
cause: the suite still crashed with it undone. The HarfBuzzSharp and SkiaSharp natives are the same on both. The crash
sits in the editor's normal layout path, so the app can likely hit it too.

What the pin costs: Passenger 0.4.4's AltGr fix (#2209) reads `KeyEventArgs.PlatformKeyInfo`, which 12.1.6 lacks, so
`Keymap.HeldAsCtrlAlt` in `vendor/passenger/src/Passenger.Editor/Keymap.cs` answers false. On Windows a Ctrl+Alt
shortcut on a key that types a character with AltGr (Ctrl+Alt+L on a Polish layout) still types, as before.

To lift it:

- Watch for an Avalonia 12.2.x release whose notes fix text shaping or font lifetimes (12.2.0's #22662, #22774 and
  #22659 touched them).
- Set every `Avalonia*` `12.1.6` back to the new version in `vendor/passenger/Directory.Packages.props` and drop the
  Wayfarer comment there, restore with `--force-evaluate`, and put upstream's `HeldAsCtrlAlt` test back (its text is in
  the method's remarks).
- Run the full suite at least five times; a single clean run proves little.
- If it still crashes, capture a dump (`dotnet test --blame-crash`) and read it with the `libHarfBuzzSharp.pdb` the
  package ships, then report it to Avalonia.

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
