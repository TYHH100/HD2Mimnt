using HD2Mimnt.Infrastructure.Cache;
using Xunit;
using Xunit.Abstractions;

namespace HD2Mimnt.Tests.Cache;

public class LruCacheTests
{
    private readonly ITestOutputHelper _output;

    public LruCacheTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Add_And_Get_Should_Work()
    {
        // Arrange
        var cache = new LruCache<string, int>(capacity: 3);

        // Act
        cache.Add("key1", 1);
        cache.Add("key2", 2);
        cache.Add("key3", 3);

        // Assert
        Assert.True(cache.TryGet("key1", out var value1));
        Assert.Equal(1, value1);

        Assert.True(cache.TryGet("key2", out var value2));
        Assert.Equal(2, value2);

        Assert.True(cache.TryGet("key3", out var value3));
        Assert.Equal(3, value3);
    }

    [Fact]
    public void Should_Evict_LRU_Item_When_Capacity_Exceeded()
    {
        // Arrange
        var cache = new LruCache<string, int>(capacity: 3);

        // Act
        cache.Add("key1", 1);
        cache.Add("key2", 2);
        cache.Add("key3", 3);
        cache.Add("key4", 4); // 这会驱逐 key1

        // Assert
        Assert.False(cache.TryGet("key1", out _), "key1 should be evicted");
        Assert.True(cache.TryGet("key2", out _), "key2 should exist");
        Assert.True(cache.TryGet("key3", out _), "key3 should exist");
        Assert.True(cache.TryGet("key4", out _), "key4 should exist");
    }

    [Fact]
    public void Access_Should_Update_LRU_Order()
    {
        // Arrange
        var cache = new LruCache<string, int>(capacity: 3);

        // Act
        cache.Add("key1", 1);
        cache.Add("key2", 2);
        cache.Add("key3", 3);

        // 访问 key1，使其成为最近使用
        cache.TryGet("key1", out _);

        // 添加 key4，应该驱逐 key2（最久未使用）
        cache.Add("key4", 4);

        // Assert
        Assert.True(cache.TryGet("key1", out _), "key1 should exist (was accessed)");
        Assert.False(cache.TryGet("key2", out _), "key2 should be evicted (LRU)");
        Assert.True(cache.TryGet("key3", out _), "key3 should exist");
        Assert.True(cache.TryGet("key4", out _), "key4 should exist");
    }

    [Fact]
    public void Update_Existing_Key_Should_Not_Increase_Count()
    {
        // Arrange
        var cache = new LruCache<string, int>(capacity: 3);

        // Act
        cache.Add("key1", 1);
        cache.Add("key1", 100); // 更新

        // Assert
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("key1", out var value));
        Assert.Equal(100, value);
    }

    [Fact]
    public void Remove_Should_Work()
    {
        // Arrange
        var cache = new LruCache<string, int>(capacity: 3);
        cache.Add("key1", 1);
        cache.Add("key2", 2);

        // Act
        var removed = cache.Remove("key1");

        // Assert
        Assert.True(removed);
        Assert.False(cache.TryGet("key1", out _));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Clear_Should_Remove_All_Items()
    {
        // Arrange
        var cache = new LruCache<string, int>(capacity: 3);
        cache.Add("key1", 1);
        cache.Add("key2", 2);
        cache.Add("key3", 3);

        // Act
        cache.Clear();

        // Assert
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("key1", out _));
        Assert.False(cache.TryGet("key2", out _));
        Assert.False(cache.TryGet("key3", out _));
    }

    [Fact]
    public void Concurrent_Access_Should_Be_Thread_Safe()
    {
        // Arrange
        var cache = new LruCache<int, int>(capacity: 100);
        var tasks = new List<Task>();

        // Act - 多线程并发访问
        for (int i = 0; i < 10; i++)
        {
            int threadId = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    int key = threadId * 100 + j;
                    cache.Add(key, key);
                    cache.TryGet(key, out _);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert - 应该没有崩溃，并且缓存大小不超过容量
        Assert.True(cache.Count <= 100);
        _output.WriteLine($"Cache count after concurrent access: {cache.Count}");
    }
}
