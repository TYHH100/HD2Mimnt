namespace HD2Mimnt.Infrastructure.Cache;

/// <summary>
/// LRU (Least Recently Used) 缓存实现
/// 线程安全的内存缓存
/// </summary>
public class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Action<TValue>? _onEvicted;
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cache;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly object _lock = new();

    /// <param name="onEvicted">条目被驱逐/移除/清空时的回调（如释放原生资源）；更新同键现有值时不触发。</param>
    public LruCache(int capacity, Action<TValue>? onEvicted = null)
    {
        if (capacity <= 0)
            throw new ArgumentException("Capacity must be greater than 0", nameof(capacity));

        _capacity = capacity;
        _onEvicted = onEvicted;
        _cache = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
        _lruList = new LinkedList<CacheItem>();
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                // 移到链表头部（最近使用）
                _lruList.Remove(node);
                _lruList.AddFirst(node);

                value = node.Value.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    public void Add(TKey key, TValue value)
    {
        TValue evicted = default!;
        var hasEvicted = false;
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var existingNode))
            {
                // 更新现有项
                _lruList.Remove(existingNode);
                existingNode.Value = new CacheItem(key, value);
                _lruList.AddFirst(existingNode);
            }
            else
            {
                // 添加新项
                if (_cache.Count >= _capacity)
                {
                    // 移除最久未使用的项
                    var lruNode = _lruList.Last;
                    if (lruNode != null)
                    {
                        _cache.Remove(lruNode.Value.Key);
                        _lruList.RemoveLast();
                        evicted = lruNode.Value.Value;
                        hasEvicted = true;
                    }
                }

                var newNode = new LinkedListNode<CacheItem>(new CacheItem(key, value));
                _lruList.AddFirst(newNode);
                _cache[key] = newNode;
            }
        }
        // 回调放到锁外执行，避免回调重入缓存时死锁
        if (hasEvicted)
        {
            _onEvicted?.Invoke(evicted);
        }
    }

    public bool Remove(TKey key)
    {
        TValue removed = default!;
        var hasRemoved = false;
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _cache.Remove(key);
                removed = node.Value.Value;
                hasRemoved = true;
            }
        }
        if (hasRemoved)
        {
            _onEvicted?.Invoke(removed);
        }
        return hasRemoved;
    }

    public void Clear()
    {
        List<TValue>? evicted = null;
        lock (_lock)
        {
            if (_onEvicted != null)
            {
                evicted = new List<TValue>(_cache.Count);
                foreach (var node in _lruList)
                {
                    evicted.Add(node.Value);
                }
            }
            _cache.Clear();
            _lruList.Clear();
        }
        // 回调放到锁外执行，避免回调重入缓存时死锁
        if (evicted != null)
        {
            foreach (var value in evicted)
            {
                _onEvicted!.Invoke(value);
            }
        }
    }

    public IEnumerable<TKey> GetKeys()
    {
        lock (_lock)
        {
            return _cache.Keys.ToList();
        }
    }

    private record struct CacheItem(TKey Key, TValue Value);
}
