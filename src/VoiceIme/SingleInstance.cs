using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace VoiceIme;

/// <summary>
/// Single-instance gate (Task 9): a named mutex owned by the first instance.
/// A second instance must show the running instance's settings window (via a
/// window-message broadcast) and exit — never a second tray icon.
/// Win32 surface is injectable so the ownership decision is unit-testable.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Broadcast message: first instance shows its settings window.</summary>
    public const string ShowSettingsMessageId = "VoiceIme.ShowSettings";

    private const uint HWND_BROADCAST = 0xFFFF;

    private readonly Func<string, bool, (Mutex Mutex, bool CreatedNew)> _createMutex;
    private readonly Action<uint, IntPtr, IntPtr> _broadcast;
    private readonly uint _showMessage;
    private Mutex? _mutex;
    private bool _disposed;

    public SingleInstance()
        : this(CreateOwnedMutex, BroadcastMessage, RegisterShowMessage())
    {
    }

    internal SingleInstance(
        Func<string, bool, (Mutex Mutex, bool CreatedNew)> createMutex,
        Action<uint, IntPtr, IntPtr> broadcast,
        uint showMessage)
    {
        _createMutex = createMutex;
        _broadcast = broadcast;
        _showMessage = showMessage;
    }

    /// <summary>
    /// True when this process is the first instance (mutex acquired).
    /// A second instance calls <see cref="NotifyRunningInstance"/> then exits.
    /// </summary>
    public bool IsFirstInstance { get; private set; }

    /// <summary>
    /// Acquires the named mutex. Returns false ONLY when another instance
    /// provably owns it (<c>createdNew == false</c> — creating an owned mutex
    /// on an existing name does NOT throw). Fail-open: an unexpected Win32
    /// error returns first-instance so the app still runs (a duplicate tray
    /// icon beats no app at all). Never throws.
    /// </summary>
    public bool Acquire(string name = "VoiceIme.SingleInstance")
    {
        try
        {
            var (mutex, createdNew) = _createMutex(name, true);
            if (!createdNew)
            {
                mutex.Dispose();
                IsFirstInstance = false;
                return false;
            }

            _mutex = mutex;
            IsFirstInstance = true;
            return true;
        }
        catch
        {
            // Unproven — no mutex was acquired, so nobody proved a first
            // instance exists. Run alone rather than exiting with none.
            IsFirstInstance = true;
            return true;
        }
    }

    /// <summary>
    /// Asks the running instance to show its settings window (no-op when
    /// there is no registered message, e.g. under test with message id 0).
    /// Never throws — failure just means the running window stays hidden.
    /// </summary>
    public void NotifyRunningInstance()
    {
        if (_showMessage == 0)
        {
            return;
        }

        try
        {
            _broadcast(_showMessage, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Best effort — the running instance may be shutting down.
        }
    }

    /// <summary>
    /// Registered id of the show-settings broadcast. HotkeyWindow compares
    /// its own registration of <see cref="ShowSettingsMessageId"/> against
    /// this to recognise the message.
    /// </summary>
    public uint ShowSettingsMessage => _showMessage;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // Not owned (never acquired or already released) — nothing to do.
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    private static (Mutex Mutex, bool CreatedNew) CreateOwnedMutex(string name, bool owned)
    {
        var mutex = new Mutex(owned, name, out var createdNew);
        return (mutex, createdNew);
    }

    private static void BroadcastMessage(uint message, IntPtr wParam, IntPtr lParam) =>
        PostMessage((IntPtr)HWND_BROADCAST, message, wParam, lParam);

    private static uint RegisterShowMessage()
    {
        try
        {
            return RegisterWindowMessage(ShowSettingsMessageId);
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
