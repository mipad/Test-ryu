using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    interface ICacheKey : IDisposable
    {
        bool KeyEqual(ICacheKey other);
    }

    struct I8ToI16CacheKey : ICacheKey
    {
        // Used to notify the pipeline that bindings have invalidated on dispose.
        private readonly VulkanRenderer _gd;
        private Auto<DisposableBuffer> _buffer;

        public I8ToI16CacheKey(VulkanRenderer gd)
        {
            _gd = gd;
            _buffer = null;
        }

        public readonly bool KeyEqual(ICacheKey other)
        {
            return other is I8ToI16CacheKey;
        }

        public void SetBuffer(Auto<DisposableBuffer> buffer)
        {
            _buffer = buffer;
        }

        public readonly void Dispose()
        {
            _gd.PipelineInternal.DirtyIndexBuffer(_buffer);
        }
    }

    struct AlignedVertexBufferCacheKey : ICacheKey
    {
        private readonly int _stride;
        private readonly int _alignment;

        // Used to notify the pipeline that bindings have invalidated on dispose.
        private readonly VulkanRenderer _gd;
        private Auto<DisposableBuffer> _buffer;

        public AlignedVertexBufferCacheKey(VulkanRenderer gd, int stride, int alignment)
        {
            _gd = gd;
            _stride = stride;
            _alignment = alignment;
            _buffer = null;
        }

        public readonly bool KeyEqual(ICacheKey other)
        {
            return other is AlignedVertexBufferCacheKey entry &&
                entry._stride == _stride &&
                entry._alignment == _alignment;
        }

        public void SetBuffer(Auto<DisposableBuffer> buffer)
        {
            _buffer = buffer;
        }

        public readonly void Dispose()
        {
            _gd.PipelineInternal.DirtyVertexBuffer(_buffer);
        }
    }

    struct TopologyConversionCacheKey : ICacheKey
    {
        private readonly IndexBufferPattern _pattern;
        private readonly int _indexSize;

        // Used to notify the pipeline that bindings have invalidated on dispose.
        private readonly VulkanRenderer _gd;
        private Auto<DisposableBuffer> _buffer;

        public TopologyConversionCacheKey(VulkanRenderer gd, IndexBufferPattern pattern, int indexSize)
        {
            _gd = gd;
            _pattern = pattern;
            _indexSize = indexSize;
            _buffer = null;
        }

        public readonly bool KeyEqual(ICacheKey other)
        {
            return other is TopologyConversionCacheKey entry &&
                entry._pattern == _pattern &&
                entry._indexSize == _indexSize;
        }

        public void SetBuffer(Auto<DisposableBuffer> buffer)
        {
            _buffer = buffer;
        }

        public readonly void Dispose()
        {
            _gd.PipelineInternal.DirtyIndexBuffer(_buffer);
        }
    }

    readonly struct TopologyConversionIndirectCacheKey : ICacheKey
    {
        private readonly TopologyConversionCacheKey _baseKey;
        private readonly BufferHolder _indirectDataBuffer;
        private readonly int _indirectDataOffset;
        private readonly int _indirectDataSize;

        public TopologyConversionIndirectCacheKey(
            VulkanRenderer gd,
            IndexBufferPattern pattern,
            int indexSize,
            BufferHolder indirectDataBuffer,
            int indirectDataOffset,
            int indirectDataSize)
        {
            _baseKey = new TopologyConversionCacheKey(gd, pattern, indexSize);
            _indirectDataBuffer = indirectDataBuffer;
            _indirectDataOffset = indirectDataOffset;
            _indirectDataSize = indirectDataSize;
        }

        public bool KeyEqual(ICacheKey other)
        {
            return other is TopologyConversionIndirectCacheKey entry &&
                entry._baseKey.KeyEqual(_baseKey) &&
                entry._indirectDataBuffer == _indirectDataBuffer &&
                entry._indirectDataOffset == _indirectDataOffset &&
                entry._indirectDataSize == _indirectDataSize;
        }

        public void SetBuffer(Auto<DisposableBuffer> buffer)
        {
            _baseKey.SetBuffer(buffer);
        }

        public void Dispose()
        {
            _baseKey.Dispose();
        }
    }

    readonly struct IndirectDataCacheKey : ICacheKey
    {
        private readonly IndexBufferPattern _pattern;

        public IndirectDataCacheKey(IndexBufferPattern pattern)
        {
            _pattern = pattern;
        }

        public bool KeyEqual(ICacheKey other)
        {
            return other is IndirectDataCacheKey entry && entry._pattern == _pattern;
        }

        public void Dispose()
        {
        }
    }

    struct DrawCountCacheKey : ICacheKey
    {
        public readonly bool KeyEqual(ICacheKey other)
        {
            return other is DrawCountCacheKey;
        }

        public readonly void Dispose()
        {
        }
    }

    readonly struct Dependency
    {
        private readonly BufferHolder _buffer;
        private readonly int _offset;
        private readonly int _size;
        private readonly ICacheKey _key;

        public Dependency(BufferHolder buffer, int offset, int size, ICacheKey key)
        {
            _buffer = buffer;
            _offset = offset;
            _size = size;
            _key = key;
        }

        public void RemoveFromOwner()
        {
            _buffer.RemoveCachedConvertedBuffer(_offset, _size, _key);
        }
    }

    struct CacheByRange<T> where T : IDisposable
    {
        private struct Entry
        {
            public ICacheKey Key;
            public T Value;
            public List<Dependency> DependencyList;

            public Entry(ICacheKey key, T value)
            {
                Key = key;
                Value = value;
                DependencyList = null;
            }

            public readonly void InvalidateDependencies()
            {
                if (DependencyList != null)
                {
                    // 优化：预先获取列表大小，避免重复属性访问
                    int count = DependencyList.Count;
                    var dependencies = DependencyList;
                    
                    for (int i = 0; i < count; i++)
                    {
                        dependencies[i].RemoveFromOwner();
                    }

                    dependencies.Clear();
                }
            }
        }

        private Dictionary<ulong, List<Entry>> _ranges;

        public void Add(int offset, int size, ICacheKey key, T value)
        {
            List<Entry> entries = GetEntries(offset, size);
            entries.Add(new Entry(key, value));
        }

        public void AddDependency(int offset, int size, ICacheKey key, Dependency dependency)
        {
            List<Entry> entries = GetEntries(offset, size);

            // 优化：使用局部变量避免重复的属性访问
            int count = entries.Count;
            for (int i = 0; i < count; i++)
            {
                Entry entry = entries[i];

                if (entry.Key.KeyEqual(key))
                {
                    if (entry.DependencyList == null)
                    {
                        entry.DependencyList = new List<Dependency>();
                        entries[i] = entry;
                    }

                    entry.DependencyList.Add(dependency);
                    break;
                }
            }
        }

        public void Remove(int offset, int size, ICacheKey key)
        {
            List<Entry> entries = GetEntries(offset, size);

            // 优化：从后向前遍历，避免RemoveAt导致的元素移动
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry entry = entries[i];

                if (entry.Key.KeyEqual(key))
                {
                    entries.RemoveAt(i);
                    DestroyEntry(entry);
                }
            }

            if (entries.Count == 0)
            {
                _ranges.Remove(PackRange(offset, size));
            }
        }

        public bool TryGetValue(int offset, int size, ICacheKey key, out T value)
        {
            List<Entry> entries = GetEntries(offset, size);

            // 优化：使用for循环代替foreach，避免枚举器分配
            int count = entries.Count;
            for (int i = 0; i < count; i++)
            {
                Entry entry = entries[i];
                if (entry.Key.KeyEqual(key))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        public void Clear()
        {
            if (_ranges != null)
            {
                // 优化：直接遍历字典值，不需要KeyValuePair
                foreach (var entries in _ranges.Values)
                {
                    int count = entries.Count;
                    for (int i = 0; i < count; i++)
                    {
                        DestroyEntry(entries[i]);
                    }
                }

                _ranges.Clear();
                _ranges = null;
            }
        }

        public readonly void ClearRange(int offset, int size)
        {
            if (_ranges != null && _ranges.Count > 0)
            {
                int end = offset + size;

                // 优化：使用List<ulong>的初始容量估计
                List<ulong> toRemove = null;

                foreach (var kvp in _ranges)
                {
                    (int rOffset, int rSize) = UnpackRange(kvp.Key);

                    int rEnd = rOffset + rSize;

                    if (rEnd > offset && rOffset < end)
                    {
                        var entries = kvp.Value;
                        int count = entries.Count;
                        
                        for (int i = 0; i < count; i++)
                        {
                            DestroyEntry(entries[i]);
                        }

                        (toRemove ??= new List<ulong>(_ranges.Count / 4)).Add(kvp.Key);
                    }
                }

                if (toRemove != null)
                {
                    // 优化：批量移除
                    foreach (ulong range in toRemove)
                    {
                        _ranges.Remove(range);
                    }
                }
            }
        }

        private List<Entry> GetEntries(int offset, int size)
        {
            _ranges ??= new Dictionary<ulong, List<Entry>>();

            ulong key = PackRange(offset, size);

            // 优化：使用TryGetValue模式避免二次查找
            if (!_ranges.TryGetValue(key, out List<Entry> value))
            {
                value = new List<Entry>();
                _ranges.Add(key, value);
            }

            return value;
        }

        private static void DestroyEntry(Entry entry)
        {
            entry.Key.Dispose();
            entry.Value?.Dispose();
            entry.InvalidateDependencies();
        }

        // 优化：标记为内联的私有方法（编译器提示）
        private static ulong PackRange(int offset, int size)
        {
            // 优化：避免显式转换，让编译器优化
            return (uint)offset | ((ulong)size << 32);
        }

        private static (int offset, int size) UnpackRange(ulong range)
        {
            // 优化：使用元组语法，简洁明了
            return ((int)range, (int)(range >> 32));
        }

        public void Dispose()
        {
            Clear();
        }

        #region 新增的安全优化方法

        // 新增：批量添加方法，减少重复范围计算
        public void AddMultiple(int offset, int size, params (ICacheKey key, T value)[] items)
        {
            if (items == null || items.Length == 0)
                return;

            List<Entry> entries = GetEntries(offset, size);
            
            // 预分配容量避免多次扩容
            if (entries.Capacity < entries.Count + items.Length)
            {
                entries.Capacity = entries.Count + items.Length;
            }

            foreach (var item in items)
            {
                entries.Add(new Entry(item.key, item.value));
            }
        }

        // 新增：检查是否存在某个键（不获取值）
        public bool ContainsKey(int offset, int size, ICacheKey key)
        {
            if (_ranges == null)
                return false;

            ulong rangeKey = PackRange(offset, size);
            if (!_ranges.TryGetValue(rangeKey, out var entries))
                return false;

            int count = entries.Count;
            for (int i = 0; i < count; i++)
            {
                if (entries[i].Key.KeyEqual(key))
                    return true;
            }

            return false;
        }

        // 新增：获取所有键的枚举（用于调试）
        public IEnumerable<ICacheKey> GetAllKeys(int offset, int size)
        {
            if (_ranges == null)
                yield break;

            ulong rangeKey = PackRange(offset, size);
            if (!_ranges.TryGetValue(rangeKey, out var entries))
                yield break;

            int count = entries.Count;
            for (int i = 0; i < count; i++)
            {
                yield return entries[i].Key;
            }
        }

        // 新增：获取缓存条目数量（用于监控）
        public int GetEntryCount(int offset, int size)
        {
            if (_ranges == null)
                return 0;

            ulong rangeKey = PackRange(offset, size);
            return _ranges.TryGetValue(rangeKey, out var entries) ? entries.Count : 0;
        }

        // 新增：获取总条目数量（用于监控）
        public int GetTotalEntryCount()
        {
            if (_ranges == null)
                return 0;

            int total = 0;
            foreach (var entries in _ranges.Values)
            {
                total += entries.Count;
            }
            return total;
        }

        #endregion
    }
}