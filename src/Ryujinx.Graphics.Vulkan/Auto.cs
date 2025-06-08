using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    interface IAuto
    {
        bool HasCommandBufferDependency(CommandBufferScoped cbs);

        void IncrementReferenceCount();
        void DecrementReferenceCount(int cbIndex);
        void DecrementReferenceCount();
    }

    interface IAutoPrivate : IAuto
    {
        void AddCommandBufferDependencies(CommandBufferScoped cbs);
    }

    interface IMirrorable<T> where T : IDisposable
    {
        Auto<T> GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored);
        void ClearMirrors(CommandBufferScoped cbs, int offset, int size);
    }

    class Auto<T> : IAutoPrivate, IDisposable where T : IDisposable
    {
        // 增加销毁状态标志
        private bool _isDisposed;
        // 增加线程安全锁
        private readonly object _refCountLock = new object();
        private int _referenceCount;
        private T _value;

        private readonly BitMap _cbOwnership;
        private readonly MultiFenceHolder _waitable;
        private readonly IAutoPrivate[] _referencedObjs;
        private readonly IMirrorable<T> _mirrorable;

        private bool _disposed;
        private bool _destroyed;

        public Auto(T value)
        {
            _referenceCount = 1;
            _value = value;
            _cbOwnership = new BitMap(CommandBufferPool.MaxCommandBuffers);
        }

        public Auto(T value, IMirrorable<T> mirrorable, MultiFenceHolder waitable, params IAutoPrivate[] referencedObjs) : this(value, waitable, referencedObjs)
        {
            _mirrorable = mirrorable;
        }

        public Auto(T value, MultiFenceHolder waitable, params IAutoPrivate[] referencedObjs) : this(value)
        {
            _waitable = waitable;
            _referencedObjs = referencedObjs;

            for (int i = 0; i < referencedObjs.Length; i++)
            {
                referencedObjs[i].IncrementReferenceCount();
            }
        }

        public T GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored)
        {
            var mirror = _mirrorable.GetMirrorable(cbs, ref offset, size, out mirrored);
            mirror._waitable?.AddBufferUse(cbs.CommandBufferIndex, offset, size, false);
            return mirror.Get(cbs);
        }

        public T Get(CommandBufferScoped cbs, int offset, int size, bool write = false)
        {
            _mirrorable?.ClearMirrors(cbs, offset, size);
            _waitable?.AddBufferUse(cbs.CommandBufferIndex, offset, size, write);
            return Get(cbs);
        }

        public T GetUnsafe()
        {
            return _value;
        }

        public T Get(CommandBufferScoped cbs)
        {
            if (_isDisposed || _destroyed)
            {
                Debug.WriteLine($"Warning: Accessing destroyed {typeof(T).Name}");
                return default;
            }
            
            if (!_destroyed)
            {
                AddCommandBufferDependencies(cbs);
            }

            return _value;
        }

        public bool HasCommandBufferDependency(CommandBufferScoped cbs)
        {
            return _cbOwnership.IsSet(cbs.CommandBufferIndex);
        }

        public bool HasRentedCommandBufferDependency(CommandBufferPool cbp)
        {
            return _cbOwnership.AnySet();
        }

        public void AddCommandBufferDependencies(CommandBufferScoped cbs)
        {
            // We don't want to add a reference to this object to the command buffer
            // more than once, so if we detect that the command buffer already has ownership
            // of this object, then we can just return without doing anything else.
            if (_cbOwnership.Set(cbs.CommandBufferIndex))
            {
                if (_waitable != null)
                {
                    cbs.AddWaitable(_waitable);
                }

                cbs.AddDependant(this);

                // We need to add a dependency on the command buffer to all objects this object
                // references aswell.
                if (_referencedObjs != null)
                {
                    for (int i = 0; i < _referencedObjs.Length; i++)
                    {
                        _referencedObjs[i].AddCommandBufferDependencies(cbs);
                    }
                }
            }
        }

        public bool TryIncrementReferenceCount()
        {
            int lastValue;
            do
            {
                lastValue = _referenceCount;

                if (lastValue == 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _referenceCount, lastValue + 1, lastValue) != lastValue);

            return true;
        }

        public void IncrementReferenceCount()
        {
            lock (_refCountLock)
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException($"Attempted to increment reference count of disposed {typeof(T).Name}");
                }
                
                if (Interlocked.Increment(ref _referenceCount) == 1)
                {
                    Interlocked.Decrement(ref _referenceCount);
                    throw new InvalidOperationException("Reference count inconsistency");
                }
            }
        }

        public void DecrementReferenceCount(int cbIndex)
        {
            _cbOwnership.Clear(cbIndex);
            DecrementReferenceCount();
        }

        public void DecrementReferenceCount()
        {
            lock (_refCountLock)
            {
                int newCount = Interlocked.Decrement(ref _referenceCount);
                if (newCount < 0)
                {
                    throw new InvalidOperationException("Reference count negative");
                }
                
                if (newCount == 0)
                {
                    try
                    {
                        _value?.Dispose();
                        _value = default;
                        _destroyed = true;
                        
                        // 清除所有引用
                        if (_referencedObjs != null)
                        {
                            foreach (var obj in _referencedObjs)
                            {
                                obj.DecrementReferenceCount();
                            }
                        }
                    }
                    finally
                    {
                        _isDisposed = true;
                    }
                }
            }
        }

// 增加安全获取方法
        public bool TryGet(CommandBufferScoped cbs, out T value)
        {
            lock (_refCountLock)
            {
                if (!_isDisposed && !_destroyed)
                {
                    value = Get(cbs);
                    return true;
                }
                
                value = default;
                return false;
            }
        }
    }
}

        public void Dispose()
        {
            lock (_refCountLock)
            {
                if (!_isDisposed)
                {
                    DecrementReferenceCount();
                }
            }
        }
    }
}
