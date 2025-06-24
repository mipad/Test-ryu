using Ryujinx.Graphics.Shader.Translation;
using System;

namespace Ryujinx.Graphics.Shader
{
    public enum AttributeType : byte
    {
        // Generic types.
        Invalid = 0,
        Float,
        Sint,
        Uint,
        Sscaled,
        Uscaled,

        Packed = 1 << 6,
        PackedRgb10A2Signed = 1 << 7,
        AnyPacked = Packed | PackedRgb10A2Signed,
    }

    static class AttributeTypeExtensions
    {
        public static AggregateType ToAggregateType(this AttributeType type)
        {
            // 安全处理Invalid类型
            if (type == AttributeType.Invalid)
            {
                return AggregateType.FP32;
            }
            
            var baseType = type & ~AttributeType.AnyPacked;
            return baseType switch
            {
                AttributeType.Float => AggregateType.FP32,
                AttributeType.Sint => AggregateType.S32,
                AttributeType.Uint => AggregateType.U32,
                _ => AggregateType.FP32 // 默认回退
            };
        }

        public static AggregateType ToAggregateType(this AttributeType type, bool supportsScaledFormats)
        {
            // 安全处理Invalid类型
            if (type == AttributeType.Invalid)
            {
                return AggregateType.FP32;
            }
            
            var baseType = type & ~AttributeType.AnyPacked;
            return baseType switch
            {
                AttributeType.Float => AggregateType.FP32,
                AttributeType.Sint => AggregateType.S32,
                AttributeType.Uint => AggregateType.U32,
                AttributeType.Sscaled => supportsScaledFormats ? AggregateType.FP32 : AggregateType.S32,
                AttributeType.Uscaled => supportsScaledFormats ? AggregateType.FP32 : AggregateType.U32,
                _ => AggregateType.FP32 // 默认回退
            };
        }

        // 新增安全转换方法
        public static AttributeType Sanitize(this AttributeType type)
        {
            return type == AttributeType.Invalid ? AttributeType.Float : type;
        }
    }
}
