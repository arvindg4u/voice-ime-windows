namespace VoiceIme;

/// <summary>
/// A paste chord: the modifier keys held while the commit key is tapped.
/// Canonical method first (unknown methods already coerced to Ctrl+V), then
/// the modifier VKs in press order, then the commit key.
/// </summary>
public sealed record PasteChord(string Method, ushort[] Modifiers, ushort Key);

/// <summary>
/// SendInput chord vocabulary behind <see cref="NativeInput"/> delivery.
/// Pure mapping from the persisted <see cref="PasteMethods"/> string to VK
/// codes — unit-testable on any OS; the actual SendInput call stays in
/// <see cref="NativeInput"/>. Unknown methods coerce to Ctrl+V, never throw.
/// </summary>
public static class PasteChords
{
    public const ushort VkControl = 0x11;
    public const ushort VkShift = 0x10;
    public const ushort VkV = 0x56;
    public const ushort VkInsert = 0x2D;
    public const ushort VkReturn = 0x0D;

    public static PasteChord ForMethod(string? method) => method switch
    {
        PasteMethods.ShiftInsert => new PasteChord(PasteMethods.ShiftInsert, [VkShift], VkInsert),
        PasteMethods.CtrlShiftV => new PasteChord(PasteMethods.CtrlShiftV, [VkControl, VkShift], VkV),
        _ => new PasteChord(PasteMethods.CtrlV, [VkControl], VkV),
    };
}
