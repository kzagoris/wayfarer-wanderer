# Implementation handoff

Continue from the [Milestone 2 handoff](milestone-2-handoff.md). The original Milestone 1 brief follows.

Implement Wayfarer Wanderer, a standalone .NET and Avalonia IDE for Java and Kotlin/JVM that shares components with Passenger. Start in `/home/kzagoris/Projects/wayfarer-wanderer`. The GitHub repository is https://github.com/kzagoris/wayfarer-wanderer. The existing Passenger checkout is at `/home/kzagoris/Projects/passenger`.

Read these first:

- [Product specification](initial-spec.md) at `docs/initial-spec.md`
- [Implementation plan](superpowers/plans/2026-11-18-wayfarer-wanderer-implementation.md) at `docs/superpowers/plans/2026-11-18-wayfarer-wanderer-implementation.md`
- Passenger’s local development guide at `/home/kzagoris/Projects/passenger/CLAUDE.md`

The implementation plan incorporates a Claude Code CLI review with Opus 5.5 at high effort. The spec and plan exist and the Passenger revision history has been checked. Runtime validation is still open. Inspect the worktree before editing and preserve any work already present.

Begin implementation now. Use `superpowers:executing-plans` and work through Milestone 1, tasks 1–3, in order. Use `omabox` for desktop checks on this workstation.

1. Build Wayfarer Wanderer from a fresh recursive clone with locked dependencies and no adjacent Passenger checkout. Run a named headless test that types into the real Passenger editor, undoes the edit and verifies the original text and caret.
2. Render the editor in light and dark themes, verify the expected resources and toggle a breakpoint on the intended line. Inspect the rendered output and published app for accidental C# language-server or debugger payloads.
3. Run the same editor regression at the parent and fixed Passenger pins. For the plan’s `4|j` case, observe the caret assertion fail before the fix and pass after changing only the pin. Verify the affected Passenger tests and repeat Wayfarer Wanderer’s checks at the product pin.

Run the historical comparison in a disposable clone. Keep the existing sibling Passenger checkout intact. For filtered tests, verify that the named cases ran. Diagnose failures and continue where possible.

At the milestone boundary, report commits, exact revisions tested, test counts, paths to screenshots and publish evidence, and any unresolved blocker. Leave JVM backend selection for Milestone 2.
