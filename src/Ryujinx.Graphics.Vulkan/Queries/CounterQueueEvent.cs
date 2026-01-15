using Ryujinx.Graphics.GAL;
using System;
using System.Threading;
using System.Threading.Tasks;
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
        
        // 异步支持
        private TaskCompletionSource<ulong> _asyncCompletionSource;
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

        internal bool TryConsume(ref ulong accumulatedResult, bool block, AutoResetEvent wakeSignal = null)
        {
            lock (_lock)
            {
                if (Disposed)
                {
                    return true;
                }

                if (ClearCounter)
                {
                    accumulatedResult = 0;
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

                accumulatedResult += _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);

                _result = accumulatedResult;

                OnResult?.Invoke(this, accumulatedResult);
                
                // 通知异步等待者
                if (_asyncResultPending)
                {
                    _asyncCompletionSource?.TrySetResult(accumulatedResult);
                    _asyncResultPending = false;
                }

                Dispose();

                return true;
            }
        }
        
        // 移除有ref参数的异步方法，改为返回结果的方法
        internal (bool Success, ulong Result) TryConsumeWithResult(bool block, AutoResetEvent wakeSignal = null)
        {
            lock (_lock)
            {
                if (Disposed)
                {
                    return (true, _result);
                }

                long queryResult;
                ulong currentResult = _result;

                if (ClearCounter)
                {
                    currentResult = 0;
                }

                if (block)
                {
                    queryResult = _counter.AwaitResult(wakeSignal);
                }
                else
                {
                    if (!_counter.TryGetResult(out queryResult))
                    {
                        return (false, currentResult);
                    }
                }

                ulong increment = _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);
                currentResult += increment;

                _result = currentResult;

                OnResult?.Invoke(this, currentResult);
                
                // 通知异步等待者
                if (_asyncResultPending)
                {
                    _asyncCompletionSource?.TrySetResult(currentResult);
                    _asyncResultPending = false;
                }

                Dispose();

                return (true, currentResult);
            }
        }
        
        // 异步消费结果 - 移除ref参数
        internal async Task<(bool Success, ulong Result)> TryConsumeAsync(bool block, AutoResetEvent wakeSignal = null)
        {
            return await Task.Run(() => TryConsumeWithResult(block, wakeSignal));
        }
        
        // 异步获取结果
        public async Task<ulong> GetResultAsync(CancellationToken cancellationToken = default)
        {
            if (Disposed)
            {
                return _result != ulong.MaxValue ? _result : 0;
            }
            
            // 如果已经有结果，直接返回
            if (_result != ulong.MaxValue)
            {
                return _result;
            }
            
            // 如果异步结果已经在等待中，返回现有的任务
            if (_asyncCompletionSource != null)
            {
                return await _asyncCompletionSource.Task;
            }
            
            // 创建新的异步完成源
            _asyncCompletionSource = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
            
            // 启动后台任务等待查询结果
            _ = Task.Run(async () =>
            {
                try
                {
                    // 使用异步方式获取查询结果
                    long queryResult = await _counter.GetResultAsync(cancellationToken);
                    
                    lock (_lock)
                    {
                        if (Disposed)
                        {
                            _asyncCompletionSource.TrySetResult(0);
                            return;
                        }
                        
                        ulong finalResult = _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);
                        
                        if (ClearCounter)
                        {
                            finalResult = 0;
                        }
                        
                        // 注意：这里我们没有累加到任何外部变量，只是返回本次查询的结果
                        _result = finalResult;
                        
                        // 触发结果事件
                        OnResult?.Invoke(this, finalResult);
                        
                        _asyncResultPending = false;
                        _asyncCompletionSource.TrySetResult(finalResult);
                        
                        Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _asyncCompletionSource.TrySetException(ex);
                }
            }, cancellationToken);
            
            // 等待异步完成
            try
            {
                return await _asyncCompletionSource.Task.WaitAsync(cancellationToken);
            }
            catch (TimeoutException)
            {
                Logger.Error?.Print(LogClass.Gpu, $"GetResultAsync timeout for query {Type}");
                return 0UL;
            }
            catch (OperationCanceledException)
            {
                Logger.Debug?.Print(LogClass.Gpu, $"GetResultAsync cancelled for query {Type}");
                return 0UL;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"GetResultAsync error: {ex.Message}");
                return 0UL;
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
            _asyncCompletionSource = null;
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
