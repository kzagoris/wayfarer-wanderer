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
the user's Gradle and Maven caches and the Gradle daemon left warm. The servers
were JDT LS 1.57.0 (redhat.java 1.53.0) and the JetBrains Kotlin server 0.0.12.

| | Gradle | Maven |
| --- | --- | --- |
| Cold import (static build-file import, no wrapper) | 17 ms | 21 ms |
| Warm reopen | 3 ms | 3 ms |
| Java completion, first useful result | 26.2 s (indexing) | not obtained |
| Java completion, warm median of 5 | 16 ms | — |
| Kotlin completion | server refused to start | server refused to start |
| Peak working set, language-server processes | 1,650 MiB | 1,172 MiB |

The Kotlin server 0.0.12 exits at start with "Please download a new build".
This build has expired, and the Kotlin numbers need a current kotlin-lsp. On
Maven, JDT LS did not offer the Kotlin top-level `message` through `GreetingKt`
from Java. The Kotlin library is not compiled into JDT LS's view, so
Java-to-Kotlin completion on Maven depends on the Kotlin side of the semantic
backend. The memory peak covers JDT LS, the launcher of the expired Kotlin
server and a Gradle process that JDT LS started on Gradle.
Do not take these numbers as the both-servers budget until the Kotlin server
runs. Under the plan, the release integrations remain unselected until then.

## Kotlin server distribution terms

Checked 2026-11-19 against the GitHub repositories and the installed build.

**JetBrains Kotlin server ([Kotlin/kotlin-lsp](https://github.com/Kotlin/kotlin-lsp)).**
It is free to download and use. Its README describes it as Alpha and built on
IntelliJ IDEA and "proprietary parts of JetBrains Air and Fleet products",
which makes it partly closed source. The repository's `LICENSE.txt` is
Apache 2.0, but that can cover only the published parts. The standalone build
271.2085.0 has a `license/` folder for third-party components such as YourKit
and Javolution. Nothing in it licenses JetBrains' own code. Release
v262.7569.0 (2026-06-09) moved the VS Code extension from Apache 2.0 to the
JetBrains Free Plugin License. No published terms permit bundling the server
with Wayfarer.

**fwcd Kotlin language server ([fwcd/kotlin-language-server](https://github.com/fwcd/kotlin-language-server)).**
It is MIT-licensed and could be redistributed. Its README now says that with
an official server available, "this project can be considered deprecated".
The last release is 1.3.13 (2025-01-18), and nothing has been pushed since
2025-06-02. It is not a candidate for the mixed-language fixtures above.

**Decision.** Wayfarer does not ship a Kotlin server. Users install the
JetBrains build themselves. Wayfarer looks for it in this order:
`WAYFARER_KOTLIN_LSP_HOME`, then the newest version under
`~/.local/share/wayfarer/kotlin-lsp/<version>/`, then the VS Code extension.
A first-run download straight from JetBrains, with the user's consent, would
also avoid redistribution. Bundling needs written redistribution terms from
JetBrains (see `docs/initial-spec.md`, Language backends).
