# Release validation

The first release is driven by `tools/acceptance.py`. The runner executes named
selectors for the shared editor, import, rename, debug, existing projects, new
projects and recovery. It writes a TRX per selector, a log per selector and a
structured `results.json`; any absent, failed or unexpectedly skipped case
returns a nonzero exit status.

```sh
python3 tools/acceptance.py --results artifacts/test-results/acceptance
python3 tools/acceptance.py --only new-projects --app /path/to/installed/WayfarerWanderer
```

Release packaging is staged from a locked build and must be exercised from the
installed location. Windows uses a per-user Velopack installer, macOS uses an
application bundle in a DMG, and Linux uses a tar archive with an install
script. Record signing status and verify signatures/notarization where
credentials are available; credentials must never enter this runner.

The package inventory includes the host, native Avalonia/Skia/HarfBuzz assets,
theme resources, wrappers/templates, any project-host runtime, semantic server
and debug adapter. Accidental Passenger application/server payloads are
excluded. Every platform report records OS/RID, CPU and memory hardware, SDK and
JDK versions, build-tool versions, cold import, warm reopen, completion latency,
peak memory, executed cases, exclusions and failure logs.

Platform results are added only after the installed application executes the
corresponding selector. The release gate treats a requested selector with no
executed tests as a failure.

## Recorded runs

| Target | Observation | Status |
| --- | --- | --- |
| `win-x64` self-contained publish | Locked restore, publish and payload inventory: 286 files, 28 libraries, 235 runtime assets and zero forbidden C# server/debugger payloads. | Verified 2026-11-18 |
| `win-x64` Velopack package | Built `WayfarerWanderer-win-Setup.exe` and portable archive. The package is unsigned because no signing credentials were supplied. | Verified 2026-11-18 |
| `win-x64` portable install | Extracted portable package executed shared-editor acceptance and the normal headless application mode; 9 editor cases executed and no failures. | Verified 2026-11-18 |
| Maven + Gradle starters | All eight combinations generated, built, ran their JUnit test, launched, and stopped; Boot starters answered their HTTP handler on an ephemeral port. | Verified 2026-11-18 |
| Regression suite | Locked Debug restore and full host/JVM suite: 63 executed, 0 failed. | Verified 2026-11-18 |

macOS, Windows ARM64, Linux and Linux musl remain unverified on this
workstation; their packaging commands are implemented but must be executed on
matching targets before those targets can be marked supported.
