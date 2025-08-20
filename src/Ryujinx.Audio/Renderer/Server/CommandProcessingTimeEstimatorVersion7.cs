using Ryujinx.Audio.Renderer.Dsp.Command;
using System;
using System.Diagnostics;

namespace Ryujinx.Audio.Renderer.Server
{
    /// <summary>
    /// <see cref="ICommandProcessingTimeEstimator"/> version 7. (added with REV15)
    /// </summary>
    public class CommandProcessingTimeEstimatorVersion7 : CommandProcessingTimeEstimatorVersion5
    {
        public CommandProcessingTimeEstimatorVersion7(uint sampleCount, uint bufferCount) : base(sampleCount, bufferCount) { }

        // REV15 对延迟命令的性能优化
        public override uint Estimate(DelayCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (SampleCount == 160)
            {
                if (command.Enabled)
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => 8529,  // REV15 优化
                        2 => 24501, // REV15 优化
                        4 => 46760, // REV15 优化
                        6 => 80203, // REV15 优化
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                    };
                }

                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)1195.20f, // REV15 优化
                    2 => (uint)1113.60f, // REV15 优化
                    4 => (uint)842.03f,  // REV15 优化
                    6 => (uint)901.6f,   // REV15 优化
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                };
            }

            if (command.Enabled)
            {
                return command.Parameter.ChannelCount switch
                {
                    1 => 11541,  // REV15 优化
                    2 => 36197,  // REV15 优化
                    4 => 68750,  // REV15 优化
                    6 => 11804,  // REV15 优化
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                };
            }

            return command.Parameter.ChannelCount switch
            {
                1 => (uint)897.67f,  // REV15 优化
                2 => (uint)877.63f,  // REV15 优化
                4 => (uint)692.31f,  // REV15 优化
                6 => (uint)775.43f,  // REV15 优化
                _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
            };
        }

        // REV15 对混响命令的性能优化
        public override uint Estimate(ReverbCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (SampleCount == 160)
            {
                if (command.Enabled)
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => 80475,  // REV15 优化
                        2 => 83975,  // REV15 优化
                        4 => 90625,  // REV15 优化
                        6 => 94332,  // REV15 优化
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                    };
                }

                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)486.30f,  // REV15 优化
                    2 => (uint)538.80f,  // REV15 优化
                    4 => (uint)593.70f,  // REV15 优化
                    6 => (uint)656.0f,   // REV15 优化
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                };
            }

            if (command.Enabled)
            {
                return command.Parameter.ChannelCount switch
                {
                    1 => 119170, // REV15 优化
                    2 => 124260, // REV15 优化
                    4 => 134750, // REV15 优化
                    6 => 140130, // REV15 优化
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                };
            }

            return command.Parameter.ChannelCount switch
            {
                1 => (uint)567.64f,  // REV15 优化
                2 => (uint)609.54f,  // REV15 优化
                4 => (uint)661.44f,  // REV15 优化
                6 => (uint)728.07f,  // REV15 优化
                _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
            };
        }

        // REV15 对3D混响命令的性能优化
        public override uint Estimate(Reverb3dCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (SampleCount == 160)
            {
                if (command.Enabled)
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => 114750, // REV15 优化
                        2 => 123910, // REV15 优化
                        4 => 144340, // REV15 优化
                        6 => 163810, // REV15 优化
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                    };
                }

                return command.Parameter.ChannelCount switch
                {
                    1 => 685,        // REV15 优化
                    2 => (uint)716.62f, // REV15 优化
                    4 => (uint)784.07f, // REV15 优化
                    6 => (uint)825.44f, // REV15 优化
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                };
            }

            if (command.Enabled)
            {
                return command.Parameter.ChannelCount switch
                {
                    1 => 168290, // REV15 优化
                    2 => 181880, // REV15 优化
                    4 => 212700, // REV15 优化
                    6 => 241850, // REV15 优化
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                };
            }

            return command.Parameter.ChannelCount switch
            {
                1 => (uint)468.47f, // REV15 优化
                2 => (uint)542.45f, // REV15 优化
                4 => (uint)586.42f, // REV15 优化
                6 => (uint)642.47f, // REV15 优化
                _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
            };
        }

        // REV15 对压缩器命令的性能优化
        public override uint Estimate(CompressorCommand command)
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
                            1 => 21100,  // REV15 优化
                            2 => 32211,  // REV15 优化
                            4 => 40587,  // REV15 优化
                            6 => 57819,  // REV15 优化
                            _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                        };
                    }
                    else
                    {
                        return command.Parameter.ChannelCount switch
                        {
                            1 => 18052,  // REV15 优化
                            2 => 28852,  // REV15 优化
                            4 => 36904,  // REV15 优化
                            6 => 54020,  // REV15 优化
                            _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                        };
                    }
                }

                return command.Parameter.ChannelCount switch
                {
                    1 => (uint)580.12f,  // REV15 优化
                    2 => (uint)588.27f,  // REV15 优化
                    4 => (uint)655.86f,  // REV15 优化
                    6 => (uint)732.02f,  // REV15 优化
                    _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                };
            }

            if (command.Enabled)
            {
                if (command.Parameter.StatisticsEnabled)
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => 31518,  // REV15 优化
                        2 => 48102,  // REV15 优化
                        4 => 60685,  // REV15 优化
                        6 => 86250,  // REV15 优化
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                    };
                }
                else
                {
                    return command.Parameter.ChannelCount switch
                    {
                        1 => 26963,  // REV15 优化
                        2 => 43016,  // REV15 优化
                        4 => 55183,  // REV15 优化
                        6 => 80862,  // REV15 优化
                        _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
                    };
                }
            }

            return command.Parameter.ChannelCount switch
            {
                1 => (uint)790.14f,  // REV15 优化
                2 => (uint)776.1f,   // REV15 优化
                4 => (uint)851.88f,  // REV15 优化
                6 => (uint)915.29f,  // REV15 优化
                _ => throw new NotImplementedException($"{command.Parameter.ChannelCount}"),
            };
        }

        // REV15 对双二阶滤波器和混合命令的性能优化
        public override uint Estimate(BiquadFilterAndMixCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (command.HasVolumeRamp)
            {
                if (SampleCount == 160)
                {
                    return 5004;  // REV15 优化
                }

                return 6483;  // REV15 优化
            }
            else
            {
                if (SampleCount == 160)
                {
                    return 3227;  // REV15 优化
                }

                return 4552;  // REV15 优化
            }
        }

        // REV15 对多抽头双二阶滤波器和混合命令的性能优化
        public override uint Estimate(MultiTapBiquadFilterAndMixCommand command)
        {
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (command.HasVolumeRamp)
            {
                if (SampleCount == 160)
                {
                    return 7739;  // REV15 优化
                }

                return 10469;  // REV15 优化
            }
            else
            {
                if (SampleCount == 160)
                {
                    return 6056;  // REV15 优化
                }

                return 8483;  // REV15 优化
            }
        }

        // REV15 可能引入了新的命令类型
        // 这里添加对新命令的估算方法
        // 例如：
        /*
        public override uint Estimate(NewRev15Command command)
        {
            // 实现REV15新命令的时间估算
            Debug.Assert(SampleCount == 160 || SampleCount == 240);

            if (SampleCount == 160)
            {
                return 10000; // 示例值
            }

            return 15000; // 示例值
        }
        */
    }
}
