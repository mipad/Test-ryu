using System;
using System.Diagnostics;

namespace Ryujinx.Cpu
{
    public class TickSource : ITickSource
    {
        private Stopwatch _tickCounter;
        private double _hostTickFreq;

        private long _tickScalar = 100; // 默认 100%
        private long _acumElapsedTicks;
        private long _lastElapsedTicks;

        /// <inheritdoc/>
        public ulong Frequency { get; }

        /// <inheritdoc/>
        public ulong Counter => (ulong)(ElapsedSeconds * Frequency);

        public long TickScalar
        {
            get => _tickScalar;
            set => _tickScalar = Math.Clamp(value, 0, 400); // 限制在 0-400%
        }

        private long ElapsedTicks
        {
            get
            {
                long elapsedTicks = _tickCounter.ElapsedTicks;
                
                _acumElapsedTicks += (elapsedTicks - _lastElapsedTicks) * _tickScalar / 100;

                _lastElapsedTicks = elapsedTicks;
                
                return _acumElapsedTicks;
            }
        }
        
        /// <inheritdoc/>
        public TimeSpan ElapsedTime => Stopwatch.GetElapsedTime(0, ElapsedTicks);

        /// <inheritdoc/>
        public double ElapsedSeconds => ElapsedTicks * _hostTickFreq;

        public TickSource(ulong frequency)
        {
            Frequency = frequency;
            _hostTickFreq = 1.0 / Stopwatch.Frequency;

            _tickCounter = new Stopwatch();
            _tickCounter.Start();
        }

        /// <inheritdoc/>
        public void Suspend()
        {
            _tickCounter.Stop();
        }

        /// <inheritdoc/>
        public void Resume()
        {
            _tickCounter.Start();
        }
    }
}
