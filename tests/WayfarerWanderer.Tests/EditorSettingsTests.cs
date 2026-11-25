using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App;
using WayfarerWanderer.App.Shell;
using WayfarerWanderer.App.Shell.Settings;

namespace WayfarerWanderer.Tests;

public sealed class EditorSettingsTests
{
    [AvaloniaFact]
    public async Task TheEditorFollowsSettingsEditorPageAndEachFileKeepsItsOwnWrap()
    {
        string root = Directory.CreateTempSubdirectory("wayfarer-editor-settings-").FullName;
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        try
        {
            string notes = Path.Combine(root, "notes.md"), code = Path.Combine(root, "A.java"), pom = Path.Combine(root, "pom.xml");
            File.WriteAllText(notes, "# Notes\n");
            File.WriteAllText(code, "class A {}\n");
            File.WriteAllText(pom, "<project>\n  <modules>\n    <module>a</module>\n  </modules>\n</project>\n");

            window.SettingsKept.Update(s => s with
            {
                EditorFontSize = 17, EditorLineSpacing = 1.5, ShowLineNumbers = false, RainbowBraces = false, Ligatures = true,
                CloseTags = false, SoftWrapIndent = false, WheelZoom = true, FoldXmlTags = true,
            });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(17, window.Editor.FontSize);
            Assert.Equal(1.5, window.Editor.LineSpacing);
            Assert.False(window.Editor.ShowLineNumbers);
            Assert.False(window.Editor.RainbowBraces);
            Assert.True(window.Editor.Ligatures);
            Assert.False(window.Editor.ClosesTags);
            Assert.False(window.Editor.WrapIndent);
            Assert.True(window.Editor.ZoomsWithWheel);

            // A Markdown file opens wrapped, Java does not; View, Active Editor's Soft-Wrap turns one file's alone.
            window.OpenSource(notes);
            Assert.True(window.Editor.SoftWrap);
            window.OpenSource(code);
            Assert.False(window.Editor.SoftWrap);
            Assert.True(window.Actions.TryExecute(AppActions.SoftWrap));
            Assert.True(window.Editor.SoftWrap);
            window.OpenSource(notes);
            Assert.True(window.Editor.SoftWrap);
            Assert.True(window.Actions.TryExecute(AppActions.SoftWrap));
            Assert.False(window.Editor.SoftWrap);
            window.OpenSource(code);
            Assert.True(window.Editor.SoftWrap);

            // Show Whitespaces in Settings shows it in every open file; the action turns the file shown's.
            window.SettingsKept.Update(s => s with { ShowWhitespaces = true });
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Editor.ShowWhitespaces);
            Assert.True(window.Actions.TryExecute(AppActions.ShowWhitespaces));
            Assert.False(window.Editor.ShowWhitespaces);
            window.OpenSource(notes);
            Assert.True(window.Editor.ShowWhitespaces);

            // The font size's actions change Settings' size, Reset leaving it unset.
            Assert.True(window.Actions.TryExecute(AppActions.IncreaseFontSize));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(18, window.SettingsKept.Value.EditorFontSize);
            Assert.Equal(18, window.Editor.FontSize);
            Assert.True(window.Actions.TryExecute(AppActions.ResetFontSize));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(window.SettingsKept.Value.EditorFontSize);
            Assert.Equal(window.SettingsKept.Value.CodeSize(), window.Editor.FontSize);

            // XML is markup whose tags typing closes, and its tags fold as it opens while Code Folding says so.
            window.OpenSource(pom);
            Assert.Equal(MarkupKind.Xml, window.Editor.Markup);
            Stopwatch waited = Stopwatch.StartNew();
            while (window.Editor.Decorations.FoldRegions.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.NotEmpty(window.Editor.Decorations.FoldRegions);
            Assert.All(window.Editor.Decorations.FoldRegions.Where(r => r.Kind == FoldKind.Tag), r => Assert.True(r.Folded));
            window.OpenSource(code);
            Assert.Equal(MarkupKind.None, window.Editor.Markup);
        }
        finally
        {
            window.SettingsKept.Update(_ => new AppSettings());
            window.Close();
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void SettingsHasTheTerminalsFontAndCursorAndRefusesAShellNamingNoProgram()
    {
        var window = EditorApplication.CreateWindow(null);
        window.Show();
        SettingsDialog dialog = window.ShowSettings();
        try
        {
            dialog.Page = SettingsDialog.TerminalPage;
            Assert.Equal(["Font size", "Font family", "Cursor style", "Cursor blinking", "Copy on selection", "Shell path"], dialog.Shown);
            dialog.Page = SettingsDialog.EditorPage;
            Assert.Contains("Soft-wrap these files", dialog.Shown);
            Assert.Contains("Font size", dialog.Shown);

            dialog.Query = "cursor blink";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([SettingsDialog.TerminalPage], dialog.Pages);

            dialog.Change(s => s with { TerminalShell = "\"\" -l" });
            Assert.NotNull(dialog.Refused);
            dialog.Change(s => s with { TerminalShell = null, TerminalCursorStyle = Passenger.Terminal.CursorShape.Underline, TerminalFontSize = 15 });
            Assert.Null(dialog.Refused);
            dialog.Ok();
            Assert.Equal(Passenger.Terminal.CursorShape.Underline, window.SettingsKept.Value.TerminalCursorStyle);
            Assert.Equal(15, window.SettingsKept.Value.TerminalFontSize);
        }
        finally
        {
            dialog.Close();
            window.SettingsKept.Update(_ => new AppSettings());
            window.Close();
        }
    }
}
