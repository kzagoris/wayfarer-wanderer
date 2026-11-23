using Avalonia.Automation;
using Avalonia.Controls;

namespace WayfarerWanderer.App.Shell;

/// <summary>v0.4.2 members of shared helpers another port owns, which this area's copies call, bridged as static extension
/// members so this branch builds alone. A real member always wins over an extension, so once that port lands the bridge
/// goes unused; delete it then.</summary>
internal static class NavigationBridges
{
    extension(ToolButtons)
    {
        /// <summary>v0.4.2's <c>ToolButtons.Tip(button, tip, keys)</c>: the tip naming its keys as this system shows them.</summary>
        public static T Tip<T>(T button, string tip, string? keys)
            where T : Control
        {
            ToolTip.SetTip(button, ActionTips.Named(tip, keys));
            AutomationProperties.SetName(button, tip);
            return button;
        }
    }
}
