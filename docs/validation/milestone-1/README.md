# Milestone 1 verification

## Passenger pin comparison

The real editor received `4`, Shift+Backslash, and `j` on the two lines `ab` and `cdefgh`. Its caret started on `a`. Coordinates in the assertions are zero-based.

| Passenger revision | Wayfarer checkout | Observed result |
| --- | --- | --- |
| Parent `47336cec63a958b8daaba3b69b288e452a68fc43` | `a9b938eadf247284e819ebe037a8686b8bdb513e` | 1 executed, 1 failed at the caret assertion. Expected (1, 3), observed (1, 1), on `d`. [Parent TRX](parent.trx) |
| Fix `f69cf8492c279cc7ab639b7f12c26e5723d07274` | `29df1f61e9c538f52be23e96be039e1e8ca59045` | 1 executed, 1 passed at (1, 3), on `f`. The full historical host suite passed 7/7. [Fixed TRX](fixed.trx) |
| Product `2044e49b52742eea87acbf6a5901a108d2778719` | `8b313998fdc84b0625c323ea1635d024a73551ca` | 8/8 passed, including the same caret and character assertions, keyboard undo, paste, both themes and window actions. [Product TRX](product/suite.trx) |

In the disposable clone, `git rev-parse f69cf8492^` returned the parent above. `git merge-base --is-ancestor f69cf8492 2044e49b5` exited zero. The upstream fix changed two Vim source files and its unit test. The two historical Wayfarer commits differ only in the mode-160000 gitlink at `vendor/passenger`. Both locked restores used identical app and test locks. The regression source has SHA-256 `e9b4b0c96053f4ad7c88132592615c8b597f43cf3c73f855b5f176f1fa67cf64` in all three checkouts.

The historical host uses the older one-argument font registration API and captures the caret immediately after moving it. The product pin supports a steady caret and configurable width. The SDK and NuGet configuration match across pins. The product pin advanced Passenger's assembly version from 0.3.4 to 0.4.0 and changed project files, locks, editor key generation and theme inputs. The historical host locks were generated at the parent.

To replay in a disposable clone of this branch, import the bundled rehearsal history from the repository root. Run the fetch once. Then repeat the checkout and test commands, setting `revision` to the parent rehearsal and then the fixed rehearsal in the table. The parent test command is expected to exit 1.

```sh
git fetch docs/validation/milestone-1/rehearsal.bundle historical-pin:refs/heads/replay-pin
git switch --detach "$revision"
git submodule update --init --recursive
dotnet restore --locked-mode --configfile vendor/passenger/nuget.config
dotnet build --no-restore
dotnet test tests/WayfarerWanderer.Tests --no-build --filter FullyQualifiedName~EditorPinUpgradeTests \
  --logger "trx;LogFileName=$revision.trx" --results-directory artifacts/test-results
```

## Upstream verification at the fix

| Run | Passed | Named observation |
| --- | ---: | --- |
| Passenger.UnitTests.Editor.VimTests | 1,348 | All seven rows of `Bar_keeps_its_column_for_j_and_k_and_an_empty_block_still_takes_its_lines` passed, including the exact `4\|j` row. |
| just test ActiveEditorTests | 8 | A breakpoint and a fold stayed on their lines with wrapped rows above them. |
| just test EditingBehaviourTests | 16 | A typed word was undone and redone in one change each. |

The two `just` invocations also inspected the unit-test assembly, where those two window classes have zero matches. A separate named TRX from Passenger.Tests verified all 24 window cases with zero failures and zero skips. The counts above count each case once.

## Host and publish verification

The final recursive clone used a new directory with no adjacent Passenger checkout. Both restore steps used the pinned NuGet configuration and locked mode. The first final-clone build stopped before emitting a diagnostic. A retry with build servers disabled and one MSBuild node completed with zero warnings and zero errors. The first host negative control failed on the missing editor. Before the runtime dictionaries were linked, both theme cases failed on the background lookup. An unmatched VSTest filter executed zero cases and the TRX checker exited 1. The later paste test reproduced the missing dispatcher registration through the real clipboard shortcut before the host fix.

Both light and dark renders show a two-line document, the caret and a breakpoint beside line two. Pixel checks covered background, text, caret, gutter and marker. The product Linux executable also opened in omabox with Xwayland, accepted typing and drew a marker after F9. [Light](product/editor-light.png) · [Dark](product/editor-dark.png) · [Desktop](desktop.png)

The inventories record the absolute self-contained Linux x64 publish path, relative file names, dependency libraries, runtime assets and forbidden matches. The product inventory counted 239 files, 23 libraries, 229 runtime assets and zero forbidden matches. The historical publish counted 238 files with the same library and asset totals. The extra product file is the JetBrains Mono license. The product publish includes Passenger.Core, Editor and Text and the diagnostics client. The source generators run at build time. [Historical inventory](fixed/publish.json) · [Product inventory](product/publish.json)

Verification used Omarchy 4.5.0 on Linux x64, .NET SDK 11.0.100, runtime 11.0.0, MSBuild 18.12.2, Avalonia 12.1.6, Microsoft.NET.Test.Sdk 18.11.0, xunit.v3 3.2.2, xunit.runner.visualstudio 4.0.1, just 1.59.0, Python 3.14.8 and Git 2.56.0. Native Windows and macOS runtime checks remain open.

The archive contains fresh TRX files, command logs, the SDK inventory and the comparison of Passenger build inputs. [Verification archive](verification.tar.gz)

## Implementation commits

| Wayfarer commit | Change |
| --- | --- |
| `8ecf35f` | Recursive dependency, independent locked build, named edit-and-undo check |
| `b2c6811` | Linked theme resources, per-window breakpoint actions, rendered and publish checks |
| `8271dce` | Host regression for the upstream Vim fix |
| `8b31399` | Dispatcher registration and paste regression, bundled editor font license |
