namespace VoiceIme;

/// <summary>
/// Live-hotkey suspend/resume seam for the General settings screen. While the
/// user captures a new chord the live hotkey is unregistered so it neither
/// fires nor swallows keystrokes (Handy suspendAllBindings pattern); commit
/// re-registers, and a failed register rolls back to the previous binding
/// (Handy restore_registration pattern) with an inline error.
/// </summary>
public interface IHotkeyRegistrar
{
    void Unregister();

    bool TryRegister(string chord, out string? error);
}

/// <summary>
/// Validation-only registrar for standalone or test hosting: parses the
/// chord but makes no Win32 calls, so it runs on any OS.
/// </summary>
public sealed class NullHotkeyRegistrar : IHotkeyRegistrar
{
    public void Unregister()
    {
    }

    public bool TryRegister(string chord, out string? error)
    {
        if (HotkeyChord.TryParse(chord, out _, out _))
        {
            error = null;
            return true;
        }

        error = $"\"{chord}\" can't be used as a global hotkey — use at least one modifier plus a key.";
        return false;
    }
}
