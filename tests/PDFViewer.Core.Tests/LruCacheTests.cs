using PDFViewer.Core.Caching;

namespace PDFViewer.Core.Tests;

public sealed class LruCacheTests
{
    [Fact]
    public void EvictsLeastRecentlyUsedWhenOverCapacity()
    {
        var evicted = new List<string>();
        var cache = new LruCache<string, int>(10, v => v, (k, _) => evicted.Add(k));

        cache.Set("a", 4);
        cache.Set("b", 4);
        cache.TryGetValue("a", out _); // a가 최근 사용됨
        cache.Set("c", 4); // 12 > 10 → b 제거

        Assert.Equal(["b"], evicted);
        Assert.True(cache.TryPeek("a", out _));
        Assert.False(cache.TryPeek("b", out _));
        Assert.Equal(8, cache.TotalCost);
    }

    [Fact]
    public void KeepsSingleOversizedItem()
    {
        var cache = new LruCache<int, int>(10, v => v);

        cache.Set(1, 100);

        Assert.True(cache.TryPeek(1, out var value));
        Assert.Equal(100, value);
    }

    [Fact]
    public void ReplacingKeyEvictsOldValue()
    {
        var evicted = new List<int>();
        var cache = new LruCache<int, int>(100, v => v, (_, v) => evicted.Add(v));

        cache.Set(1, 5);
        cache.Set(1, 7);

        Assert.Equal([5], evicted);
        Assert.Equal(7, cache.TotalCost);
    }

    [Fact]
    public void ClearEvictsEverything()
    {
        var evicted = 0;
        var cache = new LruCache<int, int>(100, v => v, (_, _) => evicted++);
        cache.Set(1, 1);
        cache.Set(2, 1);

        cache.Clear();

        Assert.Equal(2, evicted);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TotalCost);
    }
}
