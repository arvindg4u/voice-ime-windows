using System;
using System.Linq;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Paste-failure classification and copy.
///
/// Pure logic over enums and strings — no Win32, no WPF — so it runs on every
/// OS. The point of these tests is that each distinct cause produces a
/// *different* message with an honest recovery step; the old implementation
/// produced one sentence for every failure, which is what made the bug
/// unactionable.
/// </summary>
public sealed class PasteDiagnosisTests
{
    private static readonly PasteFailureReason[] AllReasons =
        Enum.GetValues<PasteFailureReason>()
            .Where(r => r != PasteFailureReason.None)
            .ToArray();

    [Fact]
    public void EveryFailureReason_HasADistinctMessage()
    {
        var messages = AllReasons.Select(PasteDiagnosis.MessageFor).ToArray();

        Assert.All(messages, m => Assert.False(string.IsNullOrWhiteSpace(m)));
        Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryFailureReason_HasARecoveryStep()
    {
        // A failure the user cannot act on is only half an error message.
        Assert.All(AllReasons, reason =>
            Assert.False(string.IsNullOrWhiteSpace(PasteDiagnosis.RecoveryFor(reason)),
                $"{reason} has no recovery guidance."));
    }

    [Fact]
    public void RecoverySteps_AreNotAllIdentical()
    {
        var recoveries = AllReasons
            .Select(PasteDiagnosis.RecoveryFor)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToArray();

        Assert.True(recoveries.Distinct(StringComparer.Ordinal).Count() > 1,
            "Every cause suggests the same fix — the classification is not earning its keep.");
    }

    [Fact]
    public void Messages_DoNotBlameTheUserOrLeakCodes()
    {
        foreach (var reason in AllReasons)
        {
            var message = PasteDiagnosis.MessageFor(reason);

            Assert.DoesNotContain("you ", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("failed to", message, StringComparison.OrdinalIgnoreCase);
            // No numeric error codes in user-facing copy.
            Assert.DoesNotMatch(@"\b0x[0-9A-Fa-f]+|\bwin32\b|\berror\b", message);
        }
    }

    [Fact]
    public void NoFailureMessage_EndsWithAPeriod()
    {
        // Labels and status lines do not take a trailing period; the recovery
        // sentence does. Keeping them distinguishable keeps the concatenated
        // toast readable.
        foreach (var reason in AllReasons)
        {
            Assert.False(PasteDiagnosis.MessageFor(reason).EndsWith('.'), reason.ToString());
        }
    }

    [Theory]
    [InlineData(5, PasteFailureReason.TargetElevated)]   // ERROR_ACCESS_DENIED = UIPI
    [InlineData(87, PasteFailureReason.InputRejected)]   // ERROR_INVALID_PARAMETER
    [InlineData(0, PasteFailureReason.InputRejected)]
    public void FromSendInputError_ClassifiesAccessDeniedAsElevation(
        int win32Error, PasteFailureReason expected)
    {
        Assert.Equal(expected, PasteDiagnosis.FromSendInputError(win32Error));
    }

    [Fact]
    public void ElevatedTarget_IsNotRetryable()
    {
        // UIPI is a static boundary — retrying identical input can never work,
        // so offering "try again" would be misleading.
        Assert.False(PasteDiagnosis.IsRetryable(PasteFailureReason.TargetElevated));
        Assert.False(PasteDiagnosis.IsRetryable(PasteFailureReason.FocusLost));
        Assert.False(PasteDiagnosis.IsRetryable(PasteFailureReason.NoForegroundWindow));
    }

    [Fact]
    public void ClipboardBusy_IsRetryable()
    {
        // Contention is transient, so retrying is genuinely likely to succeed.
        Assert.True(PasteDiagnosis.IsRetryable(PasteFailureReason.ClipboardBusy));
    }

    [Fact]
    public void ElevatedTarget_ExplainsTheAdminCause()
    {
        var message = PasteDiagnosis.MessageFor(PasteFailureReason.TargetElevated);

        Assert.Contains("administrator", message, StringComparison.OrdinalIgnoreCase);
    }

    // --- PasteError wiring ------------------------------------------------

    [Fact]
    public void PasteError_PointsAtHistoryForEveryReason()
    {
        // The store is written before the paste is attempted, so every variant
        // can honestly say the text was kept.
        Assert.All(AllReasons, reason =>
        {
            IDictationError error = new PasteError(reason);
            Assert.False(string.IsNullOrWhiteSpace(error.UserMessage));
        });
    }

    [Fact]
    public void PasteError_ExposesRecoveryAndRetryFromItsReason()
    {
        var error = new PasteError(PasteFailureReason.TargetElevated);

        Assert.Equal(PasteFailureReason.TargetElevated, error.Reason);
        Assert.NotNull(error.Recovery);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public void PasteError_DefaultReason_IsInputRejected()
    {
        // Callers that cannot classify (e.g. the tray Copy command) must still
        // produce a sane message rather than throwing.
        var error = new PasteError();

        Assert.Equal(PasteFailureReason.InputRejected, error.Reason);
        Assert.False(string.IsNullOrWhiteSpace(error.UserMessage));
    }

    [Fact]
    public void PasteOutcome_Success_HasNoReason()
    {
        Assert.True(PasteOutcome.Success.Delivered);
        Assert.Equal(PasteFailureReason.None, PasteOutcome.Success.Reason);
    }

    [Fact]
    public void PasteOutcome_Failure_IsNotDelivered()
    {
        var outcome = PasteOutcome.Failure(PasteFailureReason.ClipboardBusy, "detail");

        Assert.False(outcome.Delivered);
        Assert.Equal(PasteFailureReason.ClipboardBusy, outcome.Reason);
        Assert.Equal("detail", outcome.Diagnostic);
    }
}