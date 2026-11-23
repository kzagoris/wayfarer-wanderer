using WayfarerWanderer.App.Shell;

namespace WayfarerWanderer.App;

/// <summary>What the window reads of the system for the person's eyes and ears, again as it comes in front: whether the
/// system asks for high contrast (<see cref="SystemContrast"/>) and whether a screen reader runs, which the sound cues
/// play while (<see cref="ScreenReaders"/>).</summary>
public sealed partial class EditorWindow
{
    private void AddAccessibility()
    {
        Activated += (_, _) =>
        {
            SystemContrast.LookAgain();
            if (SystemContrast.Seen)
            {
                ScreenReaders.LookAgain();
            }
        };
        AddSoundCues();
    }
}
