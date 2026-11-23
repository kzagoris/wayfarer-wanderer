# Milestone 2 handoff

Start with the `milestone-1` branch of Wayfarer Wanderer. The verified app and test baseline is `8b31399`. The gitlink at `vendor/passenger` selects `2044e49b52742eea87acbf6a5901a108d2778719`. The existing workstation worktree is `/home/kzagoris/Projects/wayfarer-wanderer/.worktrees/milestone-1`.

## Establish the baseline

Read the [Milestone 1 verification record](../validation/milestone-1/README.md), the [product specification](../initial-spec.md), and tasks 4–6 of the [implementation plan](../superpowers/plans/2026-11-18-wayfarer-wanderer-implementation.md). The [README](../../README.md) gives the locked Debug build and self-contained publish commands. These commands create a fresh checkout of the local branch on this workstation.

```sh
git clone --branch milestone-1 --recurse-submodules /home/kzagoris/Projects/wayfarer-wanderer wayfarer-m2
cd wayfarer-m2
git switch -c milestone-2
export PATH="$(mise where dotnet@11.0.100):$PATH"
dotnet restore WayfarerWanderer.slnx --locked-mode --configfile vendor/passenger/nuget.config
dotnet build WayfarerWanderer.slnx -c Debug --no-restore
dotnet test tests/WayfarerWanderer.Tests -c Debug --no-build
```

Expect eight passing tests. EditorWindow owns its document, actions and breakpoint marker. EditorApplication installs Passenger's font fallback and UI dispatcher for clipboard work. Keep those paths in the product and test entry points. Use omabox with `--xwayland` for native desktop checks on this workstation.

## First executable slice

Start task 4 with wrapper-buildable Maven and Gradle multi-module fixtures under `tests/fixtures/jvm/`. Include Java calling Kotlin and Kotlin calling Java, both Gradle DSLs, generated sources and distinct import/runtime JDKs. Include Spring Boot fixtures for each build tool. Commit expected module identities, roots and classpaths alongside the inputs. Build the fixtures with their wrappers independently to establish expected outputs.

Write the trust test before wiring the project host. Open an untrusted fixture, edit a buffer, and assert zero launches using both Passenger's child-process observation and a marker inside the fixture's wrapper or import entry. Then demonstrate explicit trust, a successful import, and missing-JDK recovery. Give each window its own ProjectSession. Keep JVM tooling in owned child processes and tag requests with session, operation and import generation.

## Evidence required for backend selection

| Task | Demonstration |
| --- | --- |
| 4 · Project model | Compare each wrapper's evaluated source sets, generated roots, ordered classpaths, JDKs and tasks with the model served to the IDE. Add and remove a dependency, fail and repair an import, then switch workspace with a reply in flight. Verify unsaved buffers survive and stale replies are discarded. |
| 5 · Mixed-language editing | On both build systems, verify definition and references in both directions, completion, diagnostics and formatting. Preview one cross-language rename against a known reference manifest, apply, compile, test and undo. Inject stale document versions and a mid-apply failure and verify atomic recovery. |
| 6 · Launch and debug | Run a Kotlin main, bind a Kotlin breakpoint, step into Java, inspect locals and stop. Exercise a Boot HTTP handler on both tools. Demonstrate that stopping releases the service port, permits a restart, and preserves an unrelated JVM and the shared build daemon. |

Evaluate JDT LS, the standalone Kotlin language server and the Java debug adapter against these observations. Record exact versions, effective classpaths and JDKs, distribution terms, process ownership, import and completion timings, and memory measurements in `docs/validation/jvm-backends.md`. Select the JVM backends after the fixtures establish the required behavior. If mixed-language symbol coverage or Kotlin debugging remains incomplete, record the failing fixture and bring the release-scope decision to the owner before moving to Milestone 3.
