using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.Platform;
using Passenger.Text;
using WayfarerWanderer.App;

[assembly: AvaloniaTestApplication(typeof(WayfarerWanderer.Tests.TestApp))]

namespace WayfarerWanderer.Tests;

public class TestApp
{
    /// <summary>As the app's Main does first, before any test reads the program's path, so what the windows say and the
    /// shared libraries' messages are Wayfarer's.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Identify() => EditorApplication.Identify();

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<EditorApplication>()
        .UseSkia().UseHarfBuzz().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

public class EditorHostTests
{
    [AvaloniaFact]
    public void TypingThenUndoRestoresTextAndCaret()
    {
        var document = new TextDocument("class Main");
        var window = EditorApplication.CreateWindow(document);
        window.Show();
        try
        {
            var editor = window.Editor;
            Assert.True(editor.Focus());
            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            var before = editor.CaretPosition;
            Assert.Equal(new TextPosition(0, 10), before);
            window.KeyTextInput(" {}");
            Assert.Equal("class Main {}", document.Snapshot.ToString());
            Assert.Equal(new TextPosition(0, 13), editor.CaretPosition);
            window.KeyPress(Key.Z, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control,
                PhysicalKey.Z, "z");
            Assert.Equal("class Main", document.Snapshot.ToString());
            Assert.Equal(before, editor.CaretPosition);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PasteShortcutUsesWindowClipboard()
    {
        var document = new TextDocument();
        var window = EditorApplication.CreateWindow(document);
        window.Show();
        try
        {
            Assert.True(window.Editor.Focus());
            await window.Clipboard!.SetTextAsync("pasted");
            window.KeyPress(Key.V, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control,
                PhysicalKey.V, "v");
            Assert.Equal("pasted", document.Snapshot.ToString());
        }
        finally
        {
            window.Close();
        }
    }
}
