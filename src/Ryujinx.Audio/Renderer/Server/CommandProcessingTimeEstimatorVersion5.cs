using Ryujinx.Audio.Renderer.Dsp.Command;
using System;
using System.Diagnostics;

namespace Ryujinx.Audio.Renderer.Server
{
    /// <summary>
    /// <see cref="ICommandProcessingTimeEstimator"/> version 5.
    /// This version includes performance improvements over version 3, especially in reverb and delay processing.
    /// All estimates are in DSP cycles and assume proper sample count (160 or 240).
    /// </summary>
    public class CommandProcessingTimeEstimatorVersion5 : CommandProcessingTimeEstimatorVersion3
    {
        /// <summary>
        /// Initializes a new instance of <see cref="CommandProcessingTimeEstimatorVersion5"/>.
        /// </summary>
        /// <param name="sampleCount">The number of samples per frame (160 or 240).</param>
        /// <param name="bufferCount">The number of buffers (used for legacy compatibility).</param>
        public CommandProcessingTimeEstimatorVersion5(uint sampleCount, uint bufferCount) : base(sampleCount, bufferCount) { }

        /// <summary>
        /// Estimates the processing time for a <see cref="DelayCommand"/>.
        /// Version 5 optimized buffer access patterns for better cache usage.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public virtual uint Estimate(DelayCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (SampleCount == 160)
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
        /// Version 5 improved convolution efficiency and reduced memory bandwidth usage.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public virtual uint Estimate(ReverbCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (SampleCount == 160)
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
        /// Version 5 enhanced 3D spatialization with faster matrix operations.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public virtual uint Estimate(Reverb3dCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (SampleCount == 160)
            {
                if (command.Enabled)
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => (uint)114750.00f, // 1 ch enabled
                        2 => (uint)123910.00f, // 2 ch enabled
                        4 => (uint)144340.00f, // 4 ch enabled
                        6 => (uint)163810.00f, // 6 ch enabled
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                    };
                }

                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)685.00f,   // 1 ch disabled
                    2 => (uint)716.62f,   // 2 ch disabled
                    4 => (uint)784.07f,   // 4 ch disabled
                    6 => (uint)825.44f,   // 6 ch disabled
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                };
            }

            if (command.Enabled)
            {
                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)168290.00f, // 1 ch enabled
                    2 => (uint)181880.00f, // 2 ch enabled
                    4 => (uint)212700.00f, // 4 ch enabled
                    6 => (uint)241850.00f, // 6 ch enabled
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                };
            }

            return command.Parameter.ChannelCount switch
            {
                1 => (uint)468.47f,  // 1 ch disabled
                2 => (uint)542.45f,  // 2 ch disabled
                4 => (uint)586.42f,  // 4 ch disabled
                6 => (uint)642.47f,  // 6 ch disabled
                _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
            };
        }

        /// <summary>
        /// Estimates the processing time for a <see cref="CompressorCommand"/>.
        /// Version 5 optimized envelope detection and gain smoothing.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public virtual uint Estimate(CompressorCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (SampleCount == 160)
            {
                if (command.Enabled)
                {
                    if (command.Parameter.StatisticsEnabled)
                    {
                        return command.Parameter.ChannelCount switch
                        {
                            1 => (uint)21100.00f, // with stats
                            2 => (uint)32211.00f,
                            4 => (uint)40587.00f,
                            6 => (uint)57819.00f,
                            _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                        };
                    }
                    else
                    {
                        return command.Parameter.ChannelCount switch
                        {
                            1 => (uint)18052.00f, // without stats
                            2 => (uint)28852.00f,
                            4 => (uint)36904.00f,
                            6 => (uint)54020.00f,
                            _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                        };
                    }
                }

                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)580.12f,  // disabled
                    2 => (uint)588.27f,
                    4 => (uint)655.86f,
                    6 => (uint)732.02f,
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                };
            }

            if (command.Enabled)
            {
                if (command.Parameter.StatisticsEnabled)
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => (uint)31518.00f,
                        2 => (uint)48102.00f,
                        4 => (uint)60685.00f,
                        6 => (uint)86250.00f,
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                    };
                }
                else
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => (uint)26963.00f,
                        2 => (uint)43016.00f,
                        4 => (uint)55183.00f,
                        6 => (uint)80862.00f,
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
                    };
                }
            }

            return command.Parameter.ChannelCount switch
            {
                1 => (uint)790.14f,
                2 => (uint)776.10f,
                4 => (uint)851.88f,
                6 => (uint)915.29f,
                _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}")
            };
        }

        /// <summary>
        /// Estimates the processing time for a <see cref="BiquadFilterAndMixCommand"/>.
        /// Version 5 uses vectorized biquad processing for better throughput.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public virtual uint Estimate(BiquadFilterAndMixCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (command.HasVolumeRamp)
            {
                return SampleCount == 160 ? (uint)5004.00f : (uint)6483.00f;
            }
            else
            {
                return SampleCount == 160 ? (uint)3227.00f : (uint)4552.00f;
            }
        }

        /// <summary>
        /// Estimates the processing time for a <see cref="MultiTapBiquadFilterAndMixCommand"/>.
        /// Version 5 optimized multi-tap filter chaining and memory layout.
        /// </summary>
        /// <param name="command">The command to estimate.</param>
        /// <returns>The estimated processing time in cycles.</returns>
        public virtual uint Estimate(MultiTapBiquadFilterAndMixCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (command.HasVolumeRamp)
            {
                return SampleCount == 160 ? (uint)7739.00f : (uint)10469.00f;
            }
            else
            {
                return SampleCount == 160 ? (uint)6056.00f : (uint)8483.00f;
            }
        }
    }
}
