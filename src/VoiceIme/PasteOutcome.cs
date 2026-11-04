using System;

namespace VoiceIme;

/// <summary>
/// Why a transcript could not be delivered to the focused window.
///
/// These are distinct on purpose. The previous implementation reported every
/// failure as a single sentence — "Couldn't paste into the focused window" —
/// which told a user whose target app runs elevated exactly as much as it told
/// one whose clipboard was momentarily locked: nothing actionable.
/// </summary>
public enum PasteFailureReason
{
    /// <summary>Delivery succeeded.</summary>
    None,

    /// <summary>The clipboard could not be opened (another process holds it).</summary>
    ClipboardBusy,

    /// <summary>
    /// The focused window belongs to a process at a higher integrity level.
    /// Windows blocks synthesized input from a lower-integrity process (UIPI),
    /// and no amount of retrying from this app can cross that boundary.
    /// </summary>
    TargetElevated,

    /// <summary>There is no foreground window to receive the chord.</summary>
    NoForegroundWindow,

    /// <summary>
    /// The focused window belongs to Voice IME itself, so the chord would be
    /// delivered to the wrong place (or nowhere).
    /// </summary>
    FocusLost,

    /// <summary>The system rejected the synthesized keystroke for an unknown reason.</summary>
    InputRejected,
}

/// <summary>
/// The result of one paste attempt: whether it landed, and if not, why.
/// </summary>
/// <param name="Delivered">True when the chord was accepted by the OS.</param>
/// <param name="Reason">The classified cause; <see cref="PasteFailureReason.None"/> on success.</param>
/// <param name="Diagnostic">
/// Short technical detail for the log. Never shown to the user — it may name
/// an error code, and user-facing copy is produced only by
/// <see cref="PasteDiagnosis.MessageFor"/>.
/// </param>
public sealed record PasteOutcome(bool Delivered, PasteFailureReason Reason, string Diagnostic = "")
{
    public static PasteOutcome Success { get; } = new(true, PasteFailureReason.None);

    public static PasteOutcome Failure(PasteFailureReason reason, string diagnostic = "") =>
        new(false, reason, diagnostic);
}

/// <summary>
/// Maps a <see cref="PasteFailureReason"/> to what the user should read.
///
/// Kept as pure functions with no Win32 dependency so the copy is unit-testable
/// on any OS — the same discipline as <see cref="DictationError"/>. The rule
/// from the error-message guidance: state what happened, in the user's terms,
/// and offer the next step — never blame the user, never expose an error code.
/// </summary>
public static class PasteDiagnosis
{
    /// <summary>
    /// One-line explanation of the failure, phrased as the cause rather than
    /// as a generic "couldn't paste".
    /// </summary>
    public static string MessageFor(PasteFailureReason reason) => reason switch
    {
        PasteFailureReason.TargetElevated =>
            "Couldn't paste — that app runs as administrator and Windows blocks input from other apps",

        PasteFailureReason.ClipboardBusy =>
            "Couldn't paste — another app was using the clipboard",

        PasteFailureReason.NoForegroundWindow =>
            "Couldn't paste — no window was selected",

        PasteFailureReason.FocusLost =>
            "Couldn't paste — Voice IME had focus instead of your app",

        PasteFailureReason.InputRejected =>
            "Couldn't paste — the focused app didn't accept the keystroke",

        _ => "Couldn't paste",
    };

    /// <summary>
    /// The recovery step, or null when there is nothing useful to suggest.
    /// Elevation is the one cause worth explaining at length, because the
    /// obvious "try again" would never work.
    /// </summary>
    public static string? RecoveryFor(PasteFailureReason reason) => reason switch
    {
        PasteFailureReason.TargetElevated =>
            "The transcript is on your clipboard and in History. " +
            "Paste it yourself with Ctrl+V, or run Voice IME as administrator to match.",

        PasteFailureReason.ClipboardBusy =>
            "The transcript is on your clipboard and in History — paste it with Ctrl+V.",

        PasteFailureReason.FocusLost =>
            "Click into the text field you want, then use History to copy it.",

        PasteFailureReason.NoForegroundWindow =>
            "Click into the text field you want, then use History to copy it.",

        PasteFailureReason.InputRejected =>
            "The transcript is on your clipboard and in History — paste it with Ctrl+V.",

        _ => null,
    };

    /// <summary>
    /// True when retrying the same paste could plausibly succeed. Elevation
    /// never can (the boundary is static), and neither can "app had focus" —
    /// both need the user to change something first.
    /// </summary>
    public static bool IsRetryable(PasteFailureReason reason) => reason switch
    {
        PasteFailureReason.ClipboardBusy => true,
        PasteFailureReason.InputRejected => true,
        _ => false,
    };

    /// <summary>
    /// Classifies a short <c>SendInput</c> by its Win32 error code.
    ///
    /// <para>Note there is deliberately no ERROR_ACCESS_DENIED → elevated
    /// mapping. UIPI does <em>not</em> surface through SendInput: the call
    /// reports the full count with <c>GetLastError</c> == 0 and the events are
    /// dropped later, silently (documented on the SendInput reference page).
    /// Elevation is therefore detected in advance by comparing integrity
    /// levels, never from this error.</para>
    /// </summary>
    public static PasteFailureReason FromSendInputError(int win32Error) =>
        PasteFailureReason.InputRejected;
}

/// <summary>
/// Paste/clipboard delivery failed. Carries the classified reason so the
/// message and the suggested recovery match the actual cause.
///
/// The transcript is always in History (the store is written before the paste
/// is attempted), so every variant can point there honestly.
/// </summary>
public sealed record PasteError : IDictationError
{
    /// <summary>Defaults to <see cref="PasteFailureReason.InputRejected"/> for callers that cannot classify.</summary>
    public PasteError(PasteFailureReason reason = PasteFailureReason.InputRejected)
    {
        Reason = reason;
    }

    public PasteFailureReason Reason { get; }

    public string UserMessage => PasteDiagnosis.MessageFor(Reason);

    /// <summary>The next step to show alongside the message, or null.</summary>
    public string? Recovery => PasteDiagnosis.RecoveryFor(Reason);

    /// <summary>Whether retrying unchanged could succeed.</summary>
    public bool IsRetryable => PasteDiagnosis.IsRetryable(Reason);
}
