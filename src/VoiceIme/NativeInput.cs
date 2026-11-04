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

    /// <summary>
    /// Sequence number incremented whenever the clipboard changes. Used to prove
    /// our write actually landed, and to avoid restoring over someone else's copy.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // --- Target inspection (paste diagnosis) -----------------------------
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int TokenIntegrityLevel = 25;

    [DllImport("user32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength,
        out uint returnLength);

    /// <summary>
    /// TOKEN_MANDATORY_LABEL — an attributes DWORD followed by the integrity
    /// SID pointer. The RID of that SID is the integrity level.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public IntPtr Attributes;
        public IntPtr Sid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    /// <summary>
    /// The <c>INPUT</c> union. All three members must be declared even though
    /// this app only ever sends keyboard events: the union's size is that of
    /// its LARGEST member, and <c>SendInput</c> rejects the whole call unless
    /// <c>cbSize</c> is exactly <c>sizeof(INPUT)</c>.
    ///
    /// <para>Declaring only <c>KEYBDINPUT</c> made the struct 32 bytes on x64
    /// instead of 40, because MOUSEINPUT is the widest member (32 bytes). Every
    /// SendInput call then failed with ERROR_INVALID_PARAMETER — which is why
    /// no app ever received a paste.</para>
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    /// <summary>Widest union member — present only so the union is sized correctly.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>Union member — present only so the union is sized correctly.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
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

    /// <summary>
    /// The cbSize SendInput requires. Exposed for a regression test — a wrong
    /// value fails every call and is otherwise invisible (SendInput just
    /// returns 0, and nothing reaches any application).
    /// </summary>
    internal static int InputStructSize => Marshal.SizeOf<INPUT>();

    /// <summary>sizeof(INPUT) as the OS defines it, for the current pointer width.</summary>
    internal static int ExpectedInputStructSize => IntPtr.Size == 8 ? 40 : 28;

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
        var set = TrySetClipboard(text);
        if (set.Outcome is not null)
        {
            return set.Outcome;
        }

        var sequenceAtOurSet = set.SequenceNumber;

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

        var releases = NeutralizeHeldModifiers();

        // Give the clipboard a beat to settle before the keystroke lands.
        Thread.Sleep(50);

        // Releases ride along in the same array as the chord — see
        // NeutralizeHeldModifiers for why they must not be a separate call.
        var sent = SendChord(PasteChords.ForMethod(method), releases);
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
            RestoreClipboardLater(previousText, sequenceAtOurSet);

        return PasteOutcome.Success;
    }

    /// <summary>
    /// Places the transcript on the clipboard and reports whether it really
    /// landed. Distinguished from the input path because it is the one failure
    /// the user can always work around (paste it themselves).
    ///
    /// <para>The write is confirmed with <c>GetClipboardSequenceNumber</c> rather
    /// than trusted from the absence of an exception. The number increments on
    /// every clipboard change, so an unmoved value means the write did not take
    /// — which is the only way to catch a silent failure. <c>copy: true</c> is
    /// what makes this reliable: it flushes the data synchronously before
    /// returning, so the number has already advanced.</para>
    /// </summary>
    private static (PasteOutcome? Outcome, uint SequenceNumber) TrySetClipboard(string text)
    {
        try
        {
            SetClipboardTextWithRetry(text);
        }
        catch (IOException ex)
        {
            Logger.Warning($"paste: clipboard unavailable — {ex.Message}");
            return (PasteOutcome.Failure(PasteFailureReason.ClipboardBusy, ex.Message), 0);
        }
        catch (Exception ex)
        {
            Logger.Warning($"paste: clipboard error ({ex.GetType().Name}) — {ex.Message}");
            return (PasteOutcome.Failure(PasteFailureReason.ClipboardBusy, ex.GetType().Name), 0);
        }

        var sequence = GetClipboardSequenceNumber();
        return (null, sequence);
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
    /// <summary>
    /// Returns key-up events for any physically held modifiers, after briefly
    /// waiting for the user to release them.
    ///
    /// <para>The releases are *returned* rather than sent because they must
    /// travel in the same <c>SendInput</c> array as the chord. Sending them in a
    /// separate call lets the modifier messages slip back into the queue and
    /// re-press the modifier, which defeats the purpose.</para>
    ///
    /// <para>A Win-key release is wrapped with an intervening Ctrl down/up.
    /// Windows opens the Start menu on a Win press with no intervening key
    /// event, so a bare Win-up could pop Start over the user's work.</para>
    /// </summary>
    private static List<INPUT> NeutralizeHeldModifiers()
    {
        const int waitBudgetMs = 400;
        var deadline = Environment.TickCount64 + waitBudgetMs;
        while (AnyModifierDown() && Environment.TickCount64 < deadline)
            Thread.Sleep(15);

        var releases = new List<INPUT>();
        foreach (var vk in PhysicalModifierVks)
        {
            if (!IsKeyDown(vk))
            {
                continue;
            }

            if (vk is VK_LWIN or VK_RWIN)
            {
                // Guard the Start-menu side effect.
                releases.Add(KeyDown(VK_CONTROL));
                releases.Add(KeyUp(vk));
                releases.Add(KeyUp(VK_CONTROL));
            }
            else
            {
                releases.Add(KeyUp(vk));
            }
        }

        if (releases.Count > 0)
        {
            Logger.Debug($"paste: queued {releases.Count} modifier release event(s) for the chord");
        }

        return releases;
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

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
    /// Puts the user's previous clipboard text back once the target app has had
    /// time to read the transcript.
    ///
    /// <para><paramref name="sequenceAtOurSet"/> guards against data loss: if the
    /// clipboard sequence number has moved past the value recorded right after
    /// our own write, something else owns the clipboard now — the user copied
    /// something themselves, or another app set it. Restoring then would
    /// silently destroy their copy, so we leave it alone.</para>
    /// </summary>
    private static void RestoreClipboardLater(string previousText, uint sequenceAtOurSet)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(800);
            try
            {
                if (GetClipboardSequenceNumber() != sequenceAtOurSet)
                {
                    Logger.Debug("paste: skipping clipboard restore (clipboard changed since our write)");
                    return;
                }

                System.Windows.Forms.Clipboard.SetDataObject(previousText, copy: true);
            }
            catch { /* best-effort restore */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
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

        // GetWindowThreadProcessId returns 0 on failure and leaves the
        // out-parameter untouched, so a zero return must be treated as "unknown"
        // rather than reading a stale pid.
        uint pid;
        var threadId = GetWindowThreadProcessId(hwnd, out pid);
        if (threadId == 0 || pid == 0)
        {
            // Fail open: an uninspectable target is treated as reachable, so
            // "couldn't tell" never becomes "didn't try".
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
        // higher-integrity one. Detect it up front, because SendInput reports
        // NOTHING when this happens — it returns the full count with
        // GetLastError == 0, and Windows drops the events later. There is no
        // post-hoc signal to rely on, so this pre-check is the only one.
        try
        {
            var comparison = CompareIntegrityLevels(pid);
            if (comparison > 0)
            {
                Logger.Warning(
                    $"paste: target pid {pid} has a higher integrity level than Voice IME "
                    + "(own rid {OwnIntegrityRid()}) — UIPI drops the chord silently");
                return PasteOutcome.Failure(PasteFailureReason.TargetElevated);
            }
        }
        catch
        {
            // Token probe unavailable — fail open and let the paste attempt run.
        }

        return null;
    }

    /// <summary>
    /// Compares the target process's integrity level with our own.
    /// Returns 1 when the target is higher (the paste would be dropped),
    /// 0 when equal, -1 when lower. Throws when the level cannot be determined
    /// — callers treat that as "unknown" and fail open.
    ///
    /// <para>A boolean "is elevated" test is the wrong predicate: UIPI's rule is
    /// "equal or lesser integrity level", and a boolean mis-classifies low-IL
    /// and UIAccess targets. The integrity RID is compared instead.</para>
    /// </summary>
    private static int CompareIntegrityLevels(uint targetPid)
    {
        var own = OwnIntegrityRid();
        var target = GetIntegrityRid(targetPid);
        return target.CompareTo(own);
    }

    /// <summary>
    /// Our own integrity RID, cached.
    ///
    /// <para>Caching matters for correctness, not just speed: the comparison
    /// must be relative. An earlier version asked only "is the target
    /// elevated?", which meant that running Voice IME *as administrator* — the
    /// very workaround the error message suggests — made it refuse to paste
    /// into any elevated app, even though equal-integrity injection is
    /// permitted and works.</para>
    /// </summary>
    private static uint OwnIntegrityRid()
    {
        if (_ownIntegrityRid is uint cached)
        {
            return cached;
        }

        var rid = GetIntegrityRid(GetCurrentProcessId());
        _ownIntegrityRid = rid;
        return rid;
    }

    private static uint? _ownIntegrityRid;

    /// <summary>
    /// Reads a process token's mandatory-label RID, which encodes its integrity
    /// level. Opening a higher-integrity token fails with access-denied, which
    /// is itself the signal we want — it is reported as an unreachable level.
    /// </summary>
    private static uint GetIntegrityRid(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            // System/CSRSS/protected processes refuse this for reasons unrelated
            // to integrity. Throw so the caller fails open rather than guessing.
            throw new InvalidOperationException(
                $"OpenProcess failed (win32={Marshal.GetLastWin32Error()})");
        }

        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            {
                // An elevated token is protected from a non-elevated opener, so
                // this usually means the target is higher than us.
                throw new InvalidOperationException(
                    $"OpenProcessToken denied (win32={Marshal.GetLastWin32Error()})");
            }

            try
            {
                // TokenIntegrityLevel returns a TOKEN_MANDATORY_LABEL whose Sid
                // points at the RID in its last sub-authority.
                uint size = 0;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out size);
                if (size == 0)
                {
                    throw new InvalidOperationException("integrity-level size unavailable");
                }

                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                    {
                        throw new InvalidOperationException(
                            $"GetTokenInformation failed (win32={Marshal.GetLastWin32Error()})");
                    }

                    var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
                    // SubAuthorityCount - 1 is the last RID.
                    var count = Marshal.ReadByte(label.Sid, 1);
                    return (uint)Marshal.ReadInt32(
                        label.Sid,
                        8 + (4 * (count - 1))); // Sid header 8 bytes, 4-byte subauthorities
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
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

    private static PasteOutcome SendChord(PasteChord chord, List<INPUT> preEvents)
    {
        var inputs = new List<INPUT>(preEvents.Count + chord.Modifiers.Length * 2 + 2);
        inputs.AddRange(preEvents);
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
        // A cbSize mismatch makes SendInput fail for every target identically,
        // so log it as a programming error rather than a user-facing one.
        if (InputStructSize != ExpectedInputStructSize)
        {
            Logger.Error(
                $"paste: BUG — INPUT marshals to {InputStructSize} bytes, "
                + $"expected {ExpectedInputStructSize}; SendInput will reject every call");
        }

        var sent = SendInput((uint)inputs.Length, inputs, InputStructSize);
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
