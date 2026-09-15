using System;

namespace VoiceIme;

/// <summary>
/// Tray-guard decision — App enforces it. Hiding the icon while the window is
/// hidden would strand the app invisible, so that combination forces the
/// shell up instead.
/// </summary>
public enum TrayGuardAction
{
    None,
    ShowWindow,
}

/// <summary>
/// Tray tooltip text per dictation state plus the never-strand-invisible
/// guard. Pure (tooltips truncate to the WinForms 63-char cap via
/// <see cref="App.TruncateTrayText"/>) — unit-testable on any OS; App only
/// applies the strings and the guard decision.
/// </summary>
public static class TrayStateText
{
    /// <summary>
    /// Tooltip for a dictation state: idle reuses the hotkey tooltip, busy
    /// states report what is happening, error carries the safe user message.
    /// Never throws; overlong text truncates to the tooltip cap.
    /// </summary>
    public static string TooltipFor(DictationState state, string hotkey, string? detail = null) =>
        state switch
        {
            DictationState.Recording => App.TruncateTrayText("Voice IME — recording… tap hotkey to stop"),
            DictationState.Uploading => App.TruncateTrayText("Voice IME — transcribing…"),
            DictationState.Error => App.TruncateTrayText(
                string.IsNullOrWhiteSpace(detail)
                    ? "Voice IME — error"
                    : $"Voice IME — {detail.Trim()}"),
            _ => App.TrayText(hotkey),
        };

    /// <summary>
    /// Icon off + window hidden strands the app invisible — that combination
    /// resolves to showing the window; every other combination needs nothing.
    /// </summary>
    public static TrayGuardAction ResolveTrayGuard(bool showTrayIcon, bool windowVisible) =>
        (!showTrayIcon && !windowVisible) ? TrayGuardAction.ShowWindow : TrayGuardAction.None;
}
