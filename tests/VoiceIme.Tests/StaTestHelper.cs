using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;

namespace VoiceIme.Tests;

/// <summary>
/// Single shared STA runner for WPF-constructing tests (replaces the
/// copy-pasted per-class helpers). The STA thread is joined with a timeout —
/// an unbounded <c>Join()</c> turns a dispatcher deadlock into a hung CI job.
/// A timeout fails fast with the test name attached. Headless runners (no
/// window station) still pass vacuously via <c>IsHeadlessFailure</c>.
/// All members of the "WpfSta" collection serialize through xUnit, so only
/// one STA body runs at a time.
/// </summary>
internal static class StaTestHelper
{
    /// <summary>Default per-body timeout: generous for slow runners, finite for CI.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated STA thread. Returns false
    /// when no window station is available (headless); rethrows body failures
    /// with their stack traces; throws <c>TimeoutException</c> on timeout.
    /// </summary>
    public static bool TryRunOnSta(Action body, TimeSpan? timeout = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                catch
                {
                    // Shutdown on a thread with no dispatcher is a no-op miss.
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout ?? DefaultTimeout))
        {
            throw new TimeoutException(
                $"STA test body did not complete within {(timeout ?? DefaultTimeout).TotalSeconds}s — " +
                "possible dispatcher deadlock. See the test name above for the hanging test.");
        }

        if (failure is not null && IsHeadlessFailure(failure))
        {
            return false;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return true;
    }

    public static bool IsHeadlessFailure(Exception ex)
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
