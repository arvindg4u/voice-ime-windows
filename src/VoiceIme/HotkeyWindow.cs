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

    public void Register(uint modifiers, uint vk)
    {
        _registered = NativeInput.TryRegisterHotKey(Handle, _id, modifiers, vk);
    }

    protected override void WndProc(ref Message m)
    {
        if (NativeInput.IsHotKeyMessage(m.Msg) && m.WParam.ToInt32() == _id)
            _onHotkey();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered) NativeInput.TryUnregisterHotKey(Handle, _id);
        DestroyHandle();
    }
}
