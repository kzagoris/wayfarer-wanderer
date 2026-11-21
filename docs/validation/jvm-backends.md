# JVM backend validation

## Fixture baseline

The fixtures run independently under `tests/fixtures/jvm`. Both wrappers append their build tool name to the file selected by `WAYFARER_WRAPPER_MARKER` when that environment variable is set. Each rename manifest uses one-based line and column coordinates.

| Fixture | Build JVM | Independent check |
| --- | --- | --- |
| Gradle 9.7.1, Kotlin 2.4.20 | Temurin 26.0.2 | `./gradlew :service:classes :app:run` compiled both languages and printed `Hello, Wayfarer`. The default JDK 27 failed while evaluating the Groovy script with unsupported class major version 71. |
| Maven 3.9.11, Kotlin 2.2.20 | Temurin 26.0.2 | The Java main calls a Kotlin top-level function. All four reactor projects passed `./mvnw clean package`. |

## Debug information and restart

After `./gradlew :app:installDist :service:installDist`, JDK 26 jdb launched `example.MainKt` with `app/build/install/app/lib/*`. It bound the breakpoint at Main.kt line 4. A single step entered Greeting.java line 5. The stack contained both source locations and the method argument was `name = "Wayfarer"`. Continuing printed the greeting and exited.

A directly launched Gradle service returned HTTP 200 and `Hello, Wayfarer` from `/hello?name=Wayfarer`. After terminating that child and waiting for exit, a fresh JVM served the same response on the same loopback port.

## Release decision

The release now uses the selected wrapper for every build, test and launch. All
eight generated starters were built with their own wrapper, their JUnit tests
ran, command-line applications printed the expected greeting, and both Boot
flavours on both build tools served their handler and stopped without an
orphaned application child. Maven 3.9.11 and Gradle 9.7.1 are the recorded
wrapper versions. Every generated Gradle build uses Kotlin DSL; imported
Groovy and Kotlin settings are both recognized.

## Interactive debugger

The editor now uses Passenger's DAP client and the Java Debug Interface (JDI)
from the tooling JDK. The repository ships the adapter as Java source. A
trusted launch builds the selected module with its project wrapper and
resolves its runtime classpath. Wayfarer starts the adapter, which listens on
an ephemeral loopback port. It then starts the configured target JVM suspended,
with `server=n`, so the target connects out to the adapter. The adapter
installs source breakpoints and resumes the target.
Closing the session stops both owned processes.

On Windows x64 with Temurin 24.0.2 the following cases ran through the adapter.

| Fixture | Observation |
| --- | --- |
| Gradle application | F9 in the headless editor marked Main.kt line 4. Debug stopped there. Step Into entered Greeting.java line 5, Locals showed `name = "Wayfarer"`, and Step Out returned to Kotlin. Continue printed the greeting. |
| Maven application | The Java main called Kotlin. A breakpoint in Greeting.kt line 3 exposed `name = "Wayfarer"`; Step Into reached Prefix.java line 5. Continue printed the greeting. |
| Gradle and Maven Boot services | A breakpoint set after startup stopped each HTTP request at Service.java line 18. Step Into reached Greeting.java on Gradle and Greeting.kt on Maven. Both returned HTTP 200 with `Hello, Wayfarer`, stopped and served the same request after restarting on the same port. |

## Launch fidelity and process ownership (task 6)

`JvmProcessOwnershipTests` runs under `WAYFARER_DEBUG_ACCEPTANCE=1`. On
2026-11-19, on Windows x64 (Intel i7-8700K, 12 threads, 32 GB, Temurin 24.0.2
for the tooling JVM and every module, Gradle 9.7.1, Maven 3.9.11), all eleven
debug acceptance cases passed. These are the Kotlin-to-Java editor stepping, both Boot handlers,
five ownership cases and two launch comparisons.

**Launch.** A direct launch builds with the project wrapper, takes the module's
runtime classpath from the wrapper, and starts `java -cp … main` in the module
directory. On Gradle it printed the same `Hello, Wayfarer` as `:app:run`, with
the same classpath source (`sourceSets.main.runtimeClasspath`) and working
directory. On the Maven reactor, the build task cannot launch a module. With
`-pl app -am exec:java`, Maven 3 runs the goal on the aggregator first and fails
with `ClassNotFoundException: example.Main`. Without `-am` the sibling library
does not resolve. Run therefore launches a Maven reactor module directly unless
the launch configuration names a build task. The debugger uses the same
resolved command, so Run and Debug start the same process. Direct launch is the
selected mode. The IDE owns exactly that application child, and no build tool
sits between them.

**Ownership.** Each case started an unrelated JVM, debugged the Boot service on
an ephemeral port, sent HTTP and then ended the session. It ended by Stop on
Gradle, by closing the window on Gradle and Maven, or by switching the window to
another folder on Gradle and Maven. In every case the service and adapter JVMs
exited and the port refused connections. The unrelated JVM and every Gradle
daemon survived. A new session then bound the same port and served
`Hello, Wayfarer`.

**Debug adapter.** The adapter is Wayfarer's own `WayfarerDebugAdapter.java`,
run from source on the tooling JDK's `jdk.jdi` module. Wayfarer ships the
source and redistributes no adapter binary. JDI is part of the user's JDK
(GPLv2 with the Classpath Exception). It needs no JDT LS plug-in, so the
debugger does not depend on the semantic backend.

**JDWP exposure.** JDWP has no authentication, so the target never listens.
The adapter listens on an ephemeral loopback port, accepts one connection and
then stops listening. A local process that connects first gets no control over
the user's program. The real target then finds the port closed and exits
before its `main` runs, so the session fails rather than being hijacked.
`JvmDebugAdapterTests` covers a second JVM being refused and a connected target
staying suspended until `configurationDone`. Wayfarer does not check which JVM
connected. A check would need a method call inside the target before `main`,
which depends on JDK launcher internals.

## Measurements

`JvmMeasurementTests` runs under `WAYFARER_MEASURE=1` and writes
`artifacts/test-results/jvm-measurements-<tool>.json`. It used a fresh project copy, with
the user's Gradle and Maven caches and the Gradle daemon left warm. Both
language servers run together, so the memory peak covers the pair.

Recorded 2026-11-19 on an Intel Core i5-6600 (4 cores) with 39 GiB of RAM,
Omarchy (Arch Linux), linux-x64. The tools ran on OpenJDK 27 (2026-09-15) from
`PATH`, with `JAVA_HOME` unset. The servers were JDT LS 1.49.0 (redhat.java
1.44.0, the only installed Java extension) and the JetBrains Kotlin server
2027.1 EAP build 271.2085.0, found under
`~/.local/share/wayfarer/kotlin-lsp/271.2085.0/`. The static import reads only
build files, so the snapshot's JDK fields stay empty and the record carries an
`environmentJdk` field instead.

| | Gradle | Maven |
| --- | --- | --- |
| Cold import (static build-file import, no wrapper; superseded, see "Project host" below) | 32 ms | 5 ms |
| Warm reopen (static reader; superseded) | 0 ms | 0 ms |
| Kotlin classes handed to JDT LS (wrapper compile and attach) | not needed | 13.1 s |
| Java completion of a Kotlin member, first useful result | 4.4 s | 0.4 s after the hand-over |
| Java completion, warm median of 5 | 27 ms | 13 ms |
| Kotlin completion of a Java member, first useful result | 8.2 s | 9.4 s |
| Kotlin completion, warm median of 5 | 258 ms | 235 ms |
| Peak working set, both language servers | 2,234 MiB | 2,655 MiB |

Earlier runs the same day, before the Maven hand-over existed, gave similar
Gradle and Kotlin figures and memory peaks between 2,307 and 2,860 MiB.

## Project host (task 4)

The project model now comes from the JVM project host under `jvm/project-host/`,
which asks Gradle (Tooling API with Wayfarer's own model builder) and Maven
(the wrapper's Maven in an isolated class loader) instead of reading build files.

### Protocol examples

One JSON object per line on stdio, logs on stderr. The examples under
`jvm/project-host/protocol/` are checked by both ends (`ProtocolExamplesTest` in
the host, `ProjectHostTests` in .NET):

| File | What it shows |
| --- | --- |
| `ready.json` | The first line: protocol version, the tooling JVM, the host's pid |
| `refused.json` | A first line saying why the host cannot start |
| `request-import.json` | `import` with folder, generation, JDK and Maven profiles |
| `partial-target.json` | One module as a BSP-shaped build target (sources, dependency modules, JVM run environment) |
| `result-import.json` | The complete result: every target, the import JVM and the build inputs |
| `request-cancel.json`, `error-cancelled.json` | `cancel` by id and the reply it ends with |
| `request-ping.json`, `result-ping.json` | The liveness ping, answered on the reader thread |

### Snapshot against the language servers

`LanguageServerModelTests` (`WAYFARER_LANGUAGE_ACCEPTANCE=1`) imports the mixed
fixtures, starts a server on the snapshot, adds
`org.apache.commons:commons-text:1.12.0` to the Kotlin module (Maven `library`,
Gradle `app`), reimports, tells the server through
`JvmLanguageClient.ReloadModelAsync`, compares, removes the dependency, and
compares again. The Java module `service` sees the library through the Kotlin
module. JDT LS is compared on its own model: `java.project.getClasspaths`
(runtime and test scope), `java.project.listSourcePaths` and the compiler
compliance from `java.project.getSettings`.

| Compared | Maven, JDT LS 1.53.0 (m2e) | Gradle, JDT LS 1.53.0 (Buildship) |
| --- | --- | --- |
| Library jars, before, added, removed | Same set as the snapshot's runtime classpath | Same set as the snapshot's compile ∪ runtime classpath |
| Order of library jars | Same | Two jars swap places (`spring-boot-webmvc`, `spring-boot-http-converter`); not asserted |
| Compile-only jars | None in the fixture | JDT LS keeps one classpath per project, so Gradle's compile-only API jars (bnd, error_prone, OSGi annotations) appear there; the snapshot keeps them on the compile classpath only, as Gradle does |
| Modules on the classpath | `library` | `app`, `library` |
| Module outputs | `target/classes`, the same folders | Buildship's own `bin/main`; the snapshot has Gradle's `build/classes/...` (by design, so a launch runs what Gradle built) |
| Existing source roots | Same | Same |
| Language level | 17, both | 24, both, once JDT LS is given the import JDK (below) |
| Generated roots | JDT LS lists `target/generated-sources/annotations` before it exists; compared only once on disk | None |
| Kotlin server 271.2085.0, `WordUtils` from the added library | Not resolved before, resolved after the reload, gone after the removal | Same |

Before this change JDT LS reported Java 21 on Gradle while the snapshot said 24:
Buildship ran Gradle on JDT LS's bundled JRE 21, and a build without a toolchain
takes its language level from the daemon's JVM. `BuildLanguageSettings` now passes
`java.import.gradle.java.home` (the import JDK) and `java.configuration.runtimes`
(each module JDK whose `release` file matches its level) in the initialization
options and in `workspace/configuration`. After a reimport the editor sends the
new settings, the changed build files as watched-file changes and, to JDT LS,
`java/projectConfigurationsUpdate`. A failed import sends nothing, so both
servers stay on the last good model.

The Kotlin server has no model query, so it is compared by what it resolves: a
completion after `org.apache.commons.text.` in a Kotlin file of the module that
gets the dependency. With the library in the build from the start it resolves
about 15 s after start. It registers no file watchers and ignores
watched-file notifications, so the first run never saw the added library. It
imports again on the request `intellij/reloadWorkspace` (with the
initialization options), which is what JetBrains' own VS Code extension sends
when a build file is saved; `ReloadModelAsync` now sends it, and the request
returns once the new workspace is loaded. The JetBrains Kotlin server build that
came with the VS Code extension 0.0.12 no longer starts (it asks for a newer
build); these runs used the standalone 271.2085.0 archive unpacked under
`%LOCALAPPDATA%\wayfarer\kotlin-lsp\271.2085.0`, its SHA-256 checked against the
published one.

### Failure, repair and switch

`WorkspaceImportTests` covers these against the real host:

- `AFailedImportKeepsTheLastGoodSnapshotStaleAndARepairRestoresIt`: a broken
  `pom.xml` fails the generation with the file named; the last good snapshot
  stays current and stale; the repaired file brings a current one back.
- `ASwitchWithAReplyInFlightDropsTheOldWorkspacesModel`: switching from a Maven to
  a Gradle workspace while the Maven reply is pending; the old reply is dropped.
- `AModuleWhoseDependencyDoesNotResolveCarriesItsProblem`: one module's problem,
  the rest current.

`EditorWindowTests.AStaleModelTurnsRenameOffKeepsEditsAndARepairTurnsItBackOn`
drives the window: the build-file watcher reimports after the break, the kept
model shows stale, Rename Symbol turns off (it edits files from the model) while
Complete Code stays, the unsaved edit survives, and the repair turns Rename back on.

### Measurements

`JvmMeasurementTests.MeasureHostImportAndMemory` (`WAYFARER_MEASURE=1`) writes
`artifacts/test-results/jvm-host-measurements-<tool>.json`. Recorded 2026-11-20
on an Intel Core i7-8700K (6 cores, 12 threads) with 32 GiB of RAM, Windows 11
(10.0.26300), win-x64. The host and the imports ran on Temurin 24.0.2. The user's
Gradle caches, Maven repository and wrapper distributions were warm. For the
cold daemon the Gradle fixture got a JVM argument no running daemon had, so
Gradle started its own daemon without stopping anyone else's. Four runs agreed
within 3%; the table gives the last.

| | Gradle (Gradle 9.7.1) | Maven (Maven 3.9.11) |
| --- | --- | --- |
| Host start, to its first line | 204 ms | 147 ms |
| Cold import, cold daemon (Maven: first import in the host) | 9.1 s | 2.0 s |
| Cold import, warm daemon, new host | 2.3 s | 2.1 s |
| Warm import, same host and daemon | 0.70 s | 0.23 s |
| Warm reopen from the kept model (no import) | 7 ms | 2 ms |
| Peak host working set | 245 MiB | 200 MiB |

The cold Gradle import is mostly the daemon starting and configuring the build;
a Maven import in a new host pays for loading Maven into a fresh class loader.

## Kotlin classes for Java on Maven

Buildship puts Kotlin's class output on JDT LS's classpath for Gradle. m2e has
no equivalent, so on Maven JDT LS could not see any Kotlin declaration.
`KotlinClassBridge` closes the gap. It compiles the Kotlin modules with the
project's wrapper and copies only classes carrying `kotlin.Metadata` into a
folder Wayfarer owns. It then calls the `wayfarer.kotlinClassFolders` command,
which attaches that folder to each Kotlin module as an exported library, so the
module and its dependents both see it. The editor refreshes when the Java server
starts and after each Kotlin save, coalescing saves made during a refresh. Each
refresh uses a new folder, so JDT LS reindexes it. Java sees saved Kotlin
changes after the wrapper compile, not unsaved edits.

The command lives in a small JDT LS extension under `src/WayfarerWanderer.Jvm/Jdtls/`.
It is compiled with `javac` against the installed server's plugins and cached
under `~/.local/share/wayfarer/jdtls-bundles/`, so no Eclipse binary is
redistributed. It implements JDT LS's internal `IDelegateCommandHandler`, the
same mechanism fwcd/vscode-java-kotlin uses. A Java extension update can change
that interface. `JdtlsKotlinBundleTests` runs in the default suite whenever JDT
LS is installed. It builds and loads the extension and calls its command, and it
was checked to fail when the command is not registered. In the product, a broken
extension appears in the Build tool window instead of failing silently.
`KotlinClassBridgeTests` (`WAYFARER_LANGUAGE_ACCEPTANCE=1`) proves Java
completes a Kotlin function on Maven and sees a newly added one after a refresh.

JDT LS now starts with `-Djava.import.generatesMetadataFilesAtProjectRoot=false`.
Before, it wrote `.project`, `.classpath` and `.settings` into every module of the
user's tree; the bridge test asserts none appear.

The JetBrains IntelliJ server can serve Java and Kotlin from one model. The
installed, unlicensed 271.2085.0 build did not complete Java on Maven within five
minutes, and Java support there needs a paid licence. The decision (2026-11-19)
is to support both. The first release ships only the open backend above. A later
release (milestone 4) adds a setting that selects the IntelliJ server for
developers with a JetBrains subscription. See "Language backend setting" in the
specification and task 10 of the implementation plan.
