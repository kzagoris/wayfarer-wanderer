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
| Cold import (static build-file import, no wrapper) | 32 ms | 5 ms |
| Warm reopen | 0 ms | 0 ms |
| Kotlin classes handed to JDT LS (wrapper compile and attach) | not needed | 13.1 s |
| Java completion of a Kotlin member, first useful result | 4.4 s | 0.4 s after the hand-over |
| Java completion, warm median of 5 | 27 ms | 13 ms |
| Kotlin completion of a Java member, first useful result | 8.2 s | 9.4 s |
| Kotlin completion, warm median of 5 | 258 ms | 235 ms |
| Peak working set, both language servers | 2,234 MiB | 2,655 MiB |

Earlier runs the same day, before the Maven hand-over existed, gave similar
Gradle and Kotlin figures and memory peaks between 2,307 and 2,860 MiB.

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
is to support both. The open backend above remains the default, and a setting
selects the IntelliJ server for developers with a JetBrains subscription. See
"Language backend setting" in the specification and task 10 of the
implementation plan.
