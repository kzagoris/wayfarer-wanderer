# Wayfarer Wanderer

The desktop host embeds Passenger's editor. Git records its source revision at
`vendor/passenger`. Install the .NET SDK named in `global.json` and Python 3,
then build from a recursive clone in its own directory.

```sh
git clone --recurse-submodules https://github.com/kzagoris/wayfarer-wanderer.git
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

After switching revisions, run `git submodule update --init --recursive` before restore.
On this workstation, `mise exec dotnet@11.0.100 -- dotnet` selects the pinned SDK.

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

See [JVM backend validation](docs/validation/jvm-backends.md) for the
mixed-language fixture results.

See [release validation](docs/validation/release.md) for packaged application
checks and the required platform evidence.
