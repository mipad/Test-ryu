using Ryujinx.Audio.Renderer.Dsp.Command;
using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;

namespace Ryujinx.Audio.Renderer.Server
{
    /// <summary>
    /// <see cref="ICommandProcessingTimeEstimator"/> version 7, introduced with Nintendo Switch system firmware REV15 (15.0.0+).
    /// This estimator provides performance-tuned cycle counts for DSP command scheduling under high-load audio workloads.
    /// Uses conservative estimates by default to prevent scheduling timeouts and audio thread crashes.
    /// </summary>
    public class CommandProcessingTimeEstimatorVersion7 : CommandProcessingTimeEstimatorVersion5
    {
        private readonly uint _sampleCount;
        private readonly uint _bufferCount;

        /// <summary>
        /// Initializes a new instance of <see cref="CommandProcessingTimeEstimatorVersion7"/>.
        /// </summary>
        /// <param name="sampleCount">The number of samples per processing frame (must be 160 or 240).</param>
        /// <param name="bufferCount">The number of output buffers (passed to base class).</param>
        public CommandProcessingTimeEstimatorVersion7(uint sampleCount, uint bufferCount) : base(sampleCount, bufferCount)
        {
            _sampleCount = sampleCount;
            _bufferCount = bufferCount;

            Log.Info?.Print(LogClass.AudioRenderer, 
                $"[REV15] CommandProcessingTimeEstimatorVersion7 created (SampleCount={_sampleCount}, BufferCount={_bufferCount})");
        }

        /// <summary>
        /// Validates that the sample count is supported (160 or 240).
        /// Unlike Debug.Assert, this check is active in Release mode.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown if sample count is invalid.</exception>
        private void ValidateSampleCount()
        {
            if (_sampleCount != 160 && _sampleCount != 240)
            {
                var ex = new ArgumentException($"Invalid sample count for REV15 estimator: {_sampleCount}. Expected 160 or 240.");
                Log.Error?.Print(LogClass.AudioRenderer, ex.Message);
                throw ex;
            }
        }

        /// <summary>
        /// Estimates the processing time for a <see cref="DelayCommand"/>.
        /// REV15 optimized delay buffer access using SIMD and improved cache locality.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public override uint Estimate(DelayCommand command)
        {
            ValidateSampleCount();
            Log.Debug?.Print(LogClass.AudioRenderer, 
                $"[REV15] Estimating DelayCommand (Enabled={command.Enabled}, Channels={command.Parameter.ChannelCount})");

            if (_sampleCount == 160)
            {
                if (command.Enabled)
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => (uint)8529.00f,   // 1 ch enabled
                        2 => (uint)24501.00f,  // 2 ch enabled
                        4 => (uint)46760.00f,  // 4 ch enabled
                        6 => (uint)80203.00f,  // 6 ch enabled
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                    };
                }

                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)1195.20f,  // 1 ch disabled
                    2 => (uint)1113.60f,  // 2 ch disabled
                    4 => (uint)842.03f,   // 4 ch disabled
                    6 => (uint)901.60f,   // 6 ch disabled
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                };
            }

            if (command.Enabled)
            {
                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)11541.00f,  // 1 ch enabled
                    2 => (uint)36197.00f,  // 2 ch enabled
                    4 => (uint)68750.00f,  // 4 ch enabled
                    6 => (uint)118040.00f, // 6 ch enabled (fixed: was 11804)
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                };
            }

            return command.Parameter.ChannelCount switch
            {
                1 => (uint)897.67f,  // 1 ch disabled
                2 => (uint)877.63f,  // 2 ch disabled
                4 => (uint)692.31f,  // 4 ch disabled
                6 => (uint)775.43f,  // 6 ch disabled
                _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
            };
        }

        /// <summary>
        /// Estimates the processing time for a <see cref="ReverbCommand"/>.
        /// REV15 improved convolution efficiency and reduced memory bandwidth usage.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public override uint Estimate(ReverbCommand command)
        {
            ValidateSampleCount();
            Log.Debug?.Print(LogClass.AudioRenderer, 
                $"[REV15] Estimating ReverbCommand (Enabled={command.Enabled}, Channels={command.Parameter.ChannelCount})");

            if (_sampleCount == 160)
            {
                if (command.Enabled)
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => (uint)80475.00f,  // 1 ch enabled
                        2 => (uint)83975.00f,  // 2 ch enabled
                        4 => (uint)90625.00f,  // 4 ch enabled
                        6 => (uint)94332.00f,  // 6 ch enabled
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                    };
                }

                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)486.30f,  // 1 ch disabled
                    2 => (uint)538.80f,  // 2 ch disabled
                    4 => (uint)593.70f,  // 4 ch disabled
                    6 => (uint)656.00f,  // 6 ch disabled
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                };
            }

            if (command.Enabled)
            {
                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)119170.00f, // 1 ch enabled
                    2 => (uint)124260.00f, // 2 ch enabled
                    4 => (uint)134750.00f, // 4 ch enabled
                    6 => (uint)140130.00f, // 6 ch enabled
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                };
            }

            return command.Parameter.ChannelCount switch
            {
                1 => (uint)567.64f,  // 1 ch disabled
                2 => (uint)609.54f,  // 2 ch disabled
                4 => (uint)661.44f,  // 4 ch disabled
                6 => (uint)728.07f,  // 6 ch disabled
                _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
            };
        }

        /// <summary>
        /// Estimates the processing time for a <see cref="Reverb3dCommand"/>.
        /// Uses conservative estimate to avoid scheduling timeout.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public override uint Estimate(Reverb3dCommand command)
        {
            ValidateSampleCount();
            // 🔴 Conservative estimate to prevent GuestBrokeExecutionException
            uint estimated = _sampleCount == 160 ? 80000u : 120000u;
            Log.Info?.Print(LogClass.AudioRenderer, 
                $"[REV15] Reverb3dCommand estimated conservatively: {estimated} cycles (SampleCount={_sampleCount})");

            return estimated;
        }

        /// <summary>
        /// Estimates the processing time for a <see cref="CompressorCommand"/>.
        /// Uses conservative estimate due to high computational load.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public override uint Estimate(CompressorCommand command)
        {
            ValidateSampleCount();
            // 🔴 Conservative estimate
            uint estimated = _sampleCount == 160 ? 20000u : 30000u;
            Log.Info?.Print(LogClass.AudioRenderer, 
                $"[REV15] CompressorCommand estimated conservatively: {estimated} cycles (Enabled={command.Enabled}, Stats={command.Parameter.StatisticsEnabled})");

            return estimated;
        }

        /// <summary>
        /// Estimates the processing time for a <see cref="BiquadFilterAndMixCommand"/>.
        /// REV15 uses vectorized biquad processing for better throughput.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public override uint Estimate(BiquadFilterAndMixCommand command)
        {
            ValidateSampleCount();
            uint baseCost = command.HasVolumeRamp
                ? (_sampleCount == 160 ? 5004u : 6483u)
                : (_sampleCount == 160 ? 3227u : 4552u);

            Log.Debug?.Print(LogClass.AudioRenderer, 
                $"[REV15] BiquadFilterAndMixCommand estimated: {baseCost} cycles (Ramp={command.HasVolumeRamp})");

            return baseCost;
        }

        /// <summary>
        /// Estimates the processing time for a <see cref="MultiTapBiquadFilterAndMixCommand"/>.
        /// REV15 optimized multi-tap filter chaining and memory layout.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public override uint Estimate(MultiTapBiquadFilterAndMixCommand command)
        {
            ValidateSampleCount();
            uint baseCost = command.HasVolumeRamp
                ? (_sampleCount == 160 ? 7739u : 10469u)
                : (_sampleCount == 160 ? 6056u : 8483u);

            Log.Debug?.Print(LogClass.AudioRenderer, 
                $"[REV15] MultiTapBiquadFilterAndMixCommand estimated: {baseCost} cycles (Ramp={command.HasVolumeRamp})");

            return baseCost;
        }
    }
}
