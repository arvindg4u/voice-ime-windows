using System.Windows;
using System.Windows.Input;

namespace VoiceIme.Views;

/// <summary>
/// Task 6 cancel-key capture row (partial of <see cref="GeneralSettingsView"/>).
/// Reuses the hotkey-capture machinery through a unified capture target: the
/// dictation row requires a modifier+key chord (<c>HotkeyChord.TryParse</c>)
/// while cancel also accepts a bare key ("Esc") parsed via
/// <c>HotkeyChord.TryParseWithBareKey</c> with rollback to the previous value
/// on failure. A bare key here is safe because App arms the cancel hotkey
/// only while busy (recording/uploading), then unregisters it — never a
/// global always-on registration.
/// </summary>
public partial class GeneralSettingsView
{
    /// <summary>Which capture chip is currently recording keys.</summary>
    internal enum CaptureTarget
    {
        Hotkey,
        Cancel,
    }

    /// <summary>Test seam: the cancel chip's current label.</summary>
    internal string CancelHotkeyLabel => CancelHotkeyText.Text;

    /// <summary>Test seam: inline cancel error text.</summary>
    internal string CancelHotkeyErrorText => CancelHotkeyError.Text;

    /// <summary>Test seam: whether the inline cancel error is shown.</summary>
    internal bool IsCancelHotkeyErrorVisible => CancelHotkeyError.Visibility == Visibility.Visible;

    /// <summary>Test seam: which capture is in progress, if any.</summary>
    internal CaptureTarget? ActiveCaptureTarget => _capturing ? _captureTarget : null;

    private CaptureTarget _captureTarget = CaptureTarget.Hotkey;

    private void RefreshCancelRow()
    {
        CancelHotkeyText.Text = _settings.CancelHotkey;
        CancelHotkeyError.Visibility = Visibility.Collapsed;
    }

    private void CancelHotkeyChip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        StartCapture(CaptureTarget.Cancel);
    }

    private void CancelHotkeyChip_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturing && (e.Key == Key.Enter || e.Key == Key.Space))
        {
            e.Handled = true;
            StartCapture(CaptureTarget.Cancel);
        }
    }

    private void BeginCancelCapture()
    {
        _previousChord = _settings.CancelHotkey;
        CancelHotkeyText.Text = "press keys…";
        ClearCancelHotkeyError();
    }

    /// <summary>
    /// Test seam (internal): completes a cancel capture the way
    /// <see cref="CommitCapture"/> does on the last key-up — persists the
    /// captured key, then re-arms dictation (capture always suspends the
    /// live dictation registration).
    /// </summary>
    internal void CommitCancelCapture(uint mods, uint? vk)
    {
        var previous = _previousChord;
        var dictation = _settings.Hotkey;
        var display = mods == 0 && vk is uint bare
            ? HotkeyChord.KeyName(bare)
            : vk is uint full ? HotkeyChord.Format(mods, full) : null;
        EndCaptureMode();
        if (display is null)
        {
            RestoreDictationHotkey(dictation);
            ShowCancelHotkeyError(
                $"That isn't a complete key — press a single key or a modifier-plus-key chord. Kept {previous}.");
            return;
        }

        ApplyCancelHotkey(display, previous);
        RestoreDictationHotkey(dictation);
    }

    /// <summary>
    /// Re-arms the dictation hotkey after a cancel capture completes. Capture
    /// suspends the LIVE DICTATION registration (StartCapture calls
    /// <c>HotkeyRegistrar.Unregister</c>) — restoring the cancel chord would
    /// register a bare Esc as the global hotkey and leave dictation dead.
    /// Failures surface on the cancel row; the dictation chip shows its own
    /// restore path. Test-covered via FakeRegistrar attempts.
    /// </summary>
    internal void RestoreDictationHotkey(string dictation)
    {
        if (!HotkeyRegistrar.TryRegister(dictation, out var error))
        {
            var message = string.IsNullOrEmpty(error)
                ? $"Couldn't restore {dictation} — restart the app to re-arm the hotkey."
                : error;
            ShowCancelHotkeyError(message);
        }
    }

    private void ResetCancelButton_Click(object sender, RoutedEventArgs e) =>
        ApplyCancelHotkey(SettingsStore.DefaultCancelHotkey, _settings.CancelHotkey);

    /// <summary>
    /// Test seam (internal): validates then persists a cancel key; parses
    /// via <see cref="HotkeyChord.TryParseWithBareKey"/> and keeps the
    /// previous value on failure. Never touches the dictation registrar —
    /// the caller re-arms dictation (see
    /// <see cref="RestoreDictationHotkey"/>); App arms the cancel key itself
    /// while busy via <c>TryParseWithBareKey</c>.
    /// </summary>
    internal void ApplyCancelHotkey(string candidate, string rollbackTo)
    {
        if (!HotkeyChord.TryParseWithBareKey(candidate, out _, out _))
        {
            ShowCancelHotkeyError(
                $"\"{candidate}\" can't be used as a cancel key — press a single key or a modifier-plus-key chord. Kept {rollbackTo}.");
            return;
        }

        _settings.CancelHotkey = candidate.Trim();
        if (!TrySaveSettings(out var saveError))
        {
            ShowCancelHotkeyError(
                $"Cancel key is {_settings.CancelHotkey} for now, but saving failed: {saveError}");
        }
        else
        {
            ClearCancelHotkeyError();
        }

        RefreshCancelRow();
    }

    private void ShowCancelHotkeyError(string message)
    {
        CancelHotkeyError.Text = message;
        CancelHotkeyError.Visibility = Visibility.Visible;
    }

    private void ClearCancelHotkeyError() => CancelHotkeyError.Visibility = Visibility.Collapsed;
}
