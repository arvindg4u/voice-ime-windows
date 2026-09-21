using System;
using NAudio.CoreAudioApi;

namespace VoiceIme;

/// <summary>
/// Best-effort scope for the General "mute while recording" option. It mutes
/// the selected active render endpoint (or the multimedia default) and always
/// restores the exact previous mute state when recording ends. Audio-stack or
/// endpoint failures are intentionally non-fatal: recording still works.
/// </summary>
public static class AudioOutputMute
{
    public static IDisposable? TryMute(string? outputDevice)
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrWhiteSpace(outputDevice))
            {
                foreach (var candidate in enumerator.EnumerateAudioEndPoints(
                    DataFlow.Render, DeviceState.Active))
                {
                    if (OutputDevices.NamesMatch(outputDevice, candidate.FriendlyName))
                    {
                        device = candidate;
                        break;
                    }

                    // The collection creates a COM wrapper per endpoint; do
                    // not leave non-selected candidates alive until GC.
                    candidate.Dispose();
                }
            }

            device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var wasMuted = device.AudioEndpointVolume.Mute;
            device.AudioEndpointVolume.Mute = true;
            return new RestoreScope(enumerator, device, wasMuted);
        }
        catch
        {
            device?.Dispose();
            enumerator?.Dispose();
            return null;
        }
    }

    private sealed class RestoreScope(
        MMDeviceEnumerator enumerator, MMDevice device, bool wasMuted) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                device.AudioEndpointVolume.Mute = wasMuted;
            }
            catch
            {
                // Endpoint may have disappeared; there is nothing else to do.
            }
            finally
            {
                try { device.Dispose(); } catch { /* best effort */ }
                try { enumerator.Dispose(); } catch { /* best effort */ }
            }
        }
    }
}
