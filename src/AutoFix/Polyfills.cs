// What the shared scan/fix source uses that .NET Framework 4.7.2 (the API level of the game's Mono) lacks.
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }

namespace System
{
    // for x[^1] and s[2..]
    internal readonly struct Index
    {
        readonly int value;
        public Index(int value, bool fromEnd = false) { this.value = fromEnd ? ~value : value; }
        public static implicit operator Index(int value) => new Index(value);
        public bool IsFromEnd => value < 0;
        public int Value => value < 0 ? ~value : value;
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
    }

    internal readonly struct Range
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new Range(start, new Index(0, true));
        public static Range EndAt(Index end) => new Range(new Index(0), end);
        public static Range All => new Range(new Index(0), new Index(0, true));
    }
}

static class NetFrameworkPolyfills
{
    public static IEnumerable<(A First, B Second)> Zip<A, B>(this IEnumerable<A> a, IEnumerable<B> b) => a.Zip(b, (x, y) => (x, y));
    public static bool TryAdd<K, V>(this Dictionary<K, V> d, K key, V value) { if (d.ContainsKey(key)) return false; d.Add(key, value); return true; }
    public static bool Remove<K, V>(this Dictionary<K, V> d, K key, out V value) { if (d.TryGetValue(key, out value)) { d.Remove(key); return true; } return false; }
}
#endif
