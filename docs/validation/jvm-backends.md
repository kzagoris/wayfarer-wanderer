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

Semantic-language and debugger backend selection remains open. The checked-in
session has conservative JVM process/import and debug-adapter boundaries, but
it does **not** claim JDT LS/Kotlin language-server parity or a Java DAP backend
has passed the cross-language completion, reference, atomic rename, Kotlin
breakpoint, step-into-Java and local-inspection matrix. Under the release plan,
those are blocking prerequisites for a supported semantic/debug release.
