using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Passenger.Editor;
using Passenger.Text;
using WayfarerWanderer.App;

[assembly: AvaloniaTestApplication(typeof(WayfarerWanderer.Tests.TestApp))]

namespace WayfarerWanderer.Tests;

public class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<EditorApplication>()
        .UseSkia().UseHarfBuzz().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
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
}
