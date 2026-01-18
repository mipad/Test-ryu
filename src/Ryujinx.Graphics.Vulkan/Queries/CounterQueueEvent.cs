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
        
        // 防抖动机制
        private const int VisibilityChangeThreshold = 3; // 需要连续N帧状态相同才认为状态改变
        private int _visibleFrameCount = 0;
        private int _invisibleFrameCount = 0;
        private bool _lastVisibleState = false;
        private bool _currentVisibleState = false;

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
                    
                    // 如果等待超时，尝试从批量缓冲区复制结果
                    if (queryResult == 0)
                    {
                        _counter.TryCopyFromBatchResult();
                        if (_counter.TryGetResult(out queryResult))
                        {
                            if (Logger.Debug.HasValue)
                            {
                                Logger.Debug.Value.Print(LogClass.Gpu, 
                                    $"Query {Type} recovered from batch buffer after timeout");
                            }
                        }
                    }
                }
                else
                {
                    // 先尝试从批量缓冲区获取结果
                    _counter.TryCopyFromBatchResult();
                    
                    if (!_counter.TryGetResult(out queryResult))
                    {
                        return false;
                    }
                }

                // 应用防抖动逻辑
                bool isVisible = ApplyDebouncing(queryResult);
                
                // 如果不可见，将结果视为0
                if (!isVisible)
                {
                    queryResult = 0;
                }
                else if (queryResult == 0)
                {
                    // 如果逻辑上可见但查询结果为0，使用一个小的非零值
                    queryResult = 1;
                }

                result += _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);

                _result = result;

                OnResult?.Invoke(this, result);

                Dispose();

                return true;
            }
        }
        
        // 应用防抖动逻辑
        private bool ApplyDebouncing(long queryResult)
        {
            // 判断当前帧是否可见
            bool isCurrentlyVisible = queryResult > 0;
            
            // 更新状态计数器
            if (isCurrentlyVisible)
            {
                _visibleFrameCount++;
                _invisibleFrameCount = 0;
            }
            else
            {
                _invisibleFrameCount++;
                _visibleFrameCount = 0;
            }
            
            // 判断是否需要改变状态
            if (isCurrentlyVisible != _currentVisibleState)
            {
                // 如果新状态持续足够多帧，则改变状态
                if ((isCurrentlyVisible && _visibleFrameCount >= VisibilityChangeThreshold) ||
                    (!isCurrentlyVisible && _invisibleFrameCount >= VisibilityChangeThreshold))
                {
                    _lastVisibleState = _currentVisibleState;
                    _currentVisibleState = isCurrentlyVisible;
                    
                    if (Logger.Debug.HasValue && _lastVisibleState != _currentVisibleState)
                    {
                        Logger.Debug.Value.Print(LogClass.Gpu, 
                            $"Query {Type} visibility changed: {_lastVisibleState} -> {_currentVisibleState}, " +
                            $"queryResult={queryResult}, frames={Math.Max(_visibleFrameCount, _invisibleFrameCount)}");
                    }
                }
            }
            else
            {
                // 状态相同，重置另一状态的计数器
                if (isCurrentlyVisible)
                {
                    _invisibleFrameCount = 0;
                }
                else
                {
                    _visibleFrameCount = 0;
                }
            }
            
            return _currentVisibleState;
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