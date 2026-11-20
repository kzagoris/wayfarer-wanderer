# Wayfarer Wanderer

The desktop host embeds Passenger's editor and shell. Passenger's source is
copied into `vendor/passenger` (see [Passenger copy](#passenger-copy)). Install
the .NET SDK named in `global.json` and Python 3, then build from a clone in its
own directory.

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

On this workstation, `mise exec dotnet@11.0.100 -- dotnet` selects the pinned SDK.

## Passenger copy

`vendor/passenger` is an ordinary folder of Wayfarer's history, not a
submodule. It keeps only the parts of
[Passenger](https://github.com/Zaneris/passenger) that Wayfarer's build uses.
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
rules, `prune` deletes what they no longer keep.

## Editor themes and publish check

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

## JVM workflows and release validation

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

New Project generates application or Spring Boot starters for Java or Kotlin on
Maven or Gradle. Projects are staged beside the destination and published only
after every file has landed. The generated wrapper, source, test, launch
configuration and notices form one complete project.

To debug, open a workspace from **File > Open Workspace**, trust it, and open a
Java or Kotlin source file. Save edits before launching. Press **F9** on an
executable line and choose **Run > Debug**. The Debug tab shows the paused
stack and the top frame's locals. Use the Run menu to step into, over or out,
continue, or stop. Set `JAVA_HOME` to a full JDK compatible with the build.

Wayfarer ships no language server. For Kotlin, download the standalone build
from [kotlin-lsp releases](https://github.com/Kotlin/kotlin-lsp/releases). Unpack it into
`~/.local/share/wayfarer/kotlin-lsp/<version>/` (`%LOCALAPPDATA%\wayfarer\kotlin-lsp\<version>\`
on Windows). Wayfarer uses the highest version there that contains
`bin/intellij-server`. `WAYFARER_KOTLIN_LSP_HOME` overrides that folder, and an
installed VS Code Kotlin extension is the last resort. For Java, Wayfarer uses
`WAYFARER_JDTLS_HOME` or the VS Code `redhat.java` extension.

See [JVM backend validation](docs/validation/jvm-backends.md) for the
mixed-language fixture results.

See [release validation](docs/validation/release.md) for packaged application
checks and the required platform evidence.
