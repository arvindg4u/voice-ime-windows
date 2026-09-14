using System.Collections.Generic;
using Xunit;

namespace VoiceIme.Tests;

public sealed class NotifierTests
{
    [Fact]
    public void Notify_ForwardsTitleBodyAndSeverityToSink()
    {
        (string Title, string Body, NotificationSeverity Severity)? seen = null;
        var notifier = new Notifier((title, body, severity) => seen = (title, body, severity));

        notifier.Notify("Voice IME", "pasted", NotificationSeverity.Info);

        Assert.Equal(("Voice IME", "pasted", NotificationSeverity.Info), seen);
    }

    [Fact]
    public void Notify_RedactsApiKey_BeforeItReachesTheUser()
    {
        const string fakeKey = "AIzaSyFakeKeyForTestOnly0123456789";
        (string Title, string Body, NotificationSeverity Severity)? seen = null;
        var notifier = new Notifier((title, body, severity) => seen = (title, body, severity));

        // Simulate a raw exception string that accidentally embeds the key.
        notifier.Notify(
            "Voice IME",
            $"Request failed with key {fakeKey} — try again",
            NotificationSeverity.Error,
            secrets: [fakeKey]);

        Assert.NotNull(seen);
        Assert.DoesNotContain(fakeKey, seen.Value.Body);
        Assert.Contains(Notifier.RedactedPlaceholder, seen.Value.Body);
    }

    [Fact]
    public void Notify_RedactsEveryConfiguredKey()
    {
        const string keyOne = "AIzaFakeKeyOneForTest000000000001";
        const string keyTwo = "AIzaFakeKeyTwoForTest000000000002";
        string? seen = null;
        var notifier = new Notifier((_, body, _) => seen = body);

        notifier.Notify("Voice IME", $"{keyOne} and {keyTwo}", secrets: [keyOne, keyTwo]);

        Assert.NotNull(seen);
        Assert.DoesNotContain(keyOne, seen);
        Assert.DoesNotContain(keyTwo, seen);
    }

    [Fact]
    public void Notify_LeavesMessageAlone_WhenNoSecretsConfigured()
    {
        string? seen = null;
        var notifier = new Notifier((_, body, _) => seen = body);

        notifier.Notify("Voice IME", "Microphone unavailable");

        Assert.Equal("Microphone unavailable", seen);
    }

    [Fact]
    public void Redact_IgnoresSecretsTooShortToBeSafe()
    {
        // Redacting a 2-char string would wipe innocent text; Notifier skips it.
        var redacted = Notifier.Redact("No API key — open Settings", ["ey"]);

        Assert.Equal("No API key — open Settings", redacted);
    }

    [Fact]
    public void Redact_ReturnsMessageUnchanged_WhenSecretsNull()
    {
        Assert.Equal("plain", Notifier.Redact("plain", secrets: null));
        Assert.Equal("plain", Notifier.Redact("plain", secrets: new List<string>()));
    }
}
