<!-- The top half follows the landing page (site/layouts/home.html, facts in site/data/product.yaml).
     Pictures: docs/readme/, taken from the running app; theme renders: site/static/img/. -->

<p align="center">
  <img src="docs/readme/banner.webp" width="100%" alt="Wayfarer Wanderer. An open IDE for Java and Kotlin. Wayfarer Wanderer keeps IntelliJ IDEA's way of working (the keymap, navigation, run configurations, tests and debugging) on an open language backend. Free and open source. Below the headline, the app paused at a breakpoint in Main.java, showing each variable's value beside its line.">
</p>

<p align="center">
  <a href="#get-started"><b>Get started</b></a> ·
  <a href="#features">Features</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#faq">FAQ</a> ·
  <a href="#development">Development</a>
</p>

<p align="center"><sub>Windows, macOS, and Linux · First release (v0.1.0) coming soon on
<a href="https://github.com/kzagoris/wayfarer-wanderer/releases">GitHub Releases</a>.</sub></p>

## Features

### Mixed Java and Kotlin projects

Java and Kotlin files are handled by their own language servers: Eclipse JDT LS for Java and
`kotlin-lsp` for Kotlin. In Maven projects, Wayfarer compiles Kotlin sources using the project's wrapper
so Java code can resolve Kotlin declarations, letting you jump between languages with
<kbd>Ctrl</kbd>+<kbd>B</kbd>.

<p align="center">
  <img src="docs/readme/java-calls-kotlin.webp" width="760" alt="Main.java calls GreetingKt.message. Quick Documentation shows the Kotlin function as Java sees it: example.GreetingKt, String message(@NotNull String name).">
</p>
<p align="center"><sub>Quick Documentation (<kbd>Ctrl</kbd>+<kbd>Q</kbd>) on a Kotlin function called from Java (using the
<code>tests/fixtures/jvm/maven</code> fixture).</sub></p>

### Safe opening with Workspace Trust

When you open an unfamiliar repository, you can browse and edit files immediately without running
anything from the project. Wayfarer holds off on running Maven or Gradle wrappers, build daemons, and
language-server imports until you explicitly trust the folder. Any unsaved edits stay intact when the
import runs, even if the import fails.

| Step | What happens |
|---|---|
| **Open** | Files open right away with syntax highlighting and editing |
| **Trust** | Nothing in the repository runs until you click **Trust Workspace** |
| **Import** | Your Maven or Gradle wrapper imports modules, packages, and source/test roots |
| **Work** | Build, test, run, and debug as usual |

<p align="center">
  <img src="docs/readme/restricted-mode.webp" width="100%" alt="A freshly opened Maven project in Restricted mode. A banner says build scripts and code in this folder won't run until you trust it, with a Trust Workspace button.">
</p>

### Familiar keymaps

Select **IntelliJ IDEA** in **Settings › Keymap** if you want the standard JetBrains shortcuts. Visual
Studio and VS Code keymaps are included as well.

| Action | Keys | Action | Keys |
|---|---|---|---|
| Search Everywhere | <kbd>Shift</kbd> <kbd>Shift</kbd> | Build | <kbd>Ctrl</kbd>+<kbd>F9</kbd> |
| Go to Declaration | <kbd>Ctrl</kbd>+<kbd>B</kbd> | Run | <kbd>Shift</kbd>+<kbd>F10</kbd> |
| Quick Documentation | <kbd>Ctrl</kbd>+<kbd>Q</kbd> | Debug | <kbd>Shift</kbd>+<kbd>F9</kbd> |
| Rename | <kbd>Shift</kbd>+<kbd>F6</kbd> | Toggle Breakpoint | <kbd>Ctrl</kbd>+<kbd>F8</kbd> |
| Show Intention Actions | <kbd>Alt</kbd>+<kbd>Enter</kbd> | Step Over | <kbd>F8</kbd> |
| Terminal | <kbd>Alt</kbd>+<kbd>F12</kbd> | Step Into | <kbd>F7</kbd> |

<p align="center">
  <img src="docs/readme/keymap.webp" width="560" alt="Settings, Keymap page with IntelliJ IDEA chosen: Build Current Module is Ctrl+F9, Back is Ctrl+Alt+Left, Branches is Ctrl+Shift+backtick.">
</p>

### Debugging with inline values

Set a breakpoint with <kbd>Ctrl</kbd>+<kbd>F8</kbd>, start a debug session with
<kbd>Shift</kbd>+<kbd>F9</kbd>, and step through code using <kbd>F8</kbd> and <kbd>F7</kbd>. When
execution pauses, the Debug tab lists the call stack and local variables while the editor displays
variable values inline next to each line.

<p align="center">
  <img src="docs/readme/debugger.webp" width="100%" alt="Paused at a breakpoint on line 10 of Main.java. Inline values show name = Wanderer and line = Hello, Wanderer. The Debug tab lists the frame main:10 and the locals args, travellers, name and line.">
</p>

### New project wizard

Create Java projects using IntelliJ, Maven, or Gradle, or Kotlin projects using Maven or Gradle. You can
pick a detected JDK, add one from disk, or download one directly from the dialog. New projects are staged
first and moved into place only after every file is generated, so you won't be left with a partial
directory if creation fails.

<p align="center">
  <img src="docs/readme/new-project.webp" width="640" alt="New Project dialog: Kotlin with a Gradle build. The JDK list is open, offering Download JDK…, Add JDK from Disk… and a detected JDK 27.">
</p>

### JUnit test runner

JUnit tests run through your project's Maven or Gradle build. Each run writes to a fresh report directory
so stale results from an earlier pass can't mask a failed build.

### Themes and accessibility

Wayfarer includes three themes in light and dark variants, including a WCAG AAA High Contrast theme.
Optional sound cues announce what the caret lands on, and UI states never rely on colour alone. The
previews below automatically match your GitHub appearance.

<table>
  <tr>
    <td width="33%" align="center">
      <picture>
        <source media="(prefers-color-scheme: light)" srcset="site/static/img/wayfarer-light.webp">
        <img src="site/static/img/wayfarer-dark.webp" alt="The Wayfarer theme: spacious layout in deep blues">
      </picture>
      <br><b>Wayfarer</b><br><sub>Spacious layout in deep blues</sub>
    </td>
    <td width="33%" align="center">
      <picture>
        <source media="(prefers-color-scheme: light)" srcset="site/static/img/cubicle-light.webp">
        <img src="site/static/img/cubicle-dark.webp" alt="The Cubicle theme: compact layout for dense workspaces">
      </picture>
      <br><b>Cubicle</b><br><sub>Compact layout for dense workspaces</sub>
    </td>
    <td width="33%" align="center">
      <picture>
        <source media="(prefers-color-scheme: light)" srcset="site/static/img/high-contrast-light.webp">
        <img src="site/static/img/high-contrast-dark.webp" alt="The High Contrast theme: WCAG AAA contrast for text and borders">
      </picture>
      <br><b>High Contrast</b><br><sub>WCAG AAA contrast for text and borders</sub>
    </td>
  </tr>
</table>

## How it works

Wayfarer focuses on the desktop IDE shell and delegates language intelligence and builds to standard tools:

| Component | Role |
|---|---|
| **Wayfarer Wanderer** | Window chrome, actions, keymaps, run configurations, test runner, and debugger UI. Built with .NET and Avalonia on top of the editor, terminal, Git, and DAP libraries from [Passenger](https://github.com/Zaneris/passenger). |
| **Java** | [Eclipse JDT LS](https://github.com/eclipse-jdtls/eclipse.jdt.ls) for completion, navigation, hover docs, rename, and inlay hints. |
| **Kotlin** | [JetBrains kotlin-lsp](https://github.com/Kotlin/kotlin-lsp/releases) standalone language server. |
| **Build system** | Maven and Gradle invoked through your repository's own wrapper, matching what your team and CI run. |

## Get started

### Download

| Linux | Windows | macOS |
|---|---|---|
| x64 | x64 | Apple Silicon |
| [`WayfarerWanderer-linux-x64.tar.gz`](https://github.com/kzagoris/wayfarer-wanderer/releases/latest/download/WayfarerWanderer-linux-x64.tar.gz) | [`WayfarerWanderer-windows-x64.exe`](https://github.com/kzagoris/wayfarer-wanderer/releases/latest/download/WayfarerWanderer-windows-x64.exe) | [`WayfarerWanderer-macos-arm64.dmg`](https://github.com/kzagoris/wayfarer-wanderer/releases/latest/download/WayfarerWanderer-macos-arm64.dmg) |

Pre-built binaries will be published with v0.1.0 on GitHub Releases. In the meantime, you can build from
source below.

### Build from source

Requires .NET SDK 11.0.100, Python 3, and JDK 21.

```sh
git clone https://github.com/kzagoris/wayfarer-wanderer.git
cd wayfarer-wanderer
dotnet restore WayfarerWanderer.slnx --locked-mode --configfile vendor/passenger/nuget.config
python3 tools/build-jvm-host.py
dotnet run --project src/WayfarerWanderer.App --no-restore
```

### Language servers

Wayfarer does not bundle language servers, so you'll need them installed separately:

- **Java:** Eclipse JDT LS, detected via `WAYFARER_JDTLS_HOME` or VS Code's `redhat.java` extension.
- **Kotlin:** JetBrains' standalone `kotlin-lsp`. Download it from
  [kotlin-lsp releases](https://github.com/Kotlin/kotlin-lsp/releases) and unpack it into
  `~/.local/share/wayfarer/kotlin-lsp/<version>/` (`%LOCALAPPDATA%\wayfarer\kotlin-lsp\<version>\` on
  Windows). Wayfarer picks the newest version in that directory containing `bin/intellij-server`. You can
  override the path with `WAYFARER_KOTLIN_LSP_HOME`, or fall back to an installed VS Code Kotlin
  extension.

## FAQ

<details>
<summary><b>Is it free?</b></summary>

Yes. Wayfarer Wanderer is free and open source under the MIT licence, with no subscription or licence
server required.
</details>

<details>
<summary><b>Is it a fork of IntelliJ IDEA?</b></summary>

No. It follows IntelliJ IDEA's workflow and includes its keymap, but it's a separate .NET and Avalonia
application built on the open-source Passenger editor libraries. Java and Kotlin support comes from Eclipse
JDT LS and JetBrains' `kotlin-lsp`.
</details>

<details>
<summary><b>Which projects can it open?</b></summary>

Maven multi-module builds, Gradle builds (Groovy or Kotlin DSL), and folders whose only project file is
an IntelliJ `.iml`. Mixed Java and Kotlin repositories are supported out of the box.
</details>

<details>
<summary><b>Does it create Spring Boot projects?</b></summary>

Not yet. The New Project wizard currently covers Java and Kotlin applications; Spring Boot templates are
planned for a future release.
</details>

<details>
<summary><b>Can I use JetBrains' own language server?</b></summary>

Not yet. Support for an optional IntelliJ backend (using your own JetBrains licence) is planned for a
future release, while the open-source language servers will remain the default.
</details>

<details>
<summary><b>Which platforms are supported?</b></summary>

Windows (x64), Linux (x64), and macOS (Apple Silicon). Until v0.1.0 is published, you can build from
source on any of these platforms.
</details>

## Development

Wayfarer builds on
[Passenger](https://github.com/Zaneris/passenger)'s editor and libraries, copied
into `vendor/passenger` (see [Passenger copy](#passenger-copy)). The window
itself is Wayfarer's own: the shell pieces it took from Passenger live in
`src/WayfarerWanderer.App/Shell/`, each file headed with the Passenger file it
came from. Wayfarer references only Passenger.Core, Text, Editor, Dap, Terminal
and Vcs, never Passenger.App. Install the .NET SDK named in `global.json` and
Python 3, then build from a clone in its own directory.

```sh
git clone https://github.com/kzagoris/wayfarer-wanderer.git
cd wayfarer-wanderer
dotnet --version
dotnet restore WayfarerWanderer.slnx --locked-mode --configfile vendor/passenger/nuget.config
dotnet build WayfarerWanderer.slnx -c Debug --no-restore
rm -f artifacts/test-results/editorhost.trx
dotnet test tests/WayfarerWanderer.Tests -c Debug --no-build --filter FullyQualifiedName~EditorHostTests \
  --logger 'trx;LogFileName=editorhost.trx' --results-directory artifacts/test-results
python3 tools/check-trx.py artifacts/test-results/editorhost.trx \
  WayfarerWanderer.Tests.EditorHostTests.TypingThenUndoRestoresTextAndCaret \
  WayfarerWanderer.Tests.EditorHostTests.PasteShortcutUsesWindowClipboard
```

### Passenger copy

`vendor/passenger` is an ordinary folder of Wayfarer's history, not a
submodule. It keeps only the parts of
[Passenger](https://github.com/Zaneris/passenger) that Wayfarer's build uses:
the Core, Text, Editor, Dap, Terminal, Vcs and Analyzers projects, the four
theme files Editor reads from Passenger.App, the TextMate grammars Wayfarer
embeds, and the licences and root build files.
[`vendor/passenger.json`](vendor/passenger.json) names the upstream commit it
was copied from and the paths it keeps. Nothing is pushed back to Passenger.
Wayfarer's own changes to the copy are edited and committed in place.

```sh
python3 tools/import-passenger.py status        # Wayfarer's changes against upstream
python3 tools/import-passenger.py diff [PATH]   # the same as a diff
python3 tools/import-passenger.py update        # bring in upstream main (or: update v0.5.0)
```

`update` fetches upstream into `artifacts/passenger-upstream.git` and copies
upstream's changes to kept paths. Files Wayfarer never changed are taken as they
are. Files Wayfarer changed are three-way merged, and any conflict is left with
markers and listed. Afterwards run
`dotnet restore WayfarerWanderer.slnx --force-evaluate --configfile vendor/passenger/nuget.config`
to refresh the lock files, then build, test and commit. After editing the keep
rules, `prune` deletes what they no longer keep and `fill` copies in what they
now keep, such as a Passenger project Wayfarer starts to reference.

#### Shell copies

`update` never touches the shell files Wayfarer adapted by hand from
Passenger.App (`src/WayfarerWanderer.App/Shell/` and a few files beside it).
Each starts with a stamp naming the upstream commit and the file(s) it was
copied from, such as
`// From Passenger 2044e49b src/Passenger.App/ActionButtons.cs, MIT ...`.
`copies` lists the files whose originals upstream has changed since their
stamp. By default it compares them with the copy's recorded commit, so it shows
what a finished `update` left behind. It reads the mirror as it is, and
`--fetch` refreshes the mirror first.

```sh
python3 tools/import-passenger.py copies [--to v0.5.0]          # files behind, largest change first
python3 tools/import-passenger.py copies --diff FILE [--to REF]  # upstream's change to FILE's originals
python3 tools/import-passenger.py copies --restamp FILE [--to REF]
```

To port a file, take the largest one in the list and read its `--diff`, which
shows upstream's change from the file's stamp to the target. Carry over what
applies to Wayfarer, build and test, then run `--restamp` with the same `--to`.
That moves the file's stamp to the target, leaving the rest of the header and
the line endings as they were, and drops the file from the list. When upstream
renamed or deleted an origin, the listing says so, and `--restamp` reminds you
to correct the stamp's path by hand. `--json` prints the listing as JSON.

#### No "Passenger" in the window

Wayfarer never shows the word "Passenger". `NoPassengerInUiTests` reads every
string literal in Wayfarer's projects and in the vendored libraries they
reference, and fails on any that says it. Comments, namespaces, provenance
headers and resource keys such as `Passenger.Panel` don't count. The few
literals that name Passenger without showing it, such as the default
`Product.Name` and the look ID that old settings kept, are listed in the test
with a reason. The test also fails when one of those listed literals is gone.
`WelcomeTests` render the Welcome screen, the first start and Settings
headlessly and check that they say "Wayfarer Wanderer".

After an `update`, run the tests. Fix any upstream text the scan lists with
`Product.Name` (which `EditorApplication.Identify` sets to Wayfarer Wanderer) or
neutral words, editing `vendor/passenger` in place. Add a literal to the test's
list only when no one sees it.

### Editor themes and publish check

Run `dotnet test tests/WayfarerWanderer.Tests -c Debug --no-build` after the
locked Debug build. The two Skia renders are written to `artifacts/test-results`
or to the directory given by `WAYFARER_TEST_OUTPUT`. The tests compare the
editor's resolved brushes, including its syntax, diff, VCS and test colours,
and syntax style strings with the pinned Tokens dictionary. They also resolve
the editor text padding, popup radius and the Error, Problems and Check icons.
The host links the two dictionaries and Passenger's Skia font fallback source.
Passenger's control themes and shell styles are inputs to its key generator.

For a self-contained desktop publish on Linux x64, select the singular MSBuild
property at restore time. The .NET 11 restore CLI's `-r` switch also overrides
the list of RIDs that NuGet compares with the eight-RID lockfiles.

```sh
dotnet restore src/WayfarerWanderer.App -p:RuntimeIdentifier=linux-x64 -p:SelfContained=true \
  -p:Configuration=Release --locked-mode --configfile vendor/passenger/nuget.config
dotnet publish src/WayfarerWanderer.App -c Release -r linux-x64 --self-contained --no-restore
python3 tools/check-publish.py artifacts/publish/WayfarerWanderer.App/release_linux-x64 \
  artifacts/test-results/publish.json
```

The inventory includes the file tree and the dependency manifest's libraries
and runtime assets. Passenger's build-time analyzers generate the editor keys.
Passenger.Core uses Microsoft.Diagnostics.NETCore.Client for process diagnostics.

### JVM workflows and release validation

The JVM session imports a trusted Maven or Gradle workspace before it exposes
build, test and launch targets. Builds and tests use the workspace wrapper, and
each test invocation owns a fresh report directory so a failed build cannot
inherit older successful reports. Window close cancels the session's operations.

A trusted folder whose only build file is an IntelliJ `.iml` is compiled with
`javac` into `out/production/<module>` and launched with `java`. Maven and
Gradle projects are unchanged. IntelliJ GUI Designer forms are not run.

The `--acceptance <case-file> --results <directory>` application mode runs the
normal host through Avalonia's headless Skia platform and writes a PNG plus
structured observations. Trust remains disabled unless a case explicitly
exercises and accepts the prompt. Run the complete selector matrix with:

```sh
python3 tools/acceptance.py --results artifacts/test-results/acceptance
```

New Project generates application starters for Java (IntelliJ, Maven or Gradle)
or Kotlin (Maven or Gradle). Its JDK drop-down lists the JDKs Wayfarer keeps and
detects, with Download JDK… and Add JDK from Disk…; the module name or
coordinates and the package sit under Advanced Settings. Spring Boot is not
offered for now; see [docs/todo.md](docs/todo.md). Projects are staged beside the destination and published only
after every file has landed. The generated wrapper, source, test, launch
configuration and notices form one complete project.

To debug, open a workspace from **File > Open Workspace**, trust it, and open a
Java or Kotlin source file. Save edits before launching. Press **F9** on an
executable line and choose **Run > Debug**. The Debug tab shows the paused
stack and the top frame's locals. Use the Run menu to step into, over or out,
continue, or stop. Set `JAVA_HOME` to a full JDK compatible with the build.

Wayfarer does not bundle language servers: see [Language servers](#language-servers)
for where it finds JDT LS and `kotlin-lsp`.

See [JVM backend validation](docs/validation/jvm-backends.md) for the
mixed-language fixture results.

See [release validation](docs/validation/release.md) for packaged application
checks and the required platform evidence.

## Licence

Wayfarer Wanderer is under the [MIT License](LICENSE). The Passenger copy in
`vendor/passenger` and the files adapted from it keep Passenger's own MIT
licence ([`vendor/passenger/LICENSE`](vendor/passenger/LICENSE)), and the
third-party parts they carry are listed in
[`vendor/passenger/THIRD_PARTY_NOTICES.md`](vendor/passenger/THIRD_PARTY_NOTICES.md).
