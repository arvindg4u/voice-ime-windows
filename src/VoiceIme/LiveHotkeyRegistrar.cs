namespace VoiceIme;

/// <summary>
/// Production <see cref="IHotkeyRegistrar"/> backed by the app's
/// <see cref="HotkeyWindow"/>. Chord parsing is shared with the capture UI
/// via <see cref="HotkeyChord"/>; only the Win32 registration itself can
/// fail here (chord already owned by another app), and the caller rolls back
/// to the previous binding on failure.
/// </summary>
public sealed class LiveHotkeyRegistrar : IHotkeyRegistrar
{
    private readonly HotkeyWindow _window;

    public LiveHotkeyRegistrar(HotkeyWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    public void Unregister() => _window.Unregister();

    public bool TryRegister(string chord, out string? error)
    {
        if (!HotkeyChord.TryParse(chord, out var modifiers, out var vk))
        {
            error = $"\"{chord}\" can't be used as a global hotkey — use at least one modifier plus a key.";
            return false;
        }

        if (!_window.Register(modifiers, vk))
        {
            error = $"Couldn't register {chord} — another app may already use it.";
            return false;
        }

        error = null;
        return true;
    }
}
