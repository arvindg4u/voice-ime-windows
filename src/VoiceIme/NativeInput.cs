using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VoiceIme;

/// <summary>
/// Win32 glue: global hotkey registration and transcript delivery.
/// Delivery mirrors Android (clipboard + commit): set clipboard, then synthesize
/// the configured paste chord (Task 8: Ctrl+V / Shift+Insert / Ctrl+Shift+V)
/// into the focused window via SendInput — the combinations that reach
/// browsers, Office, and editors. Elevated targets are out of reach unless this
/// app also runs elevated (documented limitation, not a silent failure).
/// </summary>
public static class NativeInput
{
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_V = 0x56;
    private const uint WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Copies text to the clipboard then pastes it into the focused window
    /// with the configured paste chord. Restores the previous clipboard
    /// afterwards. Must run on an STA thread.
    /// </summary>
    public static void PasteIntoFocusedWindow(string text) =>
        PasteIntoFocusedWindow(text, PasteMethods.CtrlV);

    /// <summary>
    /// Chord-aware overload: the method is the persisted
    /// <see cref="PasteMethods"/> string (already coerced at the settings
    /// boundary); unknown values fall back to Ctrl+V, never throw.
    /// </summary>
    public static void PasteIntoFocusedWindow(string text, string? method)
    {
        System.Windows.Forms.IDataObject? previous = null;
        try { previous = System.Windows.Forms.Clipboard.GetDataObject(); } catch { /* clipboard busy */ }

        try
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                throw new IOException("Couldn't access the clipboard — try again", ex);
            }

            // Give the clipboard a beat to settle before the keystroke lands.
            Thread.Sleep(50);
            SendChord(PasteChords.ForMethod(method));
        }
        finally
        {
            // Restoration must be in finally: SendInput can fail, be
            // interrupted, or throw while the focused target disappears. A
            // failed paste must not strand the user's clipboard with our text.
            if (previous is not null)
            {
                try
                {
                    Thread.Sleep(150);
                    System.Windows.Forms.Clipboard.SetDataObject(previous, copy: true);
                }
                catch
                {
                    /* clipboard restoration is best effort */
                }
            }
        }
    }

    /// <summary>
    /// AutoSubmit (Task 8): synthesize Enter into the focused window right
    /// after a paste, for single-line targets (search boxes, chat inputs).
    /// Throws only when SendInput itself reports short — same contract as
    /// the paste chord above.
    /// </summary>
    public static void PressEnter()
    {
        var inputs = new INPUT[] { KeyDown(PasteChords.VkReturn), KeyUp(PasteChords.VkReturn) };
        SendOrThrow(inputs);
    }

    private static void SendChord(PasteChord chord)
    {
        var inputs = new List<INPUT>(chord.Modifiers.Length * 2 + 2);
        foreach (var modifier in chord.Modifiers)
        {
            inputs.Add(KeyDown(modifier));
        }

        inputs.Add(KeyDown(chord.Key));
        inputs.Add(KeyUp(chord.Key));
        for (var i = chord.Modifiers.Length - 1; i >= 0; i--)
        {
            inputs.Add(KeyUp(chord.Modifiers[i]));
        }

        SendOrThrow(inputs.ToArray());
    }

    private static void SendOrThrow(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
        {
            return;
        }

        // SendInput may accept only a prefix. If that prefix contains a
        // modifier-down event, do a best-effort release so a transient UIPI or
        // focus failure cannot leave Ctrl/Shift/Alt/Win logically stuck.
        var accepted = (int)Math.Min(sent, (uint)inputs.Length);
        var releases = new List<INPUT>();
        for (var i = accepted - 1; i >= 0; i--)
        {
            var key = inputs[i].U.ki;
            if ((key.dwFlags & KEYEVENTF_KEYUP) == 0)
            {
                releases.Add(KeyUp(key.wVk));
            }
        }

        if (releases.Count > 0)
        {
            try
            {
                SendInput((uint)releases.Count, releases.ToArray(), Marshal.SizeOf<INPUT>());
            }
            catch
            {
                // Preserve the original delivery failure.
            }
        }

        throw new IOException("Couldn't send keystrokes to the focused window");
    }

    private static INPUT KeyDown(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } },
    };

    private static INPUT KeyUp(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } },
    };

    public static bool TryRegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk) =>
        RegisterHotKey(hWnd, id, modifiers, vk);

    public static void TryUnregisterHotKey(IntPtr hWnd, int id)
    {
        try { UnregisterHotKey(hWnd, id); } catch { /* ignore */ }
    }

    public static bool IsHotKeyMessage(int msg) => msg == (int)WM_HOTKEY;

    /// <summary>
    /// Polls the physical chord because RegisterHotKey delivers WM_HOTKEY for
    /// the press but has no key-up message. Used only while a hold-style
    /// activation is recording; toggle mode never polls the keyboard.
    /// </summary>
    public static bool IsHotkeyDown(uint modifiers, uint vk)
    {
        if (!IsVirtualKeyDown(vk))
        {
            return false;
        }

        if ((modifiers & HotkeyChord.ModControl) != 0
            && !IsVirtualKeyDown(0x11)) // VK_CONTROL
        {
            return false;
        }

        if ((modifiers & HotkeyChord.ModAlt) != 0
            && !IsVirtualKeyDown(0x12)) // VK_MENU
        {
            return false;
        }

        if ((modifiers & HotkeyChord.ModShift) != 0
            && !IsVirtualKeyDown(0x10)) // VK_SHIFT
        {
            return false;
        }

        if ((modifiers & HotkeyChord.ModWin) != 0
            && !IsVirtualKeyDown(0x5B) // VK_LWIN
            && !IsVirtualKeyDown(0x5C)) // VK_RWIN
        {
            return false;
        }

        return true;
    }

    private static bool IsVirtualKeyDown(uint vk) =>
        (GetAsyncKeyState(unchecked((int)vk)) & unchecked((short)0x8000)) != 0;

    // Keep MOD_CONTROL visible to callers without exposing raw constants.
    public static uint ModControl => MOD_CONTROL;
}

/// <summary>
/// DllImports HotkeyWindow needs that do not belong on <see cref="VoiceIme.NativeInput"/>
/// (which is about hotkeys and paste). Registered window messages back the
/// Task 9 single-instance broadcast.
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);
}
