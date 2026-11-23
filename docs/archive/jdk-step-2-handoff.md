# JDK step 2 handoff — projects ask for a JDK, and a missing one can be fixed

Implement step 2 of [JDKs, libraries and Project Structure](../jdks-and-project-structure.md) (§3.2 and §4, item 2).
Start from `main` at `23e4df9` or later, on a branch of its own (for example `jdk-requirements`). The design doc is
the specification. This note adds what's needed to start without rediscovering it.

## What changes and why

Today a project records its JDK as an **absolute path** in `.wayfarer/project.json` (`{"schema":1,"jdk":"C:/…"}`,
see `src/WayfarerWanderer.Jvm/ProjectJdk.cs`). A clone, another machine or a moved JDK loses it silently and falls
back to `JAVA_HOME`. A module whose build asks for a JDK that isn't installed (a Gradle toolchain, Maven `release`
through toolchains, an IntelliJ `jdkName`) only gets a free-text `JvmModule.Problem` with no way to fix it.

Step 2 makes the project ask for a JDK by **requirement** (name, feature release, vendor). It resolves that
requirement against the `JdkRegistry` from step 1, records in the snapshot what each module asked for and what it got,
and shows a banner when a requirement can't be met, with **Choose…** and retry. Download is step 4. Leave a place for
the button, but don't build it.

## Read first

- [The design doc](../jdks-and-project-structure.md), §3.1 and §3.2 (the resolution order is the spec).
- `src/WayfarerWanderer.Jvm/JdkRegistry.cs` (step 1): `All()`, `Named()`, `Add()`, `Distribution()`, `SuggestName()`.
- `src/WayfarerWanderer.Jvm/ProjectJdk.cs`: `Recorded`, `HomeOf`, `Serialize`, `Apply`, `MavenProfiles`.
- `jvm/project-host/host/src/main/java/wayfarer/host/ImportJvms.java`: `forMaven`, `forGradle`, `named`, `candidates`.
- [`docs/task-4-handoff.md`](task-4-handoff.md), "Decisions settled", items 2, 3 and 16. They set what import, compile
  and runtime toolchains mean and that a module problem keeps the snapshot current.

## Seams that already exist

- **`HostImportParams.Jdk`** (C#) / **`ImportParams.jdk`** (Java) carry the recorded home today, and **`Jdks`** carries
  the whole table (step 1). The host can resolve requirements itself from `jdks`, so no new disk search is needed.
- **`JvmToolchain(Home, LanguageVersion, Vendor)`** in `WorkspaceSnapshot.cs` is the record to extend. On the wire it
  is `JvmEnvironment(javaHome, javaVersion, vendor)` in `Protocol.java` and `HostJvmEnvironment` in
  `HostProtocol.cs`, mapped by `HostModel.Toolchain`. Golden examples are in `jvm/project-host/protocol/`, and the
  tests on both sides read them (`ProtocolExamplesTest`, `ProjectHostTests.GoldenExamplesReadIntoTheClientRecords`).
- **Where module JDKs come from:**
  - Gradle: `WayfarerModelBuilder.toolchain(...)` in `jvm/project-host/gradle-model`. It already knows the
    `languageVersion` and catches the "no JDK n" failure into `problems`.
  - Maven: `Targets.release(...)` in `jvm/project-host/maven-model`.
  - IntelliJ: `IdeaEvaluator` (`jdkName`, `languageLevel`), resolved by `ImportJvms.named`.
- **`WorkspaceModelCache`** writes the host result for warm reopen. A new field on the wire must round-trip through
  it, or a warm reopen loses the requirement.
- **`EditorWindow.TrustBanner`** (`EditorWindow.cs:92`) is the only banner and the pattern to copy: a `Border` docked
  at the top of `frame`, shown from the refresh at `EditorWindow.cs:185-193`, which runs after every import.
- **`ProjectSession.IsStale` / `Snapshot`** (`ProjectSession.cs`) is what the window reads after an import.
- **Retry:** reimport through the session's importer, as the build-input watcher does. Don't add a second import path.

## Callers of `ProjectJdk`

| Caller | Today | After |
|---|---|---|
| `ProjectHost.cs:629` | `ProjectJdk.Recorded(root)`, a path | Send the requirement (name, feature, vendor), plus the home it resolved to, so the host still gets a home |
| `ProjectGenerator.cs:113` | `ProjectJdk.Serialize(values.Jdk)` writes the path | Register the chosen home in `JdkRegistry.Current` and write `schema: 2` with its name, feature and vendor |
| `JvmBuild.cs:215`, `JvmLaunch.cs:185` | `ProjectJdk.Apply(start, home)` | Unchanged: they take homes from the snapshot |
| `NewProjectTests.cs:58, :482`, `JvmRuntimeFoundationTests.cs:159` | Assert or write the path form | Assert the requirement, and keep one schema 1 test so old files still read |

## Suggested order

Each slice follows the plan's rule: write the failing check, see it fail, implement, rerun with the tests around it,
commit.

1. **`project.json` schema 2.** Add `ProjectJdk.Requirement(root)`, which returns `JdkRequirement(Name?, Feature?,
   Vendor?)`, and `ProjectJdk.Resolve(root, JdkRegistry)`. Resolve by exact name, then feature and distribution,
   then feature with any vendor, newest update first. A schema 1 file whose path exists registers that home and
   counts as a requirement for it. Rewrite it as schema 2 only when Wayfarer writes the file anyway, never just from
   reading it. Keep `mavenProfiles` and any other keys untouched when writing (`.wayfarer/project.json` is shared
   with them).
2. **Snapshot records the requirement.** Extend the wire `JvmEnvironment` with `required: {name, feature, vendor,
   source}`, where `source` is one of `gradle-toolchain`, `maven-release`, `idea`, `project` or `gradle-daemon`.
   Update both protocol ends, the golden examples, `HostModel` and `WorkspaceModelCache`. Add
   `JvmToolchain.Requirement` with a `Missing` shortcut (`Home is null && Requirement is not null`).
   - Gradle: when the toolchain lookup throws, set `required` and leave `javaHome` null. Keep the `problems` line.
   - Maven: `release` is a language level, not a JDK requirement, unless `maven-toolchains-plugin` is bound. Only
     report it missing when toolchains fail (see "Watch for").
   - IntelliJ: `jdkName`, then `languageLevel` as the feature.
3. **Resolution in the host.** `ImportJvms` resolves a requirement against `params.jdks()` with the same order as C#
   (share the order as golden cases in one JSON file, read by tests on both sides, like the protocol examples).
   Gradle-daemon criteria already pick by feature; route them through the same function.
4. **The missing-JDK banner.** On every import result, collect the missing requirements and show one banner, as
   IntelliJ does: *"Module `api` needs JDK 17 (any vendor). None is installed. [Choose…] [Download…]"*. With several,
   name the first and say "and n more". **Choose…** opens a JDK picker that lists `JdkRegistry.Current.All()`
   filtered to matching features, plus "Add JDK from disk…" (which calls `Add`). Choosing one for a requirement
   from `project` updates `project.json`. A build requirement (a Gradle toolchain, an IntelliJ name) can't be
   changed by editing `project.json`, so register the chosen home under the requested name (IntelliJ) or just
   register it (Gradle and Maven see it in step 3), then reimport. **Download…** is disabled with a tooltip until
   step 4.
5. **New Project writes the requirement.** Register the chosen JDK, write schema 2, and make the IntelliJ template's
   `project-jdk-name` the registry name instead of the bare `{{JDK_VERSION}}` (`templates/jvm/templates/
   application/java/intellij/.idea/misc.xml:3`). Today it writes `project-jdk-name="21"`, which resolves only if a
   JDK happens to be named `21`.
6. **Close out.** Run the JVM host tests (`python tools/build-jvm-host.py --test`), the affected .NET classes
   (`JdkRegistryTests`, `ProjectHostTests`, `WorkspaceImportTests`, `NewProjectTests`, `JvmRuntimeFoundationTests`,
   `NoPassengerInUiTests`), then the full suite. Check the banner in the app on a project that asks for a JDK you
   don't have.

## Tests to write

- Schema 2 round trip, a schema 1 path still read, an unknown key preserved, a corrupt file ignored as today.
- Resolution order from the shared golden cases, in C# and Java.
- A Gradle fixture whose toolchain asks for an absent feature: the snapshot stays current, the module's
  `RuntimeToolchain.Missing` is true with `source: gradle-toolchain`, and the other modules are whole. Pick the
  feature at run time (above anything installed) so the test can't pass by accident on a machine with every JDK.
- An IntelliJ module naming an absent JDK.
- Warm reopen keeps the requirement (through `WorkspaceModelCache`).
- Window: the banner appears after an import with a missing JDK, Choose… with a matching registry JDK reimports and
  the banner goes away. Use the headless window tests (`WelcomeTests` and `ToolDockTests` show how).

## Decisions to bring back to the owner

- **Should `.wayfarer/project.json` be committed?** The starter templates ignore all of `.wayfarer/`
  (`templates/jvm/common/.gitignore:10`, the IntelliJ template's `.gitignore:2`). The requirement form only pays off
  across machines if this file is shared. Options: stop ignoring `.wayfarer/project.json` alone (keeping
  `template.json` and launch state ignored), or keep it local and treat the requirement as per machine. This also
  answers design-doc decision 3.
- **Per-machine override:** when a shared requirement resolves to the wrong JDK on one machine, where does the person's
  choice live? Proposed: Wayfarer's state (per folder, as `TrustState` is), never the shared file.
- **Choose… for a Gradle toolchain:** registering is enough once step 3 lands (`installations.paths`). Until then, a
  chosen JDK only helps if Gradle already finds it. Decide whether step 3 should come before the banner's Choose…
  for Gradle, or whether the banner says "Gradle will see it after step 3" for now.

## Watch for

- **Two languages, one rule.** The resolution order lives in C# (window, New Project) and Java (host). Keep it in
  one golden file that both test suites read, as the protocol examples are.
- **Don't regress `ImportToolchain`.** Build and test run the wrapper on it (task 4 decision 2). A missing
  requirement must never empty it. It falls back as it does today (recorded, `JAVA_HOME`, host JVM).
- **Maven toolchains.** `maven.compiler.release=17` compiles fine on JDK 21, so it is *not* a missing JDK. Only
  `maven-toolchains-plugin` with a `<jdk><version>` requirement is. Don't invent a banner for every Maven module.
- **No "Passenger" in the window.** Banner and picker text are checked by `NoPassengerInUiTests`.
- **No download in this step.** Nothing may fetch anything. The Download button stays disabled.
- **Stale replies.** Banner state comes from the accepted snapshot only (`ProjectSession`), so a superseded
  import's missing JDK never shows.
