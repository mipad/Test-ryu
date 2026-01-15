using Ryujinx.Graphics.GAL;
using System;
using System.Threading;
using System.Threading.Tasks;

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
        
        // 异步支持
        private TaskCompletionSource<bool> _asyncCompletionSource;
        private bool _asyncResultPending;

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
            _asyncResultPending = withResult;
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
                }
                else
                {
                    if (!_counter.TryGetResult(out queryResult))
                    {
                        return false;
                    }
                }

                result += _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);

                _result = result;

                OnResult?.Invoke(this, result);
                
                // 通知异步等待者
                if (_asyncResultPending)
                {
                    _asyncCompletionSource?.TrySetResult(true);
                    _asyncResultPending = false;
                }

                Dispose();

                return true;
            }
        }
        
        // 异步消费结果
        internal async Task<bool> TryConsumeAsync(ref ulong result, bool block, AutoResetEvent wakeSignal = null)
        {
            return await Task.Run(() => TryConsume(ref result, block, wakeSignal));
        }
        
        // 异步获取结果
        public async Task<ulong> GetResultAsync(CancellationToken cancellationToken = default)
        {
            if (Disposed || !_asyncResultPending)
            {
                return _result != ulong.MaxValue ? _result : 0;
            }
            
            // 创建异步完成源
            _asyncCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            
            // 启动异步任务等待查询结果
            var waitTask = Task.Run(async () =>
            {
                try
                {
                    // 使用异步方式获取查询结果
                    long queryResult = await _counter.GetResultAsync(cancellationToken);
                    
                    lock (_lock)
                    {
                        if (Disposed)
                        {
                            return 0UL;
                        }
                        
                        ulong finalResult = _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);
                        
                        if (ClearCounter)
                        {
                            finalResult = 0;
                        }
                        
                        _result = finalResult;
                        
                        // 触发结果事件
                        OnResult?.Invoke(this, finalResult);
                        
                        _asyncResultPending = false;
                        _asyncCompletionSource?.TrySetResult(true);
                        
                        Dispose();
                        
                        return finalResult;
                    }
                }
                catch (Exception ex)
                {
                    _asyncCompletionSource?.TrySetException(ex);
                    return 0UL;
                }
            }, cancellationToken);
            
            // 等待异步完成
            await Task.WhenAny(_asyncCompletionSource.Task, Task.Delay(5000, cancellationToken));
            
            if (cancellationToken.IsCancellationRequested)
            {
                return 0UL;
            }
            
            return _result != ulong.MaxValue ? _result : 0;
        }

        public void Flush()
        {
            if (Disposed)
            {
                return;
            }

            _queue.FlushTo(this);
        }
        
        // 异步刷新
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Disposed)
            {
                return;
            }
            
            await Task.Run(() => _queue.FlushTo(this), cancellationToken);
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
            _asyncCompletionSource?.TrySetCanceled();
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