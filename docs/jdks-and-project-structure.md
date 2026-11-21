# JDKs, libraries and Project Structure — what IntelliJ does and what Wayfarer should do

Draft for review, 2026-11-21. It compares IntelliJ IDEA's Project Structure, SDK table and JDK download with what
Wayfarer has, and proposes a design to bring Wayfarer close to it.

**Status:** steps 1 to 3 of §4 are built. Where the build settled something differently from the first draft, the
sections below now describe what was built.

## 1. How IntelliJ IDEA does it

### Two levels: platform settings and project settings

IntelliJ splits **Project Structure** (Ctrl+Alt+Shift+S) into two halves, and keeping them apart is the main design idea.

| Level | What it holds | Where it is stored | Shared through VCS? |
|---|---|---|---|
| **Platform** (per user, per machine) | **SDKs**: each has a *name*, a type (JavaSDK), a home path, classes, sources, Javadoc and annotations. **Global libraries.** **Path variables.** | `<config>/JetBrains/<product><version>/options/jdk.table.xml`, `applicationLibraries.xml`, `path.macros.xml` | No |
| **Project** | Project name, **project SDK (by name)**, language level, compiler output. **Modules**: content roots, source/test/resource/excluded folders, per-module SDK (or "inherit project SDK"), language level, **dependencies** (in order, with scope). **Project libraries.** Facets, artifacts. | `.idea/misc.xml`, `.idea/modules.xml`, `*.iml`, `.idea/libraries/*.xml` | Yes |

A project names its JDK, for example `project-jdk-name="corretto-21"`. It never stores a path. Each machine maps that
name to a home in its own `jdk.table.xml`, so a checked-in `.idea` works on every machine. Paths inside project files
go through macros (`$PROJECT_DIR$`, `$MODULE_DIR$`, `$MAVEN_REPOSITORY$`, `$USER_HOME$` and user path variables) for
the same reason.

### Module dependencies ("importing classes")

A module's `<orderEntry>` list is its classpath, in order:

- `inheritedJdk` or `jdk jdkName="17"`: the module's JDK.
- `sourceFolder`: the module's own output.
- `module module-name="core"`: another module's output, optionally `exported`.
- `library name="junit" level="project|application"`: a named library from the project or global table.
- `module-library`: a library defined inline, used by this module only.
- Each entry has a `scope`: `COMPILE` (the default), `TEST`, `RUNTIME` or `PROVIDED`.

A library is a set of roots (`CLASSES`, `SOURCES`, `JAVADOC`). You add one in three ways:

1. **Java**: pick jars or a folder of jars ("jar directory").
2. **From Maven…**: search or type `group:artifact:version`. IntelliJ resolves it with Maven Resolver (transitive
   dependencies optional), downloads to `~/.m2` or to a project folder such as `lib/`, and writes a *repository
   library*:
   ```xml
   <library name="org.junit.jupiter:junit-jupiter:5.11.0" type="repository">
     <properties maven-id="org.junit.jupiter:junit-jupiter:5.11.0" include-transitive-deps="true" />
     <CLASSES><root url="jar://$MAVEN_REPOSITORY$/org/junit/.../junit-jupiter-5.11.0.jar!/" /></CLASSES>
   </library>
   ```
   On another machine with an empty `~/.m2`, IntelliJ sees the `maven-id` and downloads the jars again. The library
   never shows as broken.
3. **Kotlin/JavaScript/…** library types added by plugins.

For **Maven and Gradle projects** the build file owns all of this. IntelliJ fills modules and libraries from the
import, shows them read-only (or warns that edits will be lost on reimport), and "Add dependency" edits `pom.xml` or
`build.gradle(.kts)` instead.

### Finding JDKs

Add SDK › JDK lists **detected SDKs** from a scan of the usual places: `JAVA_HOME`, `PATH`, `~/.jdks`, SDKMAN, asdf,
Gradle's `~/.gradle/jdks`, `/usr/lib/jvm`, `/Library/Java/JavaVirtualMachines`, the Windows registry and Program
Files, and WSL distributions. Picking one registers it under a suggested name such as `temurin-21`.

### Downloading JDKs

Add SDK › **Download JDK…** asks for a version, a vendor (Oracle OpenJDK, Temurin, Corretto, Zulu, Liberica, GraalVM,
JetBrains Runtime, Semeru, SapMachine, Microsoft, Dragonwell) and a location (default `~/.jdks/<vendor>-<version>`).
The catalogue is a feed JetBrains publishes, `https://download.jetbrains.com/jdk/feed/v1/jdks.json.xz`. On
2026-11-21 it listed 643 JDKs from 11 vendors. Each package entry gives the OS and architecture, the archive URL,
`sha256`, archive type, size and how to find the Java home inside the archive (`package_root_prefix`,
`package_to_java_home_prefix` for macOS's `Contents/Home`). IntelliJ downloads to a temporary file, checks the
checksum, unpacks, moves the folder into place and registers the SDK.

### Resolving a missing JDK

When a project names a JDK this machine lacks (`SDK 'corretto-17' is not found`), IntelliJ resolves it instead of only
failing. It looks for a local JDK of the same version and vendor first, then offers to download a matching one, all
from one banner (**Setup SDK** / **Download**). Build-tool projects have one more setting: the **Gradle JVM** and the
**Maven importer JDK** are chosen apart from the project SDK. They can be the project SDK, `JAVA_HOME`, or Gradle's own
`gradle.properties`/daemon criteria.

Gradle itself downloads toolchains (the foojay resolver plugin, into `~/.gradle/jdks`) and, since 8.13, daemon JVMs
from `gradle-daemon-jvm.properties`. Maven picks toolchains from `~/.m2/toolchains.xml` and downloads nothing itself.

## 2. Where Wayfarer stands (after step 2)

| Concern | Today | Gap against IntelliJ |
|---|---|---|
| Project JDK | `.wayfarer/project.json` schema 2 holds a **requirement** (name, feature, vendor). The starter templates commit it (`!.wayfarer/project.json` in their `.gitignore`). A person's choice for one project on this machine lives in Wayfarer's state (`ProjectJdkChoices`) and wins over the shared requirement. Schema 1 paths still read. | None for the project level. |
| JDK table | `JdkRegistry` (`jdks.json`): kept JDKs, then IntelliJ's `jdk.table.xml` names, then JDKs `JdkScanner` detects. The client sends the table in `HostImportParams.Jdks`. The host doesn't search the disk. | No JDK sources or Javadoc roots. Rename and remove don't account for what points at a name (see §3.1). |
| Tooling JVM | A setting, then `JAVA_HOME`, `PATH`, then the newest Java 21 or later in the registry (`ToolingJvm.cs`). | The setting is still a **path**, not a registry name. When none is found, the user must install one themselves. |
| Per-module JDKs | `JvmEnvironment` carries `required {name, feature, vendor, source}`. The host settles each module against the table in the shared order (`protocol/jdk-resolution.json`). An unmet requirement leaves `Home` null (`JvmToolchain.Missing`). | No download (step 4). |
| Missing-JDK banner | Names the first missing requirement and how many more. **Choose…** picks from the table or adds from disk, then reimports. **Download…** is disabled. | Done, except Download. |
| JDKs for Gradle and Maven | Step 3: `BuildJdks` passes the table to the wrappers (`-Porg.gradle.java.installations.paths`, `--global-toolchains`) and the host passes it to the Tooling API (`GradleJdks.java`). | None. |
| JDKs for language servers | `java.configuration.runtimes` lists module homes, plus the table's JDK for a feature a module asks for. `java.import.gradle.arguments` carries the same installation paths. | m2e in JDT LS reads no toolchains file (§3.3). |
| Download | None. The fixtures turn foojay auto-download off on purpose. | Missing entirely. |
| IntelliJ modules | `IdeaEvaluator` reads one `.iml`: source folders, `library` entries, the JDK by name (its release read from the name when the table lacks it) and the module's own language level. | No `scope`, no `module` dependencies, no `module-library`, no `$MAVEN_REPOSITORY$`. Repository libraries with missing jars show as `AbsentLibraries` instead of being downloaded. Nothing writes `.iml` or library files. |
| UI | New Project registers the chosen JDK and writes schema 2. The IntelliJ template writes the registry name as `project-jdk-name`. Settings has no JDK page. | No Project Structure dialog, no JDK list, no library editor. |

## 3. Proposed design

### 3.1 A JDK registry (the platform level)

Add **`JdkRegistry`** in `WayfarerWanderer.Jvm`, kept in Wayfarer's per-user state the way IntelliJ keeps
`jdk.table.xml`:

```jsonc
// <Wayfarer state>/jdks.json
{
  "schema": 1,
  "jdks": [
    { "name": "temurin-21", "home": "C:/Users/me/.jdks/temurin-21.0.13", "version": "21.0.13", "feature": 21,
      "vendor": "Eclipse Adoptium", "origin": "downloaded" },
    { "name": "corretto-17", "home": "C:/Program Files/Amazon Corretto/jdk17.0.12_7", "version": "17.0.12",
      "feature": 17, "vendor": "Amazon.com Inc.", "origin": "detected" }
  ]
}
```

- **One scanner.** Move the install-folder search into the registry. Detected JDKs are listed but not saved until the
  user picks one or a project resolves to one. Also read IntelliJ's `jdk.table.xml` (the host already can) so JDKs the
  user set up in IntelliJ appear under the same names.
- **The host stops scanning.** The client sends the registry's JDKs in `HostImportParams` (`jdks: [{name, home,
  feature, vendor}]`). `ImportJvms.candidates` then picks from that list, which removes the duplicated search.
- **The tooling JVM is a registry entry too.** The Settings value `ToolingJvms.Configured` becomes a JDK name.
  *Not done in step 1: it is still a path. Do it with the Settings JDK page (step 6) or with tooling-JVM recovery
  (step 4), whichever comes first. Read an old path setting by registering it, as schema 1 files are.*
- **Sources and Javadoc.** IntelliJ's SDK entries hold `SOURCES` and `JAVADOC` roots. A registry entry holds only the
  home. Find `lib/src.zip` (or `src.zip` at the root for Java 8) when the JDK is read, and record it as `sources`, so
  navigation into JDK classes has a place to read from. A JDK without `src.zip` (some Linux packages) shows as such
  in the JDK list.
- **Names other things point at.** A registry name is referenced from `project.json` requirements, `ProjectJdkChoices`,
  IntelliJ `jdkName`s and (later) the tooling-JVM setting. Rules:
  - **Remove** a kept JDK only after listing which open and recent projects use it. A choice that names a removed JDK
    is dropped, and the project falls back to its requirement.
  - **Rename** updates `ProjectJdkChoices` and the tooling setting. It never rewrites a project's `project.json` or
    `.iml`: those are shared, so their name stays and resolves by feature and vendor.
  - **A kept JDK whose home is gone** stays listed as missing (greyed, with Remove and Locate…) instead of vanishing,
    so a JDK on an unplugged drive comes back.
- **Two processes writing `jdks.json`.** `AtomicFile.Write` keeps the file whole but loses one of two concurrent
  changes. Reload and reapply the change just before writing (read, merge by name, write), and watch the file so
  another window's change shows up.

### 3.2 Projects name a requirement, not a path

Change `.wayfarer/project.json` so it can be committed:

```jsonc
{ "schema": 2, "jdk": { "name": "temurin-21", "feature": 21, "vendor": "temurin" } }
```

Resolution, for the project and for each module:

1. **The build's own declaration wins.**
   - Gradle: a `toolchain.languageVersion` (and vendor). `gradle/gradle-daemon-jvm.properties` picks the daemon JVM
     through the same order.
   - Maven: only a `maven-toolchains-plugin` `<jdk>` requirement. `maven.compiler.release` is a language level, not a
     JDK requirement: `release=17` compiles fine on JDK 21, so it never makes a JDK missing.
   - IntelliJ: the `jdkName`. When the table lacks that name, the release the name carries is the feature asked for
     (`corretto-21`, `21`, `temurin-21.0.5`, `jdk1.8.0_202`; digits inside a word such as `my-jdk-v2` don't count).
     The language level is the feature only when the name carries none. A module's own `LANGUAGE_LEVEL` overrides
     the project's level in `misc.xml`.
2. Otherwise the project's `jdk` requirement, unless the person chose a JDK for this project on this machine
   (`ProjectJdkChoices`, kept per folder in Wayfarer's state, never in the shared file).
3. Match against the registry: the exact name (any case), else the same feature and distribution, else the same
   feature and any vendor (newest update first, table order on a tie). C# (`JdkResolution.Choose`) and the host
   (`ImportJvms.choose`) both test against `protocol/jdk-resolution.json`.
4. **No match** → the module's toolchain has a requirement and no home (no free-text `Problem` alone). The window
   shows a banner like IntelliJ's: *"Module `api` needs JDK 17 (any vendor). None is installed. [Choose…]
   [Download…]"*. Retrying reimports through the session. The import toolchain never empties: it falls back to the
   recorded JDK, `JAVA_HOME`, then the host's JVM, because build and test run the wrapper on it.

A schema 1 file that holds an absolute path still reads. The path resolves **for that project only** and is never
added to the person's `jdks.json`, because the file comes with the project. A UNC path or one inside the project is
not taken. The file becomes schema 2 only when Wayfarer writes it anyway, never just from reading it.

Snapshot change (built): the wire `JvmEnvironment` gains `required {name, feature, vendor, source}`, where `source`
is `gradle-toolchain`, `gradle-daemon`, `maven-release` (a toolchains-plugin requirement), `idea` or `project`.
`JvmToolchain.Requirement` and `Missing` read it. `WorkspaceModelCache` keeps it for warm reopen.

**Choose… and trust.** For an IntelliJ requirement, Choose… registers the chosen home under the name the project
asked for. That name comes from a shared file, so (proposed, not built):

- If the table already holds that name for another home, Choose… asks before replacing it, and never replaces a JDK
  that came from IntelliJ's own table.
- Choose… only runs for a trusted project, like the import itself.

**Language level beyond the JDK** (proposed, not built). A language level above the resolved JDK's feature (JDK_21 code on JDK 17) is a
missing requirement, not a compile error later. Preview levels (`JDK_21_PREVIEW`, Gradle or Maven
`--enable-preview`) need exactly that feature, because preview features change between releases. Record `preview` on
the requirement and match the feature exactly when it is set.

### 3.3 Hand Wayfarer's JDKs to Gradle and Maven

Gradle and Maven should see the same JDKs the IDE shows, so a toolchain request doesn't fail with a JDK on disk.
Built in step 3 (`BuildJdks.cs` in the app, `GradleJdks.java` in the host):

- **Gradle:** pass `-Porg.gradle.java.installations.paths=<registry homes>` (Tooling API arguments and the wrapper
  command line) and keep `org.gradle.java.installations.auto-download` as the project sets it. Gradle-run toolchains
  then resolve to the registry. A command-line `-P` **replaces** the value in `~/.gradle/gradle.properties` and the
  project's `gradle.properties`, so read those first and pass the union (theirs first, then the registry homes).
  Leave `auto-detect` alone: Gradle's own detection still adds what it finds.
- **Maven:** write a generated `toolchains.xml` under Wayfarer's state, one `<toolchain type="jdk">` per registry JDK
  (`version` as the full version, `vendor` as the distribution, `id` as the registry name). `--global-toolchains`
  **replaces** the Maven install's own `conf/toolchains.xml`, so copy that file's toolchains into the generated one
  first. The user's `~/.m2/toolchains.xml` still applies on top. `maven-toolchains-plugin` 3.2 can also discover
  JDKs itself (`select-jdk-toolchain`). Projects using it need nothing from Wayfarer, but the requirement reader must
  understand that goal as well as `toolchain`.
  *As built:* Maven matches a plain `<version>17</version>` exactly, so besides one toolchain per JDK (full version,
  `id` the registry name) the file holds one per feature (`17`, `1.8` for Java 8) pointing at that feature's newest
  JDK. The installation's file is found through the wrapper's `~/.m2/wrapper/dists` folder, else `MAVEN_HOME`. The
  file is `toolchains.xml` beside `jdks.json`.
- **Language servers:** JDT LS runs its own Gradle (Buildship) and Maven (m2e) import, apart from the host. Give it
  the same view, or the two imports disagree:
  - `java.import.gradle.arguments` gets the same `installations.paths`, and `java.import.gradle.java.home` stays the
    import toolchain, as it is today.
  - `java.configuration.maven.globalSettings` doesn't cover toolchains, and JDT LS has no toolchains setting. m2e
    sets each module's JRE from its execution environment (`JavaSE-17`), which `java.configuration.runtimes` answers,
    so Maven toolchain modules resolve in the editor without a toolchains file. Wayfarer doesn't write
    `~/.m2/toolchains.xml`.
  - `java.configuration.runtimes` today lists only module homes in the snapshot. Add one runtime per registry JDK of
    a feature some module asks for, so a module whose JDK was just chosen compiles in the editor before reimport
    finishes. A missing module gives no runtime, and its files show the banner's message instead of a wall of
    errors.

### 3.4 Download JDKs

Add **`JdkDownloader`** in `WayfarerWanderer.Jvm`:

- **Catalogue: the foojay Disco API** (`api.foojay.io/disco/v3.0`), the same service Gradle's foojay resolver uses. It
  covers the main vendors and returns a direct URL and a SHA-256 checksum for each package. I checked
  `packages?version=21&distribution=temurin&operating_system=windows&architecture=x64&archive_type=zip&package_type=jdk&latest=available`
  and `ids/<id>`, which give the GitHub release URL and checksum. JetBrains' own feed has a better schema (it says
  where the Java home is inside the archive), but it serves JetBrains products, and its terms for third-party IDEs
  are not stated. Don't use it unless that is cleared.
- **Steps:** query, then download to `<state>/downloads/<id>.part` with progress and Cancel, verify SHA-256, extract
  zip or tar.gz into a staging folder next to the target, find the home (the folder with `bin/java` and `release`;
  macOS `Contents/Home`), move it to `~/.jdks/<distribution>-<version>`, read `release`, register it.
  `~/.jdks` is IntelliJ's default too, so both IDEs share downloads, and the scanner already looks there.
- **Only on the user's click.** A download fetches executables, so it never starts without one: in the Download JDK
  dialog, the missing-JDK banner or the tooling-JVM error. Offline or failed downloads leave the
  missing-requirement state with retry. A download that is cut off leaves only `.part` and staging folders, which are
  removed on the next start.
- **Defaults:** vendor Temurin (free for production use, TCK-tested). The dialog lists the vendors foojay reports as
  `free_use_in_production` first and marks the others.
- **Tooling JVM recovery:** when no Java 21 or later is found on first start, offer "Download Temurin 21". This also
  partly answers task 9's bundle-or-discover question, since Wayfarer can stay unbundled and still work on a bare
  machine.
- **Platform matching:** ask for the machine's OS and architecture (`RuntimeInformation.OSArchitecture`, not the
  process's, so an x64 Wayfarer on arm64 Windows or macOS gets an arm64 JDK). On Linux ask for `libc_type` `glibc`
  or `musl` from `ldd --version` or `/lib/ld-musl-*`. When the vendor has no build for the platform, the dialog says
  so and offers the vendors that have one.
- **Unpacking:**
  - Zip entries carry no Unix mode in .NET's default extraction. On Linux and macOS set the exec bit on
    `bin/*` and `lib/jspawnhelper` from the entry's external attributes, or the JDK won't run.
  - Use tar.gz extraction that keeps modes and symlinks (`System.Formats.Tar`).
  - Reject entries that leave the staging folder (`..`, absolute paths, symlinks pointing outside).
  - On Windows, stage under a short path so deep JDK paths stay under `MAX_PATH`.
- **Checksums prove integrity, not origin.** The SHA-256 comes from the same API as the URL, so it catches a broken
  download, not a substituted one. Accept only `https` URLs on hosts the vendor publishes from (GitHub releases,
  `corretto.aws`, `cdn.azul.com` and so on, a list kept in code). Where a vendor publishes a signature (Temurin's
  `.sig`, Corretto's `.asc`), verifying it is a later step. Say this in the dialog's details, not as a warning.
- **Network:** honour the system proxy (`HttpClient`'s default handler does) and a proxy setting in Wayfarer's
  Settings. A catalogue URL setting lets a company point at a foojay mirror. With neither the catalogue nor a
  download reachable, the dialog offers "Add JDK from disk…" instead.
- **Space and concurrency:** check free space against the package size times three (archive, staging, final)
  before starting. One download per target folder at a time: a second request for the same JDK, from another
  window too, joins the first (a lock file next to the `.part`).
- **Uninstall:** removing a downloaded JDK from the registry offers to delete its folder, but only when it is under
  `~/.jdks` and Wayfarer downloaded it (`origin: downloaded`). A JDK IntelliJ downloaded to the same folder is left
  alone unless the person ticks "also delete files".

### 3.5 Libraries and module dependencies (plain IntelliJ-format projects)

For projects without a build tool, Wayfarer reads and writes IntelliJ's own files. A project edited in one IDE then
opens the same in the other.

- **Reader (host, `IdeaEvaluator`):** honour `scope` (compile/test/runtime/provided into the right classpath), `module`
  entries (another module's output as a `moduleRef`, `exported` passed on), `module-library`, `.idea/modules.xml`
  with several modules, and the `$MAVEN_REPOSITORY$`, `$USER_HOME$` and path-variable macros.
- **Platform-level files:** the client reads IntelliJ's `path.macros.xml` (path variables) and
  `applicationLibraries.xml` (global libraries) from the same config folders `JdkRegistry.IntelliJTable` searches,
  newest product first, and sends them with the import as it sends the JDK table. Wayfarer keeps its own path
  variables and global libraries in its state, offered first. An unresolved macro is a module problem naming the
  variable, with "Set path variable…", never a silent empty root.
- **Kotlin in plain projects:** IntelliJ's Kotlin projects reference a `KotlinJavaRuntime` project library (and,
  in older ones, the plugin's bundled stdlib) plus a Kotlin facet with the compiler settings. Read the library like
  any other. When its jars point into the IntelliJ plugin folder that isn't on this machine, resolve
  `org.jetbrains.kotlin:kotlin-stdlib` at the facet's version as a repository library.
- **Repository libraries:** when a `type="repository"` library's jars are missing, resolve its `maven-id` with Maven
  Resolver in the host and download into `~/.m2`. This needs a resolver that doesn't depend on a project wrapper, so
  the host carries Maven Resolver as its own dependency. Only libraries without a `maven-id` stay `AbsentLibraries`.
- **Writer (client):** "Add library → From Maven…" (coordinates, transitive yes or no, scope) writes
  `.idea/libraries/<name>.xml` and the `orderEntry`. "Add jars or folder" writes a plain library. Writes are
  atomic, keep unknown elements and attributes, and trigger the normal reimport through `buildInputs`.

For **Maven and Gradle projects**, Wayfarer doesn't keep a library table. Project Structure shows the imported modules
and dependencies read-only, with "Open build file". "Add dependency" inserts into `pom.xml` or the Gradle
`dependencies {}` block as a later step (needs a coordinate search; foojay is JDKs only, so Maven Central's search API
or a local index).

### 3.6 Project Structure dialog (UI)

One dialog, opened from File › Project Structure… and the missing-JDK banner. It follows IntelliJ's layout:

- **Project:** name, JDK (a combo box of registry names plus "Download JDK…" and "Add JDK from disk…"), language
  level, output folder (IntelliJ projects only).
- **Modules:** sources, paths and dependencies tabs. They can be edited for IntelliJ projects. For Maven or Gradle they
  are read-only and show where each value came from.
- **Libraries:** IntelliJ projects only.
- **JDKs** (platform): the registry. Add, remove, rename, download, and see which projects use each.
- **Problems:** missing JDKs, missing libraries and module `Problem`s, each with its fix.

### 3.7 The JDK a launch runs on

The initial spec asks each launch configuration to keep its own project JDK. Today `JvmLaunch` and `JvmBuild` take
the module's runtime toolchain from the snapshot. Rules:

- A launch configuration holds an optional JDK **name**, never a path, so a shared configuration travels like
  `project.json`. Absent means the module's runtime toolchain.
- A named JDK the table lacks resolves by the name's release, as an IntelliJ name does. If nothing matches, Run
  shows the missing-JDK banner for that configuration instead of starting on the wrong JDK.
- A launch JDK older than the module's compile target fails before launch with the two versions named, not with
  `UnsupportedClassVersionError` at run time.
- Gradle and Maven test runs keep using the build's own toolchain. A launch JDK applies only to Wayfarer's direct
  application and debug launches.

## 4. Suggested order

Each step stands alone and comes with tests, following the plan's rule.

1. **Done** (`23e4df9`). `JdkRegistry` with one scanner; the client sends JDKs to the host, which no longer scans.
   Tests: the same JDK list in C# and in the host; reading IntelliJ's `jdk.table.xml`.
2. **Done** (`bd195c4` to `e20d27b`). Project JDK as a requirement (schema 2, schema 1 still read, never registered);
   `required` on the wire and `JvmToolchain.Requirement`; the shared resolution cases; the missing-JDK banner with
   Choose… and retry; New Project writes the requirement and the registry name.
   *Left from this step:* the tooling-JVM setting as a registry name (§3.1).
3. **Done.** Registry JDKs passed to Gradle (`installations.paths`, merged with the person's own value) and Maven (generated
   `toolchains.xml`, merged with the install's), and the same to JDT LS (§3.3). Test: the per-module JDK fixtures
   resolve with toolchains only in the registry, and a `gradle.properties` path is kept. The banner's "the build sees
   it once Wayfarer passes its JDKs" note is gone.
4. `JdkDownloader` against foojay, with a fake catalogue server in tests (checksum mismatch, Cancel, cut-off
   download, macOS layout, a zip without exec bits, an entry escaping staging, two concurrent requests); the
   Download JDK dialog; tooling-JVM recovery.
5. `IdeaEvaluator`: scopes, module dependencies, several modules, macros and `path.macros.xml`, global libraries,
   repository-library resolution, Kotlin runtime library.
6. Project Structure dialog; library writer for IntelliJ projects; JDK list with rename, remove and missing homes
   (§3.1); JDK sources roots.
7. Launch configuration JDKs (§3.7).
8. Later: "Add dependency" for Maven and Gradle build files.

## 5. Decisions for the owner

- **Catalogue source:** foojay Disco API (recommended), Adoptium's API only, or JetBrains' feed once its terms are
  cleared.
- **Default vendor** for one-click downloads: Temurin is proposed.
- **Download location:** `~/.jdks`, shared with IntelliJ (proposed), or a Wayfarer-only folder.
- **Writing IntelliJ files:** Wayfarer edits `.idea` and `.iml` in place (proposed, which keeps both IDEs in step), or
  keeps its own format for plain projects.
- **Maven toolchains for JDT LS:** if m2e can't take a toolchains file through settings, may Wayfarer write to the
  person's `~/.m2/toolchains.xml` (with consent, keeping their entries), or do Maven toolchain modules stay
  unresolved in the editor?
- **Choose… for an IntelliJ name that already exists:** ask and replace (proposed), or always register under a new
  name and record a per-project choice instead.
- **Deleting downloaded JDKs:** offer to delete the folder on remove (proposed, Wayfarer's downloads only), or never
  delete files.
- **Signature checks:** verify vendor signatures in step 4, or ship with checksums and an allow-list of hosts first
  (proposed).

### Settled

- **`.wayfarer/project.json` is committed.** The templates ignore the rest of `.wayfarer/` and keep `project.json`.
  (Was decision 3.)
- **A person's choice for one project lives in Wayfarer's state** (`ProjectJdkChoices`, per folder), never in the
  shared file.
- **A schema 1 path is not added to the person's JDK table.** It resolves for that project only.
- **Maven `release` is not a JDK requirement.** Only a `maven-toolchains-plugin` JDK is.
