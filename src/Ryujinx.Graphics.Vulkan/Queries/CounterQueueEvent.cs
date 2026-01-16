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
        
        // 调试信息
        private readonly bool _isTbdrPlatform;
        private DateTime _creationTime;
        private DateTime? _completionTime;

        public CounterQueueEvent(CounterQueue queue, CounterType type, ulong drawIndex)
        {
            _queue = queue;
            _isTbdrPlatform = queue.GetType().GetField("_isTbdrPlatform", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(queue) as bool? ?? false;
            
            _counter = queue.GetQueryObject();
            Type = type;
            DrawIndex = drawIndex;
            
            _creationTime = DateTime.UtcNow;
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent created: Type={type}, DrawIndex={drawIndex}");
            }

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
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent Clear: Type={Type}, CounterReset={counterReset}");
            }
        }

        internal void Complete(bool withResult, double divisor)
        {
            _counter.End(withResult);
            _divisor = divisor;
            
            if (_isTbdrPlatform && withResult)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent Complete: Type={Type}, Divisor={divisor}");
            }
        }

        internal bool TryConsume(ref ulong result, bool block, AutoResetEvent wakeSignal = null)
        {
            lock (_lock)
            {
                if (Disposed)
                {
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"CounterQueueEvent TryConsume already disposed: Type={Type}");
                    }
                    return true;
                }

                if (ClearCounter)
                {
                    result = 0;
                    
                    if (_isTbdrPlatform)
                    {
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"CounterQueueEvent TryConsume clear counter: Type={Type}");
                    }
                }

                long queryResult;

                if (block)
                {
                    DateTime startTime = DateTime.UtcNow;
                    
                    queryResult = _counter.AwaitResult(wakeSignal);
                    
                    TimeSpan elapsed = DateTime.UtcNow - startTime;
                    
                    if (_isTbdrPlatform && elapsed.TotalMilliseconds > 10)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"CounterQueueEvent slow AwaitResult: Type={Type}, Time={elapsed.TotalMilliseconds:F2}ms");
                    }
                }
                else
                {
                    if (!_counter.TryGetResult(out queryResult))
                    {
                        if (_isTbdrPlatform)
                        {
                            Logger.Debug?.Print(LogClass.Gpu, 
                                $"CounterQueueEvent TryConsume no result yet: Type={Type}");
                        }
                        return false;
                    }
                }

                // 验证查询结果的有效性
                if (queryResult < 0 || queryResult > long.MaxValue)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Invalid query result detected: {queryResult}. Type={Type}. Using safe value.");
                    queryResult = 0;
                }
                
                // 限制结果范围，避免溢出
                ulong safeResult = queryResult < 0 ? 0 : (ulong)queryResult;
                if (_divisor != 1)
                {
                    safeResult = (ulong)Math.Ceiling(safeResult / _divisor);
                }
                
                // 设置合理的上限
                const ulong MaxSafeResult = 1000000000UL; // 10亿
                safeResult = Math.Min(safeResult, MaxSafeResult);
                
                result += safeResult;

                _result = result;
                _completionTime = DateTime.UtcNow;
                
                if (_isTbdrPlatform)
                {
                    TimeSpan totalTime = _completionTime.Value - _creationTime;
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueueEvent result ready: Type={Type}, " +
                        $"Result={result}, QueryResult={queryResult}, " +
                        $"TotalTime={totalTime.TotalMilliseconds:F2}ms");
                }

                OnResult?.Invoke(this, result);

                Dispose();

                return true;
            }
        }

        public void Flush()
        {
            if (Disposed)
            {
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueueEvent Flush already disposed: Type={Type}");
                }
                return;
            }

            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent Flush: Type={Type}");
            }
            
            _queue.FlushTo(this);
        }

        public void DecrementRefCount()
        {
            int newCount = Interlocked.Decrement(ref _refCount);
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent DecrementRefCount: Type={Type}, NewCount={newCount}");
            }
            
            if (newCount == 0)
            {
                DisposeInternal();
            }
        }

        public bool ReserveForHostAccess()
        {
            if (_hostAccessReserved)
            {
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueueEvent ReserveForHostAccess already reserved: Type={Type}");
                }
                return true;
            }

            if (IsValueAvailable())
            {
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueueEvent ReserveForHostAccess value already available: Type={Type}");
                }
                return false;
            }

            if (Interlocked.Increment(ref _refCount) == 1)
            {
                Interlocked.Decrement(ref _refCount);
                
                if (_isTbdrPlatform)
                {
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"CounterQueueEvent ReserveForHostAccess ref count was zero: Type={Type}");
                }
                return false;
            }

            _hostAccessReserved = true;
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent ReserveForHostAccess success: Type={Type}, RefCount={_refCount}");
            }

            return true;
        }

        public void ReleaseHostAccess()
        {
            _hostAccessReserved = false;
            
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent ReleaseHostAccess: Type={Type}");
            }

            DecrementRefCount();
        }

        private void DisposeInternal()
        {
            if (_isTbdrPlatform)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent DisposeInternal: Type={Type}");
            }
            
            _queue.ReturnQueryObject(_counter);
        }

        private bool IsValueAvailable()
        {
            bool available = _result != ulong.MaxValue || _counter.TryGetResult(out _);
            
            if (_isTbdrPlatform && available)
            {
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent IsValueAvailable: Type={Type}, Available={available}");
            }
            
            return available;
        }

        public void Dispose()
        {
            if (Disposed)
            {
                return;
            }
            
            Disposed = true;

            if (_isTbdrPlatform)
            {
                TimeSpan? lifetime = _completionTime.HasValue ? 
                    _completionTime.Value - _creationTime : 
                    DateTime.UtcNow - _creationTime;
                    
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"CounterQueueEvent Dispose: Type={Type}, Lifetime={lifetime?.TotalMilliseconds:F2}ms");
            }

            DecrementRefCount();
        }
    }
}