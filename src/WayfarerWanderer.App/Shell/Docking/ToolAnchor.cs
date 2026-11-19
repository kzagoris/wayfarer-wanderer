// From Passenger 2044e49b src/Passenger.App/Docking/ToolAnchor.cs, MIT (notices/Passenger/LICENSE).
namespace WayfarerWanderer.App.Shell.Docking;

/// <summary>Where a tool window docks: each side's upper or lower half, the bottom's left or right half. A place shows
/// one of its windows at a time.</summary>
public enum ToolAnchor
{
    LeftTop,
    LeftBottom,
    RightTop,
    RightBottom,
    BottomLeft,
    BottomRight,
}
