namespace VoiceIme;

/// <summary>
/// Sidebar sections hosted by <see cref="MainWindow"/>. Declaration order
/// matches the sidebar top-to-bottom. Later tasks plug a Views/* UserControl
/// into each value via <see cref="MainWindow.NavigateTo"/>.
/// </summary>
public enum MainSection
{
    General,
    History,
    Gemini,
    Advanced,
    About,
}
