using System;
using System.Collections.Generic;
using System.Linq;
using VoiceIme.Views;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// General settings screen tests. WPF construction needs an STA thread plus a
/// window station — on headless runners construction throws and the test
/// passes vacuously (same pattern as <c>MainWindowTests</c>). Full coverage
/// runs on windows-latest CI. The view is built with a no-op saver so tests
/// never touch disk or DPAPI.
/// </summary>
[Collection("WpfSta")]
public sealed class GeneralSettingsViewTests
{
    [Fact]
    public void Defaults_LoadIntoControls()
    {
        TryRunOnSta(new SettingsStore(), new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            Assert.Equal(HotkeyChord.DefaultChord, view.HotkeyLabel);
            Assert.Equal(3, view.ActivationItemCount);
            Assert.Equal("Toggle", view.SelectedActivationLabel);
            Assert.Equal("System default", view.SelectedMicrophone);
            Assert.False(view.MuteChecked == true);
            Assert.False(view.IsHotkeyErrorVisible);
        });
    }

    [Fact]
    public void MicrophoneList_Empty_ShowsOnlySystemDefault()
    {
        TryRunOnSta(new SettingsStore(), new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            Assert.Equal(1, view.MicrophoneItemCount);
            Assert.Equal("System default", view.SelectedMicrophone);
        });
    }

    [Fact]
    public void MicrophoneList_KnownDevice_SelectsStored()
    {
        var store = new SettingsStore { Microphone = "Other Mic" };
        TryRunOnSta(store, new NullHotkeyRegistrar(), new[] { "Other Mic" }, view =>
        {
            Assert.Equal("Other Mic", view.SelectedMicrophone);
        });
    }

    [Fact]
    public void MicrophoneList_UnpluggedDevice_ShowsUnavailableEntry()
    {
        var store = new SettingsStore { Microphone = "Gone Mic" };
        TryRunOnSta(store, new NullHotkeyRegistrar(), new[] { "Other Mic" }, view =>
        {
            Assert.Equal("Gone Mic (unavailable)", view.SelectedMicrophone);
        });
    }

    [Fact]
    public void ApplyHotkey_Success_UpdatesStoreChipAndSaves()
    {
        var store = new SettingsStore();
        var saved = new List<string>();
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyHotkey("Alt+F4", HotkeyChord.DefaultChord);

            Assert.Equal("Alt+F4", store.Hotkey);
            Assert.Equal("Alt+F4", view.HotkeyLabel);
            Assert.False(view.IsHotkeyErrorVisible);
            Assert.True(saved.SequenceEqual(["Alt+F4"]));
        }, saver: s => saved.Add(s.Hotkey));
    }

    [Fact]
    public void ApplyHotkey_InvalidChord_ShowsErrorAndKeepsOld()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyHotkey("bogus", HotkeyChord.DefaultChord);

            Assert.Equal(HotkeyChord.DefaultChord, store.Hotkey);
            Assert.Equal(HotkeyChord.DefaultChord, view.HotkeyLabel);
            Assert.True(view.IsHotkeyErrorVisible);
            Assert.Contains("Kept", view.HotkeyErrorText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ApplyHotkey_RegisterConflict_RollsBackAndShowsError()
    {
        var store = new SettingsStore { Hotkey = HotkeyChord.DefaultChord };
        var registrar = new FakeRegistrar("Alt+F4");
        TryRunOnSta(store, registrar, Array.Empty<string>(), view =>
        {
            view.ApplyHotkey("Alt+F4", HotkeyChord.DefaultChord);

            Assert.Equal(HotkeyChord.DefaultChord, store.Hotkey);
            Assert.Equal(
                new[] { "Alt+F4", HotkeyChord.DefaultChord },
                registrar.Attempts.ToArray());
            Assert.True(view.IsHotkeyErrorVisible);
            Assert.Contains("Kept", view.HotkeyErrorText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void BindsInjectedStore_ExposesSameInstance()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            Assert.Same(store, view.BoundSettings);
        });
    }

    [Fact]
    public void ApplyHotkey_Success_RaisesSavedForTrayRefresh()
    {
        var store = new SettingsStore();
        SettingsStore? raised = null;
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.Saved += s => raised = s;
            view.ApplyHotkey("Alt+F4", HotkeyChord.DefaultChord);

            Assert.Same(store, raised);
        });
    }

    [Fact]
    public void ApplyHotkey_Failure_DoesNotRaiseSaved()
    {
        var store = new SettingsStore();
        var raised = false;
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.Saved += _ => raised = true;
            view.ApplyHotkey("bogus", HotkeyChord.DefaultChord);

            Assert.False(raised);
        });
    }

    [Fact]
    public void ReloadFromSettings_ExternalChange_RefreshesChip()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            store.Hotkey = "Alt+F4";
            view.ReloadFromSettings();

            Assert.Equal("Alt+F4", view.HotkeyLabel);
        });
    }

    [Fact]
    public void MainWindow_DefaultCtor_HostsGeneralView()
    {
        RunOnStaWindow(window =>
        {
            Assert.IsType<GeneralSettingsView>(window.SectionView(MainSection.General));
        }, () => new MainWindow());
    }

    [Fact]
    public void MainWindow_NullFactory_ShowsPlaceholder()
    {
        RunOnStaWindow(window =>
        {
            Assert.Null(window.SectionView(MainSection.General));
        }, () => new MainWindow((Func<object?>?)null));
    }

    // Task 6 (Handy parity gaps): Sound atoms + cancel-key row. Same
    // headless-vacuous STA pattern; no-op saver so tests never touch disk.

    [Fact]
    public void Defaults_LoadSoundAndCancelRows()
    {
        TryRunOnSta(new SettingsStore(), new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            Assert.Equal(3, view.ChannelItemCount);
            Assert.Equal("Mono", view.SelectedChannelLabel);
            Assert.Equal(1, view.OutputItemCount);
            Assert.Equal("System default", view.SelectedOutput);
            Assert.Equal(100, view.VolumePercent);
            Assert.Equal("100%", view.VolumeLabelText);
            Assert.False(view.FeedbackChecked == true);
            Assert.Equal(SettingsStore.DefaultCancelHotkey, view.CancelHotkeyLabel);
            Assert.False(view.IsCancelHotkeyErrorVisible);
        });
    }

    [Fact]
    public void ApplyChannel_Success_PersistsAndSaves()
    {
        var store = new SettingsStore();
        var saves = 0;
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyChannel("Stereo");

            Assert.Equal(AudioChannels.Stereo, store.Channel);
            Assert.Equal("Stereo", view.SelectedChannelLabel);
            Assert.Equal(1, saves);
        }, saver: _ => saves++);
    }

    [Fact]
    public void ApplyChannel_UnknownLabel_CoercesToMono()
    {
        var store = new SettingsStore { Channel = AudioChannels.Stereo };
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyChannel("Surround 7.1");

            Assert.Equal(AudioChannels.Mono, store.Channel);
            Assert.Equal("Mono", view.SelectedChannelLabel);
        });
    }

    [Fact]
    public void OutputList_KnownDevice_SelectsStored()
    {
        var store = new SettingsStore { OutputDevice = "Speakers" };
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            Assert.Equal("Speakers", view.SelectedOutput);
        }, outputs: new[] { "Speakers" });
    }

    [Fact]
    public void OutputList_UnpluggedDevice_ShowsUnavailableEntry()
    {
        var store = new SettingsStore { OutputDevice = "Gone Speakers" };
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            Assert.Equal("Gone Speakers (unavailable)", view.SelectedOutput);
        }, outputs: new[] { "Other Speakers" });
    }

    [Fact]
    public void ApplyOutputDevice_SystemDefault_ClearsStored()
    {
        var store = new SettingsStore { OutputDevice = "Speakers" };
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyOutputDevice(OutputDevices.SystemDefaultLabel);

            Assert.Equal("", store.OutputDevice);
        }, outputs: new[] { "Speakers" });
    }

    [Fact]
    public void ApplyOutputDevice_Unavailable_IgnoredKeepsStored()
    {
        var store = new SettingsStore { OutputDevice = "Gone Speakers" };
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyOutputDevice("Gone Speakers (unavailable)");

            Assert.Equal("Gone Speakers", store.OutputDevice);
        }, outputs: new[] { "Other Speakers" });
    }

    [Fact]
    public void ApplyVolume_ClampsPersistsAndLabels()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyVolume(42);
            Assert.Equal(42, store.Volume);
            Assert.Equal(42, view.VolumePercent);
            Assert.Equal("42%", view.VolumeLabelText);

            view.ApplyVolume(9999);
            Assert.Equal(SettingsStore.MaxVolume, store.Volume);
            Assert.Equal("100%", view.VolumeLabelText);

            view.ApplyVolume(-7);
            Assert.Equal(SettingsStore.MinVolume, store.Volume);
            Assert.Equal("0%", view.VolumeLabelText);
        });
    }

    [Fact]
    public void ApplyFeedback_Success_PersistsAndRaisesSaved()
    {
        var store = new SettingsStore();
        SettingsStore? raised = null;
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.Saved += s => raised = s;
            view.ApplyFeedback(true);

            Assert.True(store.AudioFeedback);
            Assert.True(view.FeedbackChecked == true);
            Assert.Same(store, raised);
        });
    }

    [Fact]
    public void ApplyCancelHotkey_BareEsc_Success_PersistsAndClearsError()
    {
        var store = new SettingsStore { CancelHotkey = "F5" };
        var saved = new List<string>();
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyCancelHotkey("Esc", "F5");

            Assert.Equal("Esc", store.CancelHotkey);
            Assert.Equal("Esc", view.CancelHotkeyLabel);
            Assert.False(view.IsCancelHotkeyErrorVisible);
            Assert.True(saved.SequenceEqual(["Esc"]));
        }, saver: s => saved.Add(s.CancelHotkey));
    }

    [Fact]
    public void ApplyCancelHotkey_FullChord_Success_Persists()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyCancelHotkey("Ctrl+Esc", SettingsStore.DefaultCancelHotkey);

            Assert.Equal("Ctrl+Esc", store.CancelHotkey);
            Assert.Equal("Ctrl+Esc", view.CancelHotkeyLabel);
            Assert.False(view.IsCancelHotkeyErrorVisible);
        });
    }

    [Fact]
    public void ApplyCancelHotkey_Invalid_KeepsOldAndShowsError()
    {
        var store = new SettingsStore { CancelHotkey = "Esc" };
        var saved = false;
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            view.ApplyCancelHotkey("bogus", "Esc");

            Assert.Equal("Esc", store.CancelHotkey);
            Assert.Equal("Esc", view.CancelHotkeyLabel);
            Assert.True(view.IsCancelHotkeyErrorVisible);
            Assert.Contains("Kept", view.CancelHotkeyErrorText, StringComparison.Ordinal);
            Assert.False(saved);
        }, saver: _ => saved = true);
    }

    [Fact]
    public void CommitCancelCapture_BareKey_PersistsCancelAndRearmsDictation()
    {
        var store = new SettingsStore { Hotkey = "Alt+F4", CancelHotkey = "F5" };
        var registrar = new FakeRegistrar();
        TryRunOnSta(store, registrar, Array.Empty<string>(), view =>
        {
            view.CommitCancelCapture(0, 0x1B);

            Assert.Equal("Esc", store.CancelHotkey);
            Assert.Equal("Esc", view.CancelHotkeyLabel);
            Assert.False(view.IsCancelHotkeyErrorVisible);
            // The dictation hotkey is re-armed; the bare Esc is never offered
            // to the dictation registrar (it would die as a global hotkey).
            Assert.Equal(new[] { "Alt+F4" }, registrar.Attempts.ToArray());
        });
    }

    [Fact]
    public void CommitCancelCapture_FullChord_PersistsChordAndRearmsDictation()
    {
        var store = new SettingsStore { Hotkey = "Alt+F4" };
        var registrar = new FakeRegistrar();
        TryRunOnSta(store, registrar, Array.Empty<string>(), view =>
        {
            view.CommitCancelCapture(HotkeyChord.ModControl, 0x1B);

            Assert.Equal("Ctrl+Esc", store.CancelHotkey);
            Assert.Equal(new[] { "Alt+F4" }, registrar.Attempts.ToArray());
        });
    }

    [Fact]
    public void CommitCancelCapture_IncompleteKey_KeepsOldRearmsDictationShowsError()
    {
        var store = new SettingsStore { Hotkey = "Alt+F4", CancelHotkey = "Esc" };
        var registrar = new FakeRegistrar();
        TryRunOnSta(store, registrar, Array.Empty<string>(), view =>
        {
            view.CommitCancelCapture(0, null);

            Assert.Equal("Esc", store.CancelHotkey);
            Assert.Equal(new[] { "Alt+F4" }, registrar.Attempts.ToArray());
            Assert.True(view.IsCancelHotkeyErrorVisible);
            Assert.Contains("Kept", view.CancelHotkeyErrorText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RestoreDictationHotkey_Failure_ShowsErrorOnCancelRow()
    {
        var store = new SettingsStore { Hotkey = "Alt+F4" };
        var registrar = new FakeRegistrar("Alt+F4");
        TryRunOnSta(store, registrar, Array.Empty<string>(), view =>
        {
            view.RestoreDictationHotkey("Alt+F4");

            Assert.True(view.IsCancelHotkeyErrorVisible);
            Assert.Contains("Alt+F4", view.CancelHotkeyErrorText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ReloadFromSettings_ExternalSoundChange_RefreshesRows()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, new NullHotkeyRegistrar(), Array.Empty<string>(), view =>
        {
            store.Channel = AudioChannels.Stereo;
            store.Volume = 42;
            store.AudioFeedback = true;
            store.CancelHotkey = "F5";
            view.ReloadFromSettings();

            Assert.Equal("Stereo", view.SelectedChannelLabel);
            Assert.Equal(42, view.VolumePercent);
            Assert.Equal("42%", view.VolumeLabelText);
            Assert.True(view.FeedbackChecked == true);
            Assert.Equal("F5", view.CancelHotkeyLabel);
        });
    }

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        private readonly HashSet<string> _failures;

        public FakeRegistrar(params string[] failures)
        {
            _failures = new HashSet<string>(failures, StringComparer.Ordinal);
        }

        public readonly List<string> Attempts = new();

        public void Unregister()
        {
        }

        public bool TryRegister(string chord, out string? error)
        {
            Attempts.Add(chord);
            if (_failures.Contains(chord))
            {
                error = $"Couldn't register {chord} — another app may already use it.";
                return false;
            }

            error = null;
            return true;
        }
    }

    private static void TryRunOnSta(
        SettingsStore store,
        IHotkeyRegistrar registrar,
        IReadOnlyList<string> microphones,
        Action<GeneralSettingsView> body,
        Action<SettingsStore>? saver = null,
        IReadOnlyList<string>? outputs = null)
    {
        StaTestHelper.TryRunOnSta(() =>
        {
            var view = new GeneralSettingsView(
                store, registrar, () => microphones, saver ?? (_ => { }),
                listOutputDevices: outputs is null ? OutputDevices.ListNames : () => outputs);
            body(view);
        });
    }

    private static void RunOnStaWindow(Action<MainWindow> body, Func<MainWindow> create)
    {
        MainWindow? window = null;
        StaTestHelper.TryRunOnSta(() =>
        {
            try
            {
                window = create();
                body(window);
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch
                {
                    // Close failures surface via the body exception path.
                }
            }
        });
    }
}
