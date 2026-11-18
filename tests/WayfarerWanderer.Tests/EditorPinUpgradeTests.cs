using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Passenger.Text;
using WayfarerWanderer.App;

namespace WayfarerWanderer.Tests;

public class EditorPinUpgradeTests
{
    [AvaloniaFact]
    public void BarRemembersRequestedColumnOnNextLine()
    {
        var document = new TextDocument("ab\ncdefgh");
        var window = EditorApplication.CreateWindow(document);
        window.Show();
        try
        {
            var editor = window.Editor;
            editor.UsesVim = true;
            editor.GoTo(new TextPosition(0, 0));
            Assert.True(editor.Focus());
            window.KeyPress(Key.D4, RawInputModifiers.None, PhysicalKey.Digit4, "4");
            window.KeyPress(Key.OemPipe, RawInputModifiers.Shift, PhysicalKey.Backslash, "|");
            window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            Assert.Equal(new TextPosition(1, 3), editor.CaretPosition);
            Assert.Equal('f', document.Snapshot[editor.Selections.Primary.Caret]);
        }
        finally
        {
            window.Close();
        }
    }
}
