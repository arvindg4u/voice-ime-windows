using System;
using System.Windows.Forms;

namespace VoiceIme;

/// <summary>
/// Message-only window that owns the global hotkey registration.
/// Ctrl+Shift+Space by default — fires from any app, like Android's mic tap.
/// </summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly int _id;
    private readonly Action _onHotkey;
    private readonly Action? _onShowSettings;
    private readonly uint _showSettingsMessage;
    private bool _registered;

    public HotkeyWindow(int id, Action onHotkey)
        : this(id, onHotkey, onShowSettings: null)
    {
    }

    /// <summary>
    /// Task 9: the first instance also listens for the single-instance
    /// show-settings broadcast — a second instance's arrival raises
    /// <paramref name="onShowSettings"/> so App can show its window.
    /// </summary>
    public HotkeyWindow(int id, Action onHotkey, Action? onShowSettings)
    {
        _id = id;
        _onHotkey = onHotkey;
        _onShowSettings = onShowSettings;
        try
        {
            _showSettingsMessage = NativeMethods.RegisterWindowMessage(SingleInstance.ShowSettingsMessageId);
        }
        catch
        {
            _showSettingsMessage = 0;
        }

        CreateHandle(new CreateParams { Caption = "VoiceImeHotkey" });
    }

    /// <summary>
    /// (Re-)registers the global hotkey. Returns false when Windows rejects
    /// the chord (e.g. another app already owns it) — callers roll back to
    /// the previous binding instead of leaving dictation unreachable.
    /// </summary>
    public bool Register(uint modifiers, uint vk)
    {
        _registered = NativeInput.TryRegisterHotKey(Handle, _id, modifiers, vk);
        return _registered;
    }

    /// <summary>
    /// Suspends the live hotkey (e.g. while the settings screen captures a
    /// replacement chord) so it neither fires nor swallows keystrokes.
    /// </summary>
    public void Unregister()
    {
        NativeInput.TryUnregisterHotKey(Handle, _id);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (NativeInput.IsHotKeyMessage(m.Msg) && m.WParam.ToInt32() == _id)
            _onHotkey();
        if (_onShowSettings is not null && _showSettingsMessage != 0 && m.Msg == (int)_showSettingsMessage)
            _onShowSettings();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
