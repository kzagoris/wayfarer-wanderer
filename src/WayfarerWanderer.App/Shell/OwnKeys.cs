// From Passenger 8a1df7e4 src/Passenger.App/IdeKeys.cs, MIT (notices/Passenger/LICENSE): its IOwnKeys alone.
using Avalonia.Input;

namespace WayfarerWanderer.App.Shell;

/// <summary>A part of a window that takes some keys ahead of the IDE's keymap, as the first start's page takes every key:
/// the window leaves a key alone when the element with the keys, or one it is inside, says it takes it.</summary>
public interface IOwnKeys
{
    bool TakesKey(KeyEventArgs e);
}
