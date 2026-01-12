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
        private int _refCount = 1;

        private readonly object _lock = new();
        private ulong _result = ulong.MaxValue;
        private double _divisor = 1f;
        private bool _resultConsumed = false;

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
                if (Disposed || _resultConsumed)
                {
                    return true;
                }

                if (ClearCounter)
                {
                    result = 0;
                }

                long queryResult = 0;
                bool gotResult = false;

                if (block)
                {
                    queryResult = _counter.AwaitResult(wakeSignal);
                    gotResult = true;
                    
                    if (queryResult == 0 && _queue.Gd.IsTBDR)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"Query {Type} returned 0 (may be timeout or actual result)");
                    }
                }
                else
                {
                    if (_counter.TryGetResult(out queryResult))
                    {
                        gotResult = true;
                    }
                    else
                    {
                        if (_queue.Gd.IsTBDR)
                        {
                            _counter.TryCopyFromBatchResult();
                            if (_counter.TryGetResult(out queryResult))
                            {
                                gotResult = true;
                                Logger.Debug?.Print(LogClass.Gpu, 
                                    $"Query {Type} recovered from batch buffer (non-blocking)");
                            }
                        }
                    }
                }

                if (gotResult)
                {
                    result += _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);
                    _result = result;
                    _resultConsumed = true;

                    OnResult?.Invoke(this, result);
                    
                    DecrementRefCount();

                    return true;
                }

                return false;
            }
        }

        public void Flush()
        {
            if (Disposed || _resultConsumed)
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
            _counter.TryCopyFromBatchResult();
            _queue.ReturnQueryObject(_counter);
        }

        private bool IsValueAvailable()
        {
            return _result != ulong.MaxValue || _counter.TryGetResult(out _);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (Disposed) return;
                
                Disposed = true;

                if (!_resultConsumed)
                {
                    ulong dummy = 0;
                    TryConsume(ref dummy, false);
                }

                DecrementRefCount();
            }
        }
    }
}