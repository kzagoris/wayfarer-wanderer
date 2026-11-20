# Task 4 handoff — external JVM project host

Implement task 4 of the [implementation plan](superpowers/plans/2026-11-18-wayfarer-wanderer-implementation.md), "Import the same project model that tools actually use". Start from `main` at `7cb75bc` or later, on a branch of its own (for example `task-4-project-host`). The plan's task 4 text is the specification. This note adds the context needed to start without rediscovering it.

## What changes and why

Today `src/WayfarerWanderer.Jvm/WorkspaceImport.cs` builds the project model in Wayfarer's own process by reading build-file text. Maven modules come from `pom.xml` as XML, Gradle modules from a regex over `include` lines, source roots from standard folder names, main classes from a file scan. Every module's classpath is empty. Launch and debug make up for that by asking the wrapper for a runtime classpath at launch (`JvmDebugClasspath.cs`, with `Gradle/debug-runtime.gradle` and Maven's dependency plugin).

The decision of 2026-11-20 replaces this with an external JVM process that asks Gradle and Maven themselves, shaped like Passenger's project host. The in-process reader is deleted, not kept as a fallback. Before trust the window shows files and offline highlighting only, with no modules.

## Read first

- Task 4 of the plan (all seven steps) and the [specification](initial-spec.md), section on the JVM project host (line 59).
- Passenger's host, as a pattern only. Wayfarer doesn't reference these projects.
  - `vendor/passenger/src/Passenger.Host/Program.cs`: one process per folder. The first line says what it loaded or why not. Stdout is the protocol's alone, and everything else goes to stderr.
  - `vendor/passenger/src/Passenger.Host/Protocol.cs`: one JSON object per line, `HostRequest(Id, Method, Params)`. Replies are `{"id":N,"partial":…}`, `{"id":N,"result":…}` or `{"id":N,"error":"…"}`. It has `cancel`.
  - `vendor/passenger/src/Passenger.Workspace/ProjectHost.cs`: the client. It covers startup, pending requests, partial results, crash detection (`ProjectHostFailed`) and stderr capture.
- [`docs/validation/jvm-backends.md`](validation/jvm-backends.md) for the current measurements and how JDT LS and the Kotlin server import on their own.

## Seams that already exist

- **`IWorkspaceImporter`** (in `WorkspaceImport.cs`) is the interface `WorkspaceImportSession` (`WorkspaceSnapshotState.cs`) calls. That session already handles generations, cancelling a superseded import, dropping stale replies and keeping the last good snapshot as stale. Make the host client implement `IWorkspaceImporter`, and that logic keeps working. Move the interface out of `WorkspaceImport.cs` before deleting that file.
- **`WorkspaceSnapshot.cs`** already has the target shape: modules with source roots, `JvmClasspath` (compile, test, runtime), import/compile/runtime `JvmToolchain`, tasks and main classes. `OutputRoot` and `AbsentLibraries` exist for IntelliJ modules. Extend it rather than replacing it. The host's JSON should map onto these records one to one.
- **`ChildProcesses` (Passenger.Core)** and `ChildProcessRunner.cs` start and own child processes. The trust tests observe `ChildProcesses.Started`. Start the host through them so ownership and the tests both see it.
- **`WorkspaceTrust`** gates every import. The host runs build code, so it must not start before trust.

## Callers to switch to the snapshot

`WorkspaceImporter` is constructed in `WorkspaceSnapshotState.cs:79` and in tests (`JvmDebugTests`, `JvmMeasurementTests`, `JvmProcessOwnershipTests`, `JvmRuntimeFoundationTests`, `KotlinClassBridgeTests`, `NewProjectTests`). The places that work around the empty classpaths are:

- `JvmLaunch.cs:78-85` calls `JvmDebugClasspath.ResolveAsync` to get a runtime classpath from the wrapper at launch. Once the snapshot carries it, drop that call and delete `JvmDebugClasspath.cs` and `Gradle/debug-runtime.gradle`. A launch still builds first, but takes its classpath from the snapshot.
- `JvmBuild.cs:109` reads `IdeaProject` directly to compile IntelliJ modules with `javac`, and `JvmBuild.cs:130-149` uses module classpaths. Point both at the snapshot.
- `IdeaProject.cs` (the `.iml` reader, about 300 lines) moves into the host as Java, per the plan. Keep its behaviour: library and JDK tables, missing jars reported as `AbsentLibraries`, `out` as the output root, a folder of IntelliJ projects naming the module folder. The existing IntelliJ tests in `JvmRuntimeFoundationTests` define that behaviour, so port them with it.

`KotlinClassBridge.cs` and the JDT LS extension under `Jdtls/` are unaffected. Keep `-Djava.import.generatesMetadataFilesAtProjectRoot=false` in `JvmLanguageServers.cs`.

## Suggested order

Each slice follows the plan's rule: write the failing check, observe the failure, implement, rerun with neighbours, commit.

1. **Baseline.** Restore, build and run the full test suite with the README commands, and record the totals. Some language and debug tests only run with `WAYFARER_LANGUAGE_ACCEPTANCE=1` and installed servers. Note which ran.
2. **Fixtures.** Add the plan's new cases under `tests/fixtures/jvm/`: a non-standard source folder, a source set added by a plugin, a Maven profile, per-module JDKs. Commit an expected-model JSON per fixture (module ids, roots, ordered classpaths, JDKs, tasks, mains), produced by building each fixture with its wrapper. The existing `maven/`, `gradle/` and `idea/` fixtures and their `rename-expected.json` files stay.
3. **`WorkspaceImportTests.cs`.** Write the trust cases first: no host, wrapper or daemon start before trust, and no modules shown before trust. Move the trust, import and stale-snapshot cases from `JvmRuntimeFoundationTests.cs` here. `tools/acceptance.py` already selects `WorkspaceImportTests` for `--only import`.
4. **Host skeleton and client.** Create `jvm/project-host/` with its own Gradle wrapper (pinned distribution checksum, dependency locks, verification metadata). Add `tools/build-jvm-host.py` and an incremental MSBuild target that stages the JAR and dependencies under `artifacts/jvm-host`. Write `src/WayfarerWanderer.Jvm/ProjectHost.cs` implementing `IWorkspaceImporter`. Prove the protocol before any evaluator exists: the first line, a request with partial results and a result, an error, `cancel`, the host crashing mid-request and the host hanging. Stage the host from a different working directory as the plan asks.
5. **Gradle evaluator.** Use the Tooling API matched to the wrapper, with an injected init script registering a custom `ToolingModelBuilder` that returns exactly Wayfarer's model. Don't use the built-in Eclipse or IDEA models. Compare against the expected-model JSON from step 2.
6. **Maven evaluator.** Use the wrapper-selected Maven runtime loaded in an isolated class loader, resolving active profiles. This is the least settled part, so prototype it early if it worries you.
7. **IntelliJ modules.** Port `IdeaProject.cs` into the host and run the ported tests.
8. **Switch callers and delete the reader.** Change launch, build and debug as listed above. Delete `WorkspaceImporter`, its three adapters and `WorkspaceLayouts`. Search `src` for `pom.xml`, `settings.gradle`, `build.gradle` and `.iml` and confirm that only the host reads them. `ProjectGenerator.cs` writes these files, and writing is fine.
9. **Change, failure and switch.** Add and remove a dependency and compare the snapshot with both language servers' effective roots, dependencies and JDKs. Fail an import and repair it. Switch workspaces with a reply in flight. Record cold import (cold and warm Gradle daemon), warm reopen and host memory, plus protocol examples and the comparison, in `jvm-backends.md`.
10. **Close out.** Run `python tools/acceptance.py --only import`. Tick only the task 4 checkboxes whose named tests exist and pass.

## Decisions to bring back to the owner

- **Tooling JVM:** bundle one, or find one on the machine. Check versions, redistribution terms and cold-start cost first, as the plan says. Also decide the host's minimum Java version.
- **Maven approach:** if the isolated class loader proves unworkable, describe the alternative (for example, running the wrapper's `mvn` with a small extension that writes the model) before switching.
- **Gradle versions:** which wrapper versions the Tooling API and init script support, and what an unsupported one shows.

## Watch for

- **Stdout is the protocol.** The Tooling API, Maven and any logging library will happily write to stdout. Redirect everything to stderr at host startup, as `Program.cs` does in Passenger.
- **One schema, two languages.** Passenger compiles `Protocol.cs` into both ends. Here the ends are Java and C#. Keep golden JSON examples, use the same files in tests on both sides, and version the first line.
- **Ownership.** Closing a window or switching workspace ends that window's host. An unrelated JVM and the shared Gradle daemon must survive (task 6's tests check this).
- **No "Passenger" in the window.** Host error text reaches the UI, and `NoPassengerInUiTests` fails on any string literal that says Passenger.
- **Locked builds.** The new MSBuild target must not break `dotnet restore --locked-mode`. The host's Gradle build downloads dependencies, so record what a clean clone needs.
- **`vendor/passenger` is Wayfarer's copy.** If anything there must change, edit it in place, and never push upstream. Prefer not touching it for this task.

## Decisions settled with the owner (2026-11-20)

These answer "Decisions to bring back" above and refine the plan where they differ.

### Tooling JVM and builds
1. **Tooling JVM is discovered, not bundled**, for task 4: a Wayfarer setting, then `JAVA_HOME`, `PATH`, then the usual install folders. Minimum **Java 21**, so one JVM can run the host, JDT LS and the debug adapter. Bundling is revisited in task 9. An absent or too-old JVM names the host and offers the JDK picker.
2. **`ToolingToolchain` means the host's JVM only** (the host and the debug adapter's JDI). Build and test run the wrapper on the snapshot's `ImportToolchain`. Launch and debug run the app on the module's `RuntimeToolchain`.
3. **Gradle daemon JVM** for import: `gradle/gradle-daemon-jvm.properties`, then `org.gradle.java.home`, then `.wayfarer/project.json`, then the host's JVM. It's recorded as `ImportToolchain`. A class-version failure names the JDK and the Gradle version and offers the picker.
4. **Gradle 8.0 or later** is supported. Older wrappers get a clear error plus the stale snapshot.
5. **The host's build in MSBuild** runs `tools/build-jvm-host.py` on build/publish only, never on restore. It's incremental, fails clearly without JDK 21, and `-p:WayfarerSkipJvmHost=true` skips it.

### Process model (follows Passenger's `ProjectHosts`)
6. **One host per tooling JVM, shared by windows**, in a pool that tracks the folders each host serves (this replaces the plan's "one host per window"). Separate Gradle versions get separate Tooling API connections, and separate Maven versions get separate isolated class loaders, all in one host. A window that closes or switches workspace cancels its requests and releases its folder. A host ends when it serves no folder, or when trust is revoked. The shared Gradle daemon and unrelated JVMs survive.
7. Requests run concurrently in the host. `cancel` looks a request up by id. The host starts in Wayfarer's folder, not the workspace's, and has one minute for its first line. Its stderr goes to the IDE log, and the first line of a crash becomes the reason shown.
8. **Hangs:** the client pings every 5 s while a request is open, and the host answers on its reader thread. Three missed pings kill the host and fail the generation, keeping the stale snapshot. Requests have no timeout.
9. No `hold` method. Instead, Build, Test and Run wait (with Cancel) for an import running for the same folder.
10. No head start in task 4.

### Protocol and model
11. The wire uses **BSP names and shapes** (build targets, sources, dependency modules, JVM run environments). `ProjectHost.cs` maps them onto the existing C# records in one translator.
12. A single **`import`** method sends one partial per module, then a result. The client collects partials and reports progress only (an optional `IProgress<ImportProgress>`). `State.Accept` only ever sees complete snapshots.
13. **Source sets:** `JvmSourceSet(Id, Name, Roots, CompileClasspath, RuntimeClasspath)` under `JvmModule` (ids like `:app:integrationTest`, `g:a:test`). `JvmModule.Classpath` stays as the main/test shortcut.
14. **Dependencies between modules** are listed as output folders plus a `moduleRef`, so a launch builds `classes`/`compile`, not jars.
15. **Classpath entries** carry `path` plus `coordinates` (`g:a:v[:classifier]`) or `moduleRef`. The expected-model JSON compares coordinates and module refs in order, and paths relative to the fixture root.
16. **A problem per module:** `JvmModule.Problem` for a dependency that doesn't resolve or a missing toolchain, with the snapshot still current. The whole generation fails only when the build won't configure.
17. Each result carries **`buildInputs`**, the files the evaluation read. The client watches those plus the root build files and reimports after a 500 ms debounce.
18. **Warm reopen:** a versioned `StateDocument` in Wayfarer's `StateStore` holds the last good snapshot plus stamps of its `buildInputs` (last write time in ms plus length, the same rule in Java and C#). After trust, matching stamps mean the snapshot is current and no import runs. A mismatch shows it stale and reimports. Nothing is read before trust.
19. **Composite builds:** projects of builds included through `includeBuild` are modules with ids prefixed by the build path. `buildSrc` and plugin-only included builds are left out.
20. **Maven profiles:** Maven's own activation, plus an optional `mavenProfiles` list in `.wayfarer/project.json` (no UI yet). The user's `settings.xml` and `.mvn/maven.config` are honoured.
21. **Getting the Maven runtime:** the host runs `mvnw -v` to download it, then loads `lib/` from `~/.m2/wrapper/dists` in an isolated class loader and runs `ProjectBuilder`. A configured `mvn` installation is used when there's no wrapper. This gets prototyped right after the host skeleton.
22. **Main classes:** build configuration first (`application.mainClass`, `springBoot.mainClass`, the configuration of the Boot and exec plugins), then a source scan in the host. Each main is tagged `configured` or `discovered`.
23. **Tasks:** Gradle tasks in the groups `application`, `build`, `verification` and Boot's group, plus `run`/`bootRun`. For Maven, `compile`, `test`, `package`, `verify`, plus `spring-boot:run` when that plugin is bound.

### Fixtures
24. **Per-module JDKs:** a Gradle build with toolchains 11 and 22 in two modules, and a Maven reactor with `release` 11 and 22 through `toolchains.xml`. The import runs on 24. Foojay auto-download is off. A missing JDK **fails** the test with the version it needs. `tools/acceptance.py` lists the JDKs a machine needs.
