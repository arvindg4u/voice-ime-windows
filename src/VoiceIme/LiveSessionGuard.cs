namespace VoiceIme;

/// <summary>
/// Hands out the id of the live session currently allowed to write the
/// transcript buffers. Callbacks must capture the id returned by
/// <see cref="Next"/> at session start and check <see cref="IsCurrent"/>
/// on every fire — never read a mutable sequence counter at fire time (all
/// live sessions would read the same converged value and the guard would
/// accept everything, including a previous session's late callback).
/// Direct port of the Android LiveSessionGuard.
/// </summary>
internal sealed class LiveSessionGuard
{
    // Sessions on different dictations may start concurrently (e.g. a stale
    // session's teardown racing a new session's startup), so both Next and
    // IsCurrent take this lock around current to keep ids monotonic and the
    // read of the latest id atomic with respect to the increment.
    private readonly object _gate = new();
    private long _current;

    /// <summary>Starts a new session era; returns its id (monotonic, from 1).</summary>
    public long Next()
    {
        lock (_gate)
        {
            _current += 1;
            return _current;
        }
    }

    /// <summary>True only for the id of the latest started session.</summary>
    public bool IsCurrent(long id)
    {
        lock (_gate)
        {
            return id != 0 && id == _current;
        }
    }
}
