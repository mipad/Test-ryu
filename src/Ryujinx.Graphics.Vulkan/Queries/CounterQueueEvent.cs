// CounterQueueEvent.cs 修复版本
using Ryujinx.Graphics.GAL;
using System;
using System.Threading;
using Ryujinx.Common.Logging;

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

        private bool _hostAccessReserved;
        private int _refCount = 1; // Starts with a reference from the counter queue.

        private readonly object _lock = new();
        private ulong _result = ulong.MaxValue;
        private double _divisor = 1f;

        public CounterQueueEvent(CounterQueue queue, CounterType type, ulong drawIndex)
        {
            _queue = queue;

            _counter = queue.GetQueryObject();
            Type = type;

            DrawIndex = drawIndex;

            _counter.Begin(_queue.ResetSequence);
        }

        public Auto<DisposableBuffer> GetBuffer()
        {
            return _counter.GetBuffer();
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

                if (block)
                {
                    queryResult = _counter.AwaitResult(wakeSignal);
                    
                    // 检查是否为超时返回的特殊值
                    if (queryResult == -1)
                    {
                        // -1 是超时或错误值，尝试从批量缓冲区获取
                        if (_counter.TryCopyFromBatchResult())
                        {
                            if (_counter.TryGetResult(out queryResult))
                            {
                                Logger.Debug?.Print(LogClass.Gpu, 
                                    $"Query {Type} recovered from batch buffer after timeout");
                            }
                            else
                            {
                                // 如果获取不到，返回false让调用者知道结果不可用
                                Logger.Warning?.Print(LogClass.Gpu, 
                                    $"Query {Type} timed out with no recoverable result");
                                return false;
                            }
                        }
                        else
                        {
                            // 如果无法从批量缓冲区恢复，返回false
                            return false;
                        }
                    }
                }
                else
                {
                    // 非阻塞：先尝试从批量缓冲区获取
                    _counter.TryCopyFromBatchResult();
                    
                    if (!_counter.TryGetResult(out queryResult))
                    {
                        return false;
                    }
                }

                result += _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);

                _result = result;

                OnResult?.Invoke(this, result);

                Dispose();

                return true;
            }
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
            // 在释放查询对象之前，确保从批量缓冲区复制了结果
            _counter.TryCopyFromBatchResult();
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