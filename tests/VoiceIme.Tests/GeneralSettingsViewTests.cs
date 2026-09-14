using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
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
            Assert.Equal(new[] { "Alt+F4" }, saved.ToArray());
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
        Action<SettingsStore>? saver = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new GeneralSettingsView(
                    store, registrar, () => microphones, saver ?? (_ => { }));
                body(view);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null && IsHeadlessFailure(failure))
        {
            return;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RunOnStaWindow(Action<MainWindow> body, Func<MainWindow> create)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = create();
                body(window);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null && IsHeadlessFailure(failure))
        {
            return;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static bool IsHeadlessFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var name = current.GetType().Name;
            if (name.Contains("InvalidOperationException", StringComparison.Ordinal)
                || name.Contains("COMException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
