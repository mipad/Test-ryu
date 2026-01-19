using Ryujinx.Graphics.GAL;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    class CounterQueueEvent : ICounterEvent
    {
        public event EventHandler<ulong> OnResult;

        public CounterType Type { get; }
        public bool ClearCounter { get; private set; }

        public bool Disposed { get; private set; }
        public bool Invalid { get; set; }

        public ulong DrawIndex { get; }

        private readonly CounterQueue _queue;
        private readonly BufferedQuery _counter;
        private readonly bool _includeTimestamp;

        private bool _hostAccessReserved;
        private int _refCount = 1; // Starts with a reference from the counter queue.

        private readonly object _lock = new();
        private ulong _result = ulong.MaxValue;
        private ulong _timestamp = 0;
        private double _divisor = 1f;

        public CounterQueueEvent(CounterQueue queue, CounterType type, ulong drawIndex, bool includeTimestamp = false)
        {
            _queue = queue;
            _includeTimestamp = includeTimestamp;

            _counter = queue.GetQueryObject();
            Type = type;
            DrawIndex = drawIndex;

            _counter.Begin(_queue.ResetSequence);
        }

        public Auto<DisposableBuffer> GetBuffer()
        {
            return _counter.GetBuffer();
        }

        public Auto<DisposableBuffer> GetTimestampBuffer()
        {
            return _counter.GetTimestampBuffer();
        }

        internal void Clear(bool counterReset)
        {
            if (counterReset)
            {
                _counter.Reset();
            }

            ClearCounter = true;
        }

        internal void Complete(bool withResult, double divisor)
        {
            _counter.End(withResult);
            _divisor = divisor;
        }

        internal bool TryConsume(ref ulong result, bool block, AutoResetEvent wakeSignal = null)
        {
            lock (_lock)
            {
                if (Disposed)
                {
                    return true;
                }

                if (ClearCounter)
                {
                    result = 0;
                }

                long queryResult;
                ulong timestamp = 0;

                if (block)
                {
                    var (res, ts) = _counter.AwaitResult(wakeSignal);
                    queryResult = res;
                    timestamp = ts;
                }
                else
                {
                    if (_includeTimestamp)
                    {
                        if (!_counter.TryGetResult(out queryResult, out timestamp))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (!_counter.TryGetResult(out queryResult))
                        {
                            return false;
                        }
                    }
                }

                result += _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);
                _result = result;
                _timestamp = timestamp;

                OnResult?.Invoke(this, result);

                Dispose();

                return true;
            }
        }

        public ulong GetTimestamp()
        {
            lock (_lock)
            {
                return _timestamp;
            }
        }
        
        // 等待查询完成（防止闪烁）
        public void WaitForCompletion()
        {
            // 使用超时避免无限等待
            _counter.WaitForCompletion(50000000); // 50毫秒超时
        }

        public void Flush()
        {
            if (Disposed)
            {
                return;
            }

            _queue.FlushTo(this);
        }

        public void DecrementRefCount()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                DisposeInternal();
            }
        }

        public bool ReserveForHostAccess()
        {
            if (_hostAccessReserved)
            {
                return true;
            }

            if (IsValueAvailable())
            {
                return false;
            }

            if (Interlocked.Increment(ref _refCount) == 1)
            {
                Interlocked.Decrement(ref _refCount);

                return false;
            }

            _hostAccessReserved = true;

            return true;
        }

        public void ReleaseHostAccess()
        {
            _hostAccessReserved = false;

            DecrementRefCount();
        }

        private void DisposeInternal()
        {
            _queue.ReturnQueryObject(_counter);
        }

        private bool IsValueAvailable()
        {
            return _result != ulong.MaxValue || _counter.TryGetResult(out _);
        }

        public void Dispose()
        {
            Disposed = true;
            DecrementRefCount();
        }
    }
}