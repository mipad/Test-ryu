using Ryujinx.Audio.Renderer.Server.Effect;
using Ryujinx.Common.Memory;
using System.Runtime.InteropServices;

namespace Ryujinx.Audio.Renderer.Parameter.Effect
{
    /// <summary>
    /// <see cref="IEffectInParameter.SpecificData"/> for <see cref="Common.EffectType.BiquadFilter"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]  // 明确指定 Pack=4 以确保4字节对齐
    public struct BiquadFilterEffectParameter2
    {
        /// <summary>
        /// The input channel indices that will be used by the <see cref="Dsp.AudioProcessor"/>.
        /// </summary>
        public Array6<byte> Input;

        /// <summary>
        /// The output channel indices that will be used by the <see cref="Dsp.AudioProcessor"/>.
        /// </summary>
        public Array6<byte> Output;

        /// <summary>
        /// 改为保留字段数组，确保与旧版本兼容
        /// </summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        private readonly byte[] _reserved1;  // 改为4字节数组而不是uint

        /// <summary>
        /// Biquad filter numerator (b0, b1, b2).
        /// </summary>
        public Array3<float> Numerator;

        /// <summary>
        /// Biquad filter denominator (a1, a2).
        /// </summary>
        /// <remarks>a0 = 1</remarks>
        public Array2<float> Denominator;

        /// <summary>
        /// The total channel count used.
        /// </summary>
        public byte ChannelCount;

        /// <summary>
        /// The current usage status of the effect on the client side.
        /// </summary>
        public UsageState Status;
        
        /// <summary>
        /// 保留字段改为2字节数组
        /// </summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        private readonly byte[] _reserved2;
        
        /// <summary>
        /// 构造函数，确保保留字段初始化
        /// </summary>
        public BiquadFilterEffectParameter2()
        {
            Input = default;
            Output = default;
            _reserved1 = new byte[4];
            Numerator = default;
            Denominator = default;
            ChannelCount = 0;
            Status = UsageState.Invalid;
            _reserved2 = new byte[2];
        }
    }
}
