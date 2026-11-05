using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace VoiceIme;

/// <summary>
/// Raises screen-reader announcements for live regions.
///
/// <para>WPF's <c>AutomationProperties.LiveSetting</c> is only half the
/// mechanism: it marks an element as a live region, but the automation peer
/// does not raise anything on its own. Microsoft's own sample is explicit that
/// a <c>LiveRegionChanged</c> event must be raised in code after the text is
/// set. Without this call the attribute is inert and nothing is ever announced
/// — which is how it was before this helper existed, on every element that
/// declared it.</para>
///
/// <para>Announcements are deliberately restricted to state transitions
/// (recording started/stopped, sending, errors, save outcomes). Announcing a
/// streaming transcript would be constant noise: WCAG 4.1.3 warns explicitly
/// about making an application "too chatty" for screen-reader users.</para>
/// </summary>
internal static class LiveRegion
{
    /// <summary>
    /// Raises a UIA notification event — the "forced announcement" API, for
    /// state that has no live-region element to hang off. This is the correct
    /// tool for the dictation overlay, which is deliberately non-focusable
    /// (<c>ShowActivated="False"</c>), so a screen reader can never reach it by
    /// navigation and a live region there would go unheard.
    ///
    /// <para>Interrupts only when <paramref name="important"/> is set; most
    /// state changes should not cut off what the user is already hearing.</para>
    /// </summary>
    public static void Notify(UIElement? element, string message, bool important = false)
    {
        if (element is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            var peer = UIElementAutomationPeer.FromElement(element)
                       ?? UIElementAutomationPeer.CreatePeerForElement(element);
            if (peer is null)
            {
                return;
            }

            peer.RaiseNotificationEvent(
                important
                    ? AutomationNotificationKind.ActionAborted
                    : AutomationNotificationKind.Other,
                important
                    ? AutomationNotificationProcessing.ImportantMostRecent
                    : AutomationNotificationProcessing.MostRecent,
                message,
                "VoiceIme.Overlay");
        }
        catch
        {
            // Notifications are best-effort; never let one break a state change.
        }
    }

    /// <summary>
    /// Raises <see cref="AutomationEvents.LiveRegionChanged"/> for an element
    /// marked with <c>LiveSetting</c>. No-op when the element has no automation
    /// peer (never realised on screen) or no live setting, so callers may
    /// invoke it unconditionally after any state change. Never throws.
    /// </summary>
    public static void Announce(UIElement? element)
    {
        if (element is null)
        {
            return;
        }

        try
        {
            // FromElement returns null unless a peer already exists; create one
            // so the first update is not silently dropped.
            var peer = UIElementAutomationPeer.FromElement(element)
                       ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch
        {
            // Accessibility plumbing must never take down a state change.
        }
    }
}