using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VoiceIme;

/// <summary>
/// Win32 glue: global hotkey registration and transcript delivery.
/// Delivery mirrors Android (clipboard + commit): set clipboard, then synthesize
/// Ctrl+V into the focused window via SendInput — the combination that reaches
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
    /// with Ctrl+V. Restores the user's previous clipboard afterwards.
    /// Must run on an STA thread.
    /// </summary>
    public static void PasteIntoFocusedWindow(string text)
    {
        IDataObject? previous = null;
        try { previous = Clipboard.GetDataObject(); } catch { /* clipboard busy */ }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            throw new IOException("Couldn't access the clipboard — try again", ex);
        }

        // Give the clipboard a beat to settle before the keystroke lands.
        Thread.Sleep(50);
        SendCtrlV();

        // Restore what the user had (best-effort, never throws).
        if (previous is not null)
        {
            Thread.Sleep(150);
            try { Clipboard.SetDataObject(previous, copy: true); } catch { /* ignore */ }
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new INPUT[]
        {
            KeyDown(0x11), // CTRL
            KeyDown((ushort)VK_V),
            KeyUp((ushort)VK_V),
            KeyUp(0x11),
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
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

    // Keep MOD_CONTROL visible to callers without exposing raw constants.
    public static uint ModControl => MOD_CONTROL;
}
