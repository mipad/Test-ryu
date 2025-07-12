using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.Utilities
{
    /// <summary>
    /// 带宽限制器，用于控制资源访问速率
    /// </summary>
    public class RateLimiter : IDisposable
    {
        private readonly long _capacity;
        private long _available;
        private long _lastRefillTime;
        private readonly object _lock = new object();
        private bool _disposed;
        
        public RateLimiter(long bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), "Capacity must be greater than 0");
            
            _capacity = bytesPerSecond;
            _available = bytesPerSecond;
            _lastRefillTime = Stopwatch.GetTimestamp();
        }
        
        public void Wait(int bytes)
        {
            if (bytes <= 0) return;
            if (_disposed) throw new ObjectDisposedException(nameof(RateLimiter));
            
            lock (_lock)
            {
                Refill();
                
                while (_available < bytes)
                {
                    long deficit = bytes - _available;
                    double waitSeconds = (double)deficit / _capacity;
                    int waitMs = (int)Math.Ceiling(waitSeconds * 1000);
                    
                    // 更精确的等待
                    if (waitMs > 0)
                    {
                        Monitor.Wait(_lock, waitMs);
                        Refill();
                    }
                }
                
                _available -= bytes;
            }
        }
        
        private void Refill()
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedSeconds = (double)(now - _lastRefillTime) / Stopwatch.Frequency;
            
            if (elapsedSeconds > 0)
            {
                long refillAmount = (long)(elapsedSeconds * _capacity);
                if (refillAmount > 0)
                {
                    _available = Math.Min(_capacity, _available + refillAmount);
                    _lastRefillTime = now;
                }
            }
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                // 唤醒所有等待线程
                lock (_lock)
                {
                    Monitor.PulseAll(_lock);
                }
            }
        }
        
        ~RateLimiter()
        {
            Dispose(false);
        }
    }
}
