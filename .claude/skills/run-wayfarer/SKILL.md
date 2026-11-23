---
name: run-wayfarer
description: Run, launch or screenshot Wayfarer to see a UI change working — a picture of a window, dialog, menu or popup, or the real app opened on a project. Use before reaching for computer-use or starting WayfarerWanderer.App.exe.
---

Two ways to see the app. Take a **headless picture** to check how something looks; open the **real window** only when the
check needs interaction the tests can't drive, or the user asked to see it running.

## Headless picture

Tests render the real `EditorWindow` with Avalonia.Headless, so a picture needs no desktop, no computer-use grant and no
running process to lock the build.

1. Find the test that opens the UI in question (`grep -rn "CreateWindow" tests/WayfarerWanderer.Tests`), or write an
   `[AvaloniaFact]` beside it that opens the window the same way.
2. Capture and save the frame, as `tests/WayfarerWanderer.Tests/EditorWindowTests.cs` does after its clicks:
   `window.CaptureRenderedFrame()`, saved as PNG under `Environment.GetEnvironmentVariable("WAYFARER_TEST_OUTPUT")`.
3. Run only that test with `WAYFARER_TEST_OUTPUT` set to the scratchpad, then Read the PNG.
4. Done when the PNG shows the change. Delete a test you wrote only for the picture before committing.

`tools/first-start-pictures.py` is a different job: it regenerates the committed first-start assets. Run it only after a
change to the shell's look or themes.

## Real window

1. Build the app into its own artifacts path, so the running exe never locks `artifacts/bin` for builds and tests:
   `dotnet build src/WayfarerWanderer.App -c Debug --artifacts-path artifacts/run`. The exe is
   `artifacts/run/bin/WayfarerWanderer.App/debug/WayfarerWanderer.App.exe`.
2. Stage the JVM host once per clone, if `artifacts/jvm-host/host.jar` is missing: `python tools/build-jvm-host.py`. The
   app finds it from any artifacts path by walking up to `WayfarerWanderer.slnx`.
3. Point `WAYFARER_SETTINGS` at a scratchpad folder, so the user's own settings and recent projects stay untouched.
4. Copy a fixture (`tests/fixtures/jvm/maven`, `gradle`, …) into the scratchpad and pass that copy's path as the only
   argument. Opening a project writes into it, and the fixtures are committed.
5. Start the exe in the background. For computer-use, call `request_access` for `WayfarerWanderer.App.exe` first.
6. Stop the process when done; a leftover instance holds its exe and the user's screen.
