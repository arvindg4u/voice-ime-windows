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

    // --- Target inspection (paste diagnosis) -----------------------------
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass,
        ref TOKEN_ELEVATION tokenInformation, uint tokenInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>TOKEN_ELEVATION — a single DWORD, 1 when the token is elevated.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

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
    ///
    /// Returns a classified <see cref="PasteOutcome"/> instead of throwing, so
    /// the caller can tell the user *why* it failed. A single generic message
    /// cannot distinguish "that app is elevated and Windows forbids this" from
    /// "the clipboard was momentarily locked", and the first can never be fixed
    /// by trying again.
    /// </summary>
    public static PasteOutcome PasteIntoFocusedWindow(string text) =>
        PasteIntoFocusedWindow(text, PasteMethods.CtrlV);

    /// <summary>
    /// Chord-aware overload: the method is the persisted
    /// <see cref="PasteMethods"/> string (already coerced at the settings
    /// boundary); unknown values fall back to Ctrl+V.
    /// </summary>
    public static PasteOutcome PasteIntoFocusedWindow(string text, string? method)
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

        // Clipboard first: if we cannot even place the text, there is nothing
        // to paste and the keystroke would only deliver stale content.
        var clipboard = TrySetClipboard(text);
        if (clipboard is not null)
        {
            return clipboard;
        }

        // Check the target BEFORE sending. Two of these are unrecoverable from
        // this process, and sending anyway would paste into the wrong window
        // (or nowhere) while reporting success.
        var target = ClassifyTarget();
        if (target is not null)
        {
            // The transcript is already on the clipboard, so the user can still
            // recover it by hand — leave it there instead of restoring over it.
            return target;
        }

        NeutralizeHeldModifiers();

        // Give the clipboard a beat to settle before the keystroke lands.
        Thread.Sleep(50);

        var sent = SendChord(PasteChords.ForMethod(method));
        if (!sent.Delivered)
        {
            return sent;
        }

        // Restore what the user had — best-effort AND DELAYED: the chord only
        // queues WM_KEYDOWNs, and slow targets (Electron, UWP, browsers, RDP)
        // read the clipboard well after 150 ms. An early restore makes them
        // paste the OLD content; a background STA thread restores once the
        // target has had time to consume the transcript.
        if (!string.IsNullOrEmpty(previousText))
            RestoreClipboardLater(previousText);

        return PasteOutcome.Success;
    }

    /// <summary>
    /// Places the transcript on the clipboard, returning null on success or a
    /// classified failure. Distinguished from the input path because it is the
    /// one failure the user can always work around (paste it themselves).
    /// </summary>
    private static PasteOutcome? TrySetClipboard(string text)
    {
        try
        {
            SetClipboardTextWithRetry(text);
            return null;
        }
        catch (IOException ex)
        {
            Logger.Warning($"paste: clipboard unavailable — {ex.Message}");
            return PasteOutcome.Failure(
                PasteFailureReason.ClipboardBusy, ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Warning($"paste: clipboard error ({ex.GetType().Name}) — {ex.Message}");
            return PasteOutcome.Failure(
                PasteFailureReason.ClipboardBusy, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Decides whether the chord can be delivered, before sending it.
    /// Returns null when the target looks reachable.
    ///
    /// Deliberately conservative: an uncertain target is treated as reachable,
    /// because refusing to paste is worse than pasting and being wrong.
    /// </summary>
    private static PasteOutcome? ClassifyTarget()
    {
        IntPtr hwnd;
        try
        {
            hwnd = GetForegroundWindow();
        }
        catch
        {
            return null;
        }

        if (hwnd == IntPtr.Zero)
        {
            Logger.Warning("paste: no foreground window to receive the chord");
            return PasteOutcome.Failure(PasteFailureReason.NoForegroundWindow);
        }

        uint pid = 0;
        try
        {
            GetWindowThreadProcessId(hwnd, out pid);
        }
        catch
        {
            return null;
        }

        if (pid == (uint)Environment.ProcessId)
        {
            // The overlay or settings window holds focus, so the chord lands on
            // us. Sending it would be harmless but useless, and reporting
            // success would be a lie.
            Logger.Warning("paste: foreground window belongs to Voice IME — chord will not reach the target app");
            return PasteOutcome.Failure(PasteFailureReason.FocusLost);
        }

        // UIPI: a lower-integrity process may not inject input into a
        // higher-integrity one. Detect it up front so the message can say so
        // rather than surfacing a generic failure the user will retry forever.
        try
        {
            if (IsProcessElevated(pid))
            {
                Logger.Warning($"paste: target pid {pid} is elevated — UIPI blocks synthesized input");
                return PasteOutcome.Failure(PasteFailureReason.TargetElevated);
            }
        }
        catch
        {
            // Token probe unavailable — assume reachable and let SendInput decide.
        }

        return null;
    }

    /// <summary>
    /// True when the process is running with an elevated token. Uses
    /// PROCESS_QUERY_LIMITED_INFORMATION, which is grantable for other users'
    /// processes without needing full query rights.
    /// </summary>
    private static bool IsProcessElevated(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false; // Cannot inspect — do not block the paste.
        }

        try
        {
            if (!OpenProcessToken(handle, TOKEN_QUERY, out var token))
            {
                return false;
            }

            try
            {
                var elevation = new TOKEN_ELEVATION();
                var size = Marshal.SizeOf<TOKEN_ELEVATION>();
                if (!GetTokenInformation(
                        token, TokenElevation, ref elevation, (uint)size, out _))
                {
                    return false;
                }

                return elevation.TokenIsElevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
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
    /// AutoSubmit: synthesize Enter into the focused window right after a
    /// paste, for single-line targets (search boxes, chat inputs). Returns the
    /// classified outcome rather than throwing, matching the paste chord.
    /// </summary>
    public static PasteOutcome PressEnter()
    {
        var inputs = new INPUT[] { KeyDown(PasteChords.VkReturn), KeyUp(PasteChords.VkReturn) };
        return SendOrClassify(inputs);
    }

    private static PasteOutcome SendChord(PasteChord chord)
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

        return SendOrClassify(inputs.ToArray());
    }

    /// <summary>
    /// Sends the chord and classifies a failure from the Win32 error rather
    /// than throwing a message that discards the cause.
    /// </summary>
    private static PasteOutcome SendOrClassify(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
        {
            return PasteOutcome.Success;
        }

        var error = Marshal.GetLastWin32Error();
        var reason = PasteDiagnosis.FromSendInputError(error);
        Logger.Warning($"paste: SendInput delivered {sent}/{inputs.Length} (win32={error})"
            + (reason == PasteFailureReason.TargetElevated ? " — UIPI (target elevated)" : string.Empty));
        return PasteOutcome.Failure(reason, $"SendInput win32={error}");
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
