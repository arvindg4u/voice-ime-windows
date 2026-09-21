namespace VoiceIme;

/// <summary>
/// Sidebar sections hosted by <see cref="MainWindow"/>. Declaration order
/// matches the sidebar top-to-bottom; each value is backed by a Views/*
/// UserControl registered through <see cref="MainWindow.NavigateTo"/>.
/// </summary>
public enum MainSection
{
    General,
    History,
    Gemini,
    Advanced,
    About,
}
