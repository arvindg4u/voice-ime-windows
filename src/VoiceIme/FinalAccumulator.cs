using System.Text;

namespace VoiceIme;

/// <summary>
/// Accumulates finalized live-transcript fragments into one authoritative
/// string and returns only the new delta per call. Joins with longest
/// suffix/prefix overlap so a cumulative server resend collapses instead of
/// duplicating, disjoint fragments append, and exact echoes yield "" (the
/// caller skips onFinal but still completes its signal). Direct port of the
/// Android FinalAccumulator.
/// </summary>
internal sealed class FinalAccumulator
{
    private readonly StringBuilder _text = new();

    /// <summary>Appends <paramref name="incoming"/> final text; returns only the not-yet-seen delta.</summary>
    public string Append(string incoming)
    {
        if (string.IsNullOrEmpty(incoming))
            return "";
        var current = _text.ToString();
        var delta = incoming.Substring(MaxOverlap(current, incoming));
        _text.Append(delta);
        return delta;
    }

    /// <summary>Full accumulated final text.</summary>
    public string Snapshot() => _text.ToString();

    /// <summary>Longest k such that <paramref name="current"/> ends with the first k chars of <paramref name="incoming"/>.</summary>
    private static int MaxOverlap(string current, string incoming)
    {
        var max = Math.Min(current.Length, incoming.Length);
        for (var k = max; k >= 1; k--)
        {
            if (current.EndsWith(incoming.Substring(0, k), StringComparison.Ordinal))
                return k;
        }
        return 0;
    }
}
