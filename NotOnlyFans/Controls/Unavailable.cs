using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NotOnlyFans.Controls;

/// <summary>
/// Something this PC can't do stays in its place, greyed, and says why on hover ("Unavailable" over the reason)
/// instead of disappearing. A disabled control is not hit-tested and shows no tooltip of its own, so the reason
/// sits on its host: a <see cref="Border"/> with a transparent background right around it (the pointer falls
/// through the disabled control onto the host).
/// </summary>
public static class Unavailable
{
    /// <summary>Tooltip text: "Unavailable" over the reason (a full sentence).</summary>
    public static string Tip(string reason) => "Unavailable\n" + reason;

    /// <summary>
    /// <paramref name="reason"/> null: the control is enabled when <paramref name="enabled"/> (other, passing reasons
    /// such as "busy" disable it without a tooltip). Else greyed with the reason on its host.
    /// </summary>
    public static void Set(Control control, string? reason, bool enabled = true)
    {
        bool on = reason is null && enabled;
        if (control.IsEnabled != on)
        {
            control.IsEnabled = on;
        }
        string? tip = reason is null ? null : Tip(reason);
        if (control.Parent is Border host && !Equals(ToolTipService.GetToolTip(host), tip))
        {
            ToolTipService.SetToolTip(host, tip);
        }
        AutomationProperties.SetHelpText(control, reason is null ? "" : "Unavailable. " + reason);
    }

    /// <summary>A whole card (it has its own background, so it is the host): its controls greyed, the reason on the card.</summary>
    public static void SetCard(Border card, string? reason, params Control[] controls)
    {
        foreach (var control in controls)
        {
            if (control.IsEnabled != (reason is null))
            {
                control.IsEnabled = reason is null;
            }
            AutomationProperties.SetHelpText(control, reason is null ? "" : "Unavailable. " + reason);
        }
        string? tip = reason is null ? null : Tip(reason);
        if (!Equals(ToolTipService.GetToolTip(card), tip))
        {
            ToolTipService.SetToolTip(card, tip);
        }
    }

    /// <summary>
    /// For something that stays clickable (a page in the sidebar: it opens and explains more): looks greyed,
    /// tells the reason on hover. <paramref name="normalTip"/> is its tooltip when available.
    /// </summary>
    public static void Mark(FrameworkElement element, string? reason, string? normalTip = null)
    {
        double opacity = reason is null ? 1 : 0.45;
        if (element.Opacity != opacity)
        {
            element.Opacity = opacity;
        }
        string? tip = reason is null ? normalTip : Tip(reason);
        if (!Equals(ToolTipService.GetToolTip(element), tip))
        {
            ToolTipService.SetToolTip(element, tip);
        }
        AutomationProperties.SetHelpText(element, reason is null ? "" : "Unavailable. " + reason);
    }
}
