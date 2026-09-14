using System;
using System.Collections.Generic;

namespace VoiceIme;

/// <summary>
/// Severity of a user-visible notification; App maps it to a balloon icon.
/// Lives here (not WinForms) so Notifier stays free of platform assemblies.
/// </summary>
public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Single host for all user-visible messages (tray balloon tips). Pure sink —
/// no WinForms/WPF types — so formatting and secret redaction are
/// unit-testable on any OS. App wires the sink to NotifyIcon.ShowBalloonTip;
/// nothing else may show balloons or message boxes to the user.
/// </summary>
public sealed class Notifier
{
    public const string RedactedPlaceholder = "[redacted]";

    /// <summary>
    /// Secrets shorter than this are ignored — redacting a 1-3 char string
    /// would wipe innocent substrings out of every message.
    /// </summary>
    public const int MinSecretLength = 4;

    private readonly Action<string, string, NotificationSeverity> _sink;

    public Notifier(Action<string, string, NotificationSeverity> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    public void Notify(
        string title,
        string body,
        NotificationSeverity severity = NotificationSeverity.Info,
        IEnumerable<string>? secrets = null) =>
        _sink(title, Redact(body, secrets), severity);

    /// <summary>
    /// Strips every known secret from a message before it reaches the user.
    /// Returns the message unchanged when there is nothing to redact.
    /// </summary>
    public static string Redact(string message, IEnumerable<string>? secrets)
    {
        if (string.IsNullOrEmpty(message) || secrets is null)
        {
            return message;
        }

        var safe = message;
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < MinSecretLength)
            {
                continue;
            }

            safe = safe.Replace(secret, RedactedPlaceholder, StringComparison.Ordinal);
        }

        return safe;
    }
}
