using System;
using VoiceIme;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Locks the Win32 <c>INPUT</c> struct layout.
///
/// This exists because of a defect that shipped in every release up to v1.0.2:
/// the <c>INPUT</c> union declared only <c>KEYBDINPUT</c>, so the struct
/// marshalled to 32 bytes on x64 instead of 40. <c>SendInput</c> requires
/// <c>cbSize</c> to equal <c>sizeof(INPUT)</c> exactly and returns 0 with
/// ERROR_INVALID_PARAMETER otherwise, so *every* synthesized keystroke was
/// rejected — in every application, with no per-app pattern to investigate.
///
/// The failure is invisible without this check: no exception, no crash, and
/// nothing to notice at runtime except "paste never works".
/// </summary>
public sealed class InputStructLayoutTests
{
    /// <summary>
    /// x64: type(4) + pad(4) + union(32) = 40. x86: type(4) + union(28) = 28.
    /// </summary>
    private static int Expected => IntPtr.Size == 8 ? 40 : 28;

    [Fact]
    public void InputStruct_MatchesTheOSSize()
    {
        Assert.Equal(Expected, NativeInput.InputStructSize);
    }

    [Fact]
    public void ExpectedSize_MatchesTheOSSize()
    {
        Assert.Equal(Expected, NativeInput.ExpectedInputStructSize);
    }

    /// <summary>
    /// The union must be as wide as its widest member. If a future edit drops
    /// MOUSEINPUT again to "simplify" the keyboard-only path, the struct shrinks
    /// and every SendInput call silently starts failing.
    /// </summary>
    [Fact]
    public void InputStruct_IsAtLeastAsLargeAsAWideUnionMember()
    {
        // MOUSEINPUT is 32 bytes on x64 (24 on x86); INPUT must exceed it by at
        // least the type field plus alignment padding.
        var widestUnionMember = IntPtr.Size == 8 ? 32 : 24;

        Assert.True(
            NativeInput.InputStructSize > widestUnionMember,
            $"INPUT is {NativeInput.InputStructSize} bytes, which does not accommodate " +
            $"a {widestUnionMember}-byte union member.");
    }
}