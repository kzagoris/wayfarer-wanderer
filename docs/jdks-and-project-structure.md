# JDKs, libraries and Project Structure — what IntelliJ does and what Wayfarer should do

Draft for review, 2026-11-21. It compares IntelliJ IDEA's Project Structure, SDK table and JDK download with what
Wayfarer has after task 4, and proposes a design to bring Wayfarer close to it. Nothing here is built yet.

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

## 2. Where Wayfarer stands (after task 4)

| Concern | Today | Gap against IntelliJ |
|---|---|---|
| Project JDK | `.wayfarer/project.json` stores an **absolute path** (`ProjectJdk.cs`). Falls back to `JAVA_HOME`. | A path is not portable, so a cloned project loses its JDK. There is no name or version requirement. |
| JDK table | None. Two separate scanners do the same search: `ToolingJvms` (C#) and `ImportJvms.candidates` (Java host). | No registry, no names, no single source of truth. The two lists can disagree. |
| Tooling JVM | Discovered: a setting, then `JAVA_HOME`, `PATH`, install folders; Java 21 or later (`ToolingJvm.cs`). | When none is found, the user must install one themselves. |
| Per-module JDKs | The snapshot records import, compile and runtime `JvmToolchain` (`Home`, `LanguageVersion`, `Vendor`). Gradle toolchains and Maven `release` are read. | A missing toolchain is only a `Problem` string. It offers no resolve or download. |
| JDKs for Gradle and Maven | Wayfarer sets `JAVA_HOME` for the wrapper. It does not tell Gradle or Maven about the other JDKs it knows. | Gradle toolchains and Maven toolchains can't see JDKs Wayfarer found or downloaded. |
| Download | None. The fixtures turn foojay auto-download off on purpose. | Missing entirely. |
| IntelliJ modules | `IdeaEvaluator` reads one `.iml`: source folders, `library` entries and the JDK by name from IntelliJ's `jdk.table.xml`. | No `scope`, no `module` dependencies, no `module-library`, no `$MAVEN_REPOSITORY$`. Repository libraries with missing jars show as `AbsentLibraries` instead of being downloaded. Nothing writes `.iml` or library files. |
| UI | New Project has a JDK text box with Browse. Settings has no JDK page. | No Project Structure dialog, no JDK list, no library editor. |

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

### 3.2 Projects name a requirement, not a path

Change `.wayfarer/project.json` so it can be committed:

```jsonc
{ "schema": 2, "jdk": { "name": "temurin-21", "feature": 21, "vendor": "temurin" } }
```

Resolution, for the project and for each module:

1. **The build's own declaration wins.** A Gradle `toolchain.languageVersion` or `--release`, Maven
   `maven.compiler.release` or `toolchains.xml` requirements, an IntelliJ `jdkName` or `languageLevel`. The snapshot
   already reads these.
2. Otherwise the project's `jdk` requirement.
3. Match against the registry: the exact name, else the same feature and vendor, else the same feature and any vendor
   (newest update first).
4. **No match** → the snapshot records a `JdkRequirement` with no home (no free-text `Problem`). The window shows a
   banner like IntelliJ's: *"Module `api` needs JDK 17 (any vendor). None is installed. [Download Temurin 17]
   [Choose…]"*. Retrying reimports.

A schema 1 file that holds an absolute path still reads. If the folder exists, Wayfarer registers it and rewrites the
file as a requirement on the next save.

Snapshot change: `JvmToolchain` gains `Requirement` (feature, vendor or `null`, name or `null`, and where it came from:
`gradle-toolchain`, `maven-release`, `idea`, `project`) next to the resolved `Home`.

### 3.3 Hand Wayfarer's JDKs to Gradle and Maven

Gradle and Maven should see the same JDKs the IDE shows, so a toolchain request doesn't fail with a JDK on disk:

- **Gradle:** pass `-Porg.gradle.java.installations.paths=<registry homes>` (Tooling API arguments and the wrapper
  command line) and keep `org.gradle.java.installations.auto-download` as the project sets it. Gradle-run toolchains
  then resolve to the registry.
- **Maven:** write a generated `toolchains.xml` under Wayfarer's state, one `<toolchain type="jdk">` per registry JDK,
  and pass it as `--global-toolchains`. The user's `~/.m2/toolchains.xml` still applies on top.

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

### 3.5 Libraries and module dependencies (plain IntelliJ-format projects)

For projects without a build tool, Wayfarer reads and writes IntelliJ's own files. A project edited in one IDE then
opens the same in the other.

- **Reader (host, `IdeaEvaluator`):** honour `scope` (compile/test/runtime/provided into the right classpath), `module`
  entries (another module's output as a `moduleRef`, `exported` passed on), `module-library`, `.idea/modules.xml`
  with several modules, and the `$MAVEN_REPOSITORY$`, `$USER_HOME$` and path-variable macros.
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

## 4. Suggested order

Each step stands alone and comes with tests, following the plan's rule.

1. `JdkRegistry` with one scanner; the client sends JDKs to the host; delete `ImportJvms.candidates`'s own scan.
   Tests: the same JDK list in C# and in the host; reading IntelliJ's `jdk.table.xml`.
2. Project JDK as a requirement (schema 2, schema 1 still read); `JvmToolchain.Requirement`; the missing-JDK banner
   with Choose… and retry.
3. Registry JDKs passed to Gradle (`installations.paths`) and Maven (generated `toolchains.xml`). Test: the per-module
   JDK fixtures resolve with toolchains only in the registry.
4. `JdkDownloader` against foojay, with a fake catalogue server in tests (checksum mismatch, Cancel, cut-off
   download, macOS layout); the Download JDK dialog; tooling-JVM recovery.
5. `IdeaEvaluator`: scopes, module dependencies, several modules, macros, repository-library resolution.
6. Project Structure dialog; library writer for IntelliJ projects.
7. Later: "Add dependency" for Maven and Gradle build files.

## 5. Decisions for the owner

- **Catalogue source:** foojay Disco API (recommended), Adoptium's API only, or JetBrains' feed once its terms are
  cleared.
- **Default vendor** for one-click downloads: Temurin is proposed.
- **Should `.wayfarer/project.json` be committed?** The requirement form makes that safe. If it is shared, a
  per-machine override (an exact home) belongs in Wayfarer's state, not in the file.
- **Download location:** `~/.jdks`, shared with IntelliJ (proposed), or a Wayfarer-only folder.
- **Writing IntelliJ files:** Wayfarer edits `.idea` and `.iml` in place (proposed, which keeps both IDEs in step), or
  keeps its own format for plain projects.
