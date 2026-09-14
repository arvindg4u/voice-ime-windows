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
    private bool _registered;

    public HotkeyWindow(int id, Action onHotkey)
    {
        _id = id;
        _onHotkey = onHotkey;
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
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
