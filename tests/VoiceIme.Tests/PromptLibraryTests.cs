using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Prompt-library pure logic tests (<see cref="PromptLibrary"/>). No WPF, no
/// file, no DPAPI — runs on any OS. Store round-trip and view behavior live
/// in <c>SettingsStoreTests</c> and <c>GeminiSettingsViewTests</c>.
/// </summary>
public sealed class PromptLibraryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateName_Blank_RejectsWithMessage(string? name)
    {
        var error = PromptLibrary.ValidateName(name);

        Assert.NotNull(error);
        Assert.Contains("required", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateName_AcceptsUpTo60Chars()
    {
        Assert.Null(PromptLibrary.ValidateName("Work"));
        Assert.Null(PromptLibrary.ValidateName(new string('n', 60)));
    }

    [Fact]
    public void ValidateName_RejectsOver60()
    {
        var error = PromptLibrary.ValidateName(new string('n', 61));

        Assert.NotNull(error);
        Assert.Contains("60", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateText_MatchesExisting2000Convention()
    {
        Assert.Null(PromptLibrary.ValidateText(""));
        Assert.Null(PromptLibrary.ValidateText(new string('x', 2000)));

        var error = PromptLibrary.ValidateText(new string('x', 2001));

        Assert.NotNull(error);
        Assert.Contains("2000", error, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexOf_MatchesCaseInsensitivelyAndTrims()
    {
        var prompts = new List<PromptEntry> { new("Work", "a"), new("Mail", "b") };

        Assert.Equal(0, PromptLibrary.IndexOf(prompts, "work"));
        Assert.Equal(1, PromptLibrary.IndexOf(prompts, "  MAIL  "));
        Assert.Equal(-1, PromptLibrary.IndexOf(prompts, "Missing"));
        Assert.Equal(-1, PromptLibrary.IndexOf(prompts, null));
    }

    [Fact]
    public void CoercePrompts_Null_YieldsEmpty()
    {
        Assert.Empty(PromptLibrary.CoercePrompts(null));
    }

    [Fact]
    public void CoercePrompts_DropsBlankNamesAndNullsWithWarnings()
    {
        var warnings = new List<string>();
        var entries = new List<PromptEntry?>
        {
            null,
            new("", "no-name"),
            new("   ", "blank-name"),
            new("Kept", "ok"),
        };

        var clean = PromptLibrary.CoercePrompts(entries, warnings.Add);

        Assert.True(clean.SequenceEqual([new PromptEntry("Kept", "ok")]));
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void CoercePrompts_DuplicateKeepsFirst_CaseInsensitive()
    {
        var warnings = new List<string>();
        var entries = new List<PromptEntry?>
        {
            new("Work", "first"),
            new("work", "second"),
            new(" WORK ", "third"),
        };

        var clean = PromptLibrary.CoercePrompts(entries, warnings.Add);

        Assert.True(clean.SequenceEqual([new PromptEntry("Work", "first")]));
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void CoercePrompts_TruncatesOverlongNameAndText()
    {
        var warnings = new List<string>();
        var entries = new List<PromptEntry?>
        {
            new(new string('n', 61), new string('x', 2001)),
        };

        var clean = PromptLibrary.CoercePrompts(entries, warnings.Add);

        Assert.Single(clean);
        Assert.Equal(60, clean[0].Name.Length);
        Assert.Equal(2000, clean[0].Text.Length);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void CoercePrompts_CapsAt20KeepingFirst()
    {
        var warnings = new List<string>();
        var entries = Enumerable.Range(1, 25)
            .Select(i => new PromptEntry($"P{i:00}", $"t{i}"))
            .ToList();

        var clean = PromptLibrary.CoercePrompts(entries, warnings.Add);

        Assert.Equal(PromptLibrary.MaxPrompts, clean.Count);
        Assert.Equal("P01", clean[0].Name);
        Assert.Equal("P20", clean[19].Name);
        Assert.Single(warnings);
    }

    [Fact]
    public void CoerceActivePrompt_Match_KeepsCanonicalCasing()
    {
        var prompts = new List<PromptEntry> { new("Work", "a") };

        Assert.Equal("Work", PromptLibrary.CoerceActivePrompt("work", prompts));
    }

    [Fact]
    public void CoerceActivePrompt_Unknown_FallsBackToFirstWithWarning()
    {
        var warnings = new List<string>();
        var prompts = new List<PromptEntry> { new("Work", "a"), new("Mail", "b") };

        var active = PromptLibrary.CoerceActivePrompt("Ghost", prompts, warnings.Add);

        Assert.Equal("Work", active);
        Assert.Single(warnings);
    }

    [Fact]
    public void CoerceActivePrompt_EmptyLibrary_YieldsEmpty()
    {
        Assert.Equal("", PromptLibrary.CoerceActivePrompt("Anything", []));
        Assert.Equal("", PromptLibrary.CoerceActivePrompt("", []));
    }

    [Fact]
    public void MigrateCustomPrompt_EmptyPlusLegacy_SeedsDefault()
    {
        var (prompts, active) =
            PromptLibrary.MigrateCustomPrompt([], "", "  polish it  ");

        Assert.True(prompts.SequenceEqual(
            [new PromptEntry(PromptLibrary.DefaultPromptName, "polish it")]));
        Assert.Equal(PromptLibrary.DefaultPromptName, active);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MigrateCustomPrompt_EmptyPlusBlank_NoEntry(string? legacy)
    {
        var (prompts, active) =
            PromptLibrary.MigrateCustomPrompt([], "", legacy);

        Assert.Empty(prompts);
        Assert.Equal("", active);
    }

    [Fact]
    public void MigrateCustomPrompt_NonEmpty_IgnoresLegacy()
    {
        // Simplest safe rule: once the library owns the text, the legacy
        // field is ignored as input (the store mirror still syncs it).
        var existing = new List<PromptEntry> { new("Work", "lib text") };

        var (prompts, active) =
            PromptLibrary.MigrateCustomPrompt(existing, "Work", "stale legacy");

        Assert.Equal(existing, prompts);
        Assert.Equal("Work", active);
        Assert.Equal("lib text", prompts[0].Text);
    }

    [Fact]
    public void MigrateCustomPrompt_Idempotent_RerunIsNoOp()
    {
        var (first, active) =
            PromptLibrary.MigrateCustomPrompt([], "", "legacy");
        var (second, activeAgain) =
            PromptLibrary.MigrateCustomPrompt(first, active, "legacy");

        Assert.True(second.SequenceEqual(
            [new PromptEntry(PromptLibrary.DefaultPromptName, "legacy")]));
        Assert.Equal(PromptLibrary.DefaultPromptName, activeAgain);
    }

    [Fact]
    public void GetActiveText_ReturnsActiveOrFallback()
    {
        var prompts = new List<PromptEntry> { new("Work", "lib text") };

        Assert.Equal("lib text", PromptLibrary.GetActiveText(prompts, "WORK", "fallback"));
        Assert.Equal("fallback", PromptLibrary.GetActiveText(prompts, "Ghost", "fallback"));
        Assert.Equal("fallback", PromptLibrary.GetActiveText([], "", "fallback"));
    }
}
