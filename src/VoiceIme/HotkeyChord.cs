using System;
using System.Collections.Generic;

namespace VoiceIme;

/// <summary>
/// Parses and formats global-hotkey chords such as "Ctrl+Shift+Space".
/// Pure logic — no Win32 calls — so it is unit-testable on any OS.
/// Canonical order is Ctrl+Alt+Shift+Win+Key. A valid chord is exactly one
/// main key plus at least one modifier: a bare key can never be a global
/// hotkey, and bare modifiers cannot fire one.
/// </summary>
public static class HotkeyChord
{
    public const string DefaultChord = "Ctrl+Shift+Space";

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    private static readonly Dictionary<string, uint> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Esc"] = 0x1B,
        ["Escape"] = 0x1B,
        ["Backspace"] = 0x08,
        ["Back"] = 0x08,
        ["Delete"] = 0x2E,
        ["Del"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Ins"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["Prior"] = 0x21,
        ["PageDown"] = 0x22,
        ["Next"] = 0x22,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
    };

    private static readonly Dictionary<uint, string> KeyCodes = new()
    {
        [0x20] = "Space",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x1B] = "Esc",
        [0x08] = "Backspace",
        [0x2E] = "Delete",
        [0x2D] = "Insert",
        [0x24] = "Home",
        [0x23] = "End",
        [0x21] = "PageUp",
        [0x22] = "PageDown",
        [0x26] = "Up",
        [0x28] = "Down",
        [0x25] = "Left",
        [0x27] = "Right",
    };

    /// <summary>
    /// Parses "Ctrl+Shift+Space" into Win32 RegisterHotKey modifiers + VK.
    /// Returns false (zeroed outputs) for empty, modifier-less, key-less,
    /// multi-key, or unknown-token chords — never throws.
    /// </summary>
    public static bool TryParse(string? chord, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(chord))
        {
            return false;
        }

        var mainKeys = 0;
        foreach (var raw in chord.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                return false;
            }

            if (TryParseModifier(token, out var mod))
            {
                modifiers |= mod;
                continue;
            }

            if (TryParseKey(token, out var code))
            {
                mainKeys++;
                vk = code;
                continue;
            }

            return false;
        }

        return mainKeys == 1 && modifiers != 0;
    }

    /// <summary>
    /// Parses a cancel-style key: either a full modifier+key chord (see
    /// <see cref="TryParse"/>) or a single bare key ("Esc") with zero
    /// modifiers. Bare keys are only safe for dynamically-armed shortcuts —
    /// cancel registers during recording/uploading, then unregisters — never
    /// for the always-on dictation hotkey, which must keep
    /// <see cref="TryParse"/>. Never throws.
    /// </summary>
    public static bool TryParseWithBareKey(string? chord, out uint modifiers, out uint vk)
    {
        if (TryParse(chord, out modifiers, out vk))
        {
            return true;
        }

        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(chord))
        {
            return false;
        }

        var token = chord.Trim();
        if (TryParseModifier(token, out _))
        {
            return false;
        }

        return TryParseKey(token, out vk);
    }

    /// <summary>Formats modifiers + VK back to canonical "Ctrl+Shift+Space" form.</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    /// <summary>Display name for one virtual-key code ("A", "F5", "Space").</summary>
    public static string KeyName(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z')
        {
            return ((char)vk).ToString();
        }

        if (vk >= '0' && vk <= '9')
        {
            return ((char)vk).ToString();
        }

        if (vk >= 0x70 && vk <= 0x87)
        {
            return "F" + (vk - 0x6F);
        }

        return KeyCodes.TryGetValue(vk, out var name) ? name : $"0x{vk:X2}";
    }

    private static bool TryParseModifier(string token, out uint mod)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                mod = ModControl;
                return true;
            case "alt":
                mod = ModAlt;
                return true;
            case "shift":
                mod = ModShift;
                return true;
            case "win":
            case "windows":
            case "meta":
            case "super":
            case "command":
                mod = ModWin;
                return true;
            default:
                mod = 0;
                return false;
        }
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                vk = c;
                return true;
            }

            vk = 0;
            return false;
        }

        if (token.Length > 1
            && (token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token[1..], out var f)
            && f >= 1 && f <= 24)
        {
            vk = (uint)(0x6F + f);
            return true;
        }

        return KeyNames.TryGetValue(token, out vk);
    }
}
