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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

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
        // Snapshot the previous clipboard TEXT only (a plain string crosses
        // threads safely; an IDataObject COM proxy cannot leave this STA, so
        // restoring it from the delayed thread below would silently fail).
        string? previousText = null;
        try
        {
            if (System.Windows.Forms.Clipboard.ContainsText())
                previousText = System.Windows.Forms.Clipboard.GetText();
        }
        catch { /* clipboard busy */ }

        SetClipboardTextWithRetry(text);
        WarnIfForegroundIsSelf();
        NeutralizeHeldModifiers();

        // Give the clipboard a beat to settle before the keystroke lands.
        Thread.Sleep(50);
        SendChord(PasteChords.ForMethod(method));

        // Restore what the user had — best-effort AND DELAYED: the chord only
        // queues WM_KEYDOWNs, and slow targets (Electron, UWP, browsers, RDP)
        // read the clipboard well after 150 ms. An early restore makes them
        // paste the OLD content; a background STA thread restores once the
        // target has had time to consume the transcript.
        if (!string.IsNullOrEmpty(previousText))
            RestoreClipboardLater(previousText);
    }

    /// <summary>
    /// Windows 11's clipboard history / cross-device sync service (cbdhsvc)
    /// opens the clipboard after every change, and any concurrent opener makes
    /// SetText throw CLIPBRD_E_CANT_OPEN even after WinForms' internal
    /// 10×100 ms retries. One outer retry round absorbs that churn before the
    /// failure is declared; the user-safe message never changes.
    /// </summary>
    private static void SetClipboardTextWithRetry(string text)
    {
        const int attempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(text, copy: true, retryTimes: 10, retryDelay: 100);
                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                Logger.Warning($"paste: clipboard busy ({ex.GetType().Name}), attempt {attempt}/{attempts}");
                Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                throw new IOException("Couldn't access the clipboard — try again", ex);
            }
        }
    }

    /// <summary>
    /// Live commits land while the stop-hotkey keys are often still physically
    /// held, and SendInput merges injected and physical state — Ctrl+V becomes
    /// Ctrl+Alt+V and the target app ignores it. Wait briefly for the user to
    /// release, then force key-ups for any stragglers (the user's eventual
    /// physical release is a harmless no-op afterwards).
    /// </summary>
    private static void NeutralizeHeldModifiers()
    {
        const int waitBudgetMs = 400;
        var deadline = Environment.TickCount64 + waitBudgetMs;
        while (AnyModifierDown() && Environment.TickCount64 < deadline)
            Thread.Sleep(15);

        var ups = new List<INPUT>();
        foreach (var vk in PhysicalModifierVks)
        {
            if (IsKeyDown(vk))
                ups.Add(KeyUp(vk));
        }

        if (ups.Count == 0)
            return;
        Logger.Debug($"paste: releasing {ups.Count} still-held modifier(s) before the chord");
        SendInput((uint)ups.Count, ups.ToArray(), Marshal.SizeOf<INPUT>()); // best-effort
        Thread.Sleep(15); // let the key state settle before the chord
    }

    // Left/right-specific VKs: the specific key must be released, releasing
    // generic VK_CONTROL does not clear a held VK_RCONTROL.
    private static readonly ushort[] PhysicalModifierVks =
    [
        0xA0 /*LSHIFT*/, 0xA1 /*RSHIFT*/,
        0xA2 /*LCONTROL*/, 0xA3 /*RCONTROL*/,
        0xA4 /*LMENU*/, 0xA5 /*RMENU*/,
        0x5B /*LWIN*/, 0x5C /*RWIN*/,
    ];

    private static bool AnyModifierDown()
    {
        foreach (var vk in PhysicalModifierVks)
        {
            if (IsKeyDown(vk))
                return true;
        }
        return false;
    }

    private static bool IsKeyDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Key-free diagnosis: when the foreground window belongs to this process
    /// (overlay stole focus), the chord cannot reach the user's target app —
    /// log it so "pasted nothing, no error" reports become actionable.
    /// </summary>
    private static void WarnIfForegroundIsSelf()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                Logger.Warning("paste: no foreground window to receive the chord");
                return;
            }
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId)
                Logger.Warning("paste: foreground window belongs to Voice IME — chord will not reach the target app");
        }
        catch
        {
            // Diagnosis only; never block the paste.
        }
    }

    private static void RestoreClipboardLater(string previousText)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(800);
            try { System.Windows.Forms.Clipboard.SetDataObject(previousText, copy: true); }
            catch { /* best-effort restore */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
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
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            // win32=5 (access denied) is UIPI: the focused window is elevated
            // and a non-elevated process may not inject input into it.
            Logger.Warning($"paste: SendInput delivered {sent}/{inputs.Length} (win32={error})"
                + (error == 5 ? " — focused window appears elevated (UIPI)" : ""));
            throw new IOException("Couldn't send keystrokes to the focused window");
        }
    }

    private static INPUT KeyDown(ushort vk) => Key(vk, 0);

    private static INPUT KeyUp(ushort vk) => Key(vk, KEYEVENTF_KEYUP);

    /// <summary>
    /// Builds one keystroke carrying BOTH the virtual key and its hardware
    /// scan code (MapVirtualKey): apps reading Raw Input / scan codes ignore
    /// VK-only injection. Extended keys (Insert, right-side modifiers, Win)
    /// additionally need KEYEVENTF_EXTENDEDKEY for a correct scan code.
    /// </summary>
    private static INPUT Key(ushort vk, uint flags)
    {
        if (IsExtendedKey(vk))
            flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */),
                    dwFlags = flags,
                },
            },
        };
    }

    internal static bool IsExtendedKey(ushort vk) => vk is
        0x2D /*INSERT*/ or 0x2E /*DELETE*/
        or 0xA3 /*RCONTROL*/ or 0xA5 /*RMENU*/
        or 0x5B /*LWIN*/ or 0x5C /*RWIN*/;

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
