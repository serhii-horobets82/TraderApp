using System.Collections;

namespace TraderApp.Backtest;

/// <summary>
/// A zero-copy sliding view over a list.
///
/// The replay hands the signal engine "history up to bar i" once per bar. Doing
/// that with Take().ToList() copies i elements each time, which is O(n^2) over a
/// run - about 9 million candle copies per market on a 45-day 15m series, and
/// enough to make a parameter sweep take hours instead of seconds.
/// </summary>
internal sealed class ListWindow<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _source;
    private readonly int _offset;

    /// <summary>Views source[endExclusive-maxCount .. endExclusive).</summary>
    public ListWindow(IReadOnlyList<T> source, int endExclusive, int maxCount)
    {
        _source = source;
        endExclusive = Math.Clamp(endExclusive, 0, source.Count);
        Count = Math.Min(maxCount, endExclusive);
        _offset = endExclusive - Count;
    }

    public int Count { get; }

    public T this[int index] =>
        index >= 0 && index < Count
            ? _source[_offset + index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return _source[_offset + i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
