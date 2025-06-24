using System;
using System.Diagnostics.CodeAnalysis;

namespace Ryujinx.Graphics.Shader.Translation
{
    [Flags]
    [SuppressMessage("Design", "CA1069: Enums values should not be duplicated")]
    enum AggregateType
    {
        Invalid,
        Void,
        Bool,
        FP32,
        FP64,
        S32,
        U32,
        Unsupported, // 添加 Unsupported 成员
        
        // 新增特殊类型处理
        FallbackFP32 = 0x400,  // 用于标记回退类型
        FallbackVector4 = 0x800,

        ElementTypeMask = 0xff,

        ElementCountShift = 8,
        ElementCountMask = 3 << ElementCountShift,

        Scalar = 0 << ElementCountShift,
        Vector2 = 1 << ElementCountShift,
        Vector3 = 2 << ElementCountShift,
        Vector4 = 3 << ElementCountShift,

        Array = 1 << 10,
    }

    static class AggregateTypeExtensions
    {
        public static bool IsValid(this AggregateType type)
        {
            // 检查是否包含有效的基础类型
            var baseType = type & AggregateType.ElementTypeMask;
            return baseType != AggregateType.Invalid && 
                   baseType != AggregateType.Void &&
                   baseType != AggregateType.Unsupported; // 添加 Unsupported 检查
        }
        
        public static AggregateType Sanitize(this AggregateType type)
        {
            if (type.IsValid()) return type;
            
            // 提供智能回退策略
            if ((type & AggregateType.ElementCountMask) == AggregateType.Vector4)
            {
                return AggregateType.Vector4 | AggregateType.FP32;
            }
            
            return AggregateType.FP32 | AggregateType.FallbackFP32;
        }

        public static int GetSizeInBytes(this AggregateType type)
        {
            // 先进行消毒处理
            type = type.Sanitize();
            
            // 移除回退标记
            type = type & ~(AggregateType.FallbackFP32 | AggregateType.FallbackVector4);
            
            int elementSize = (type & AggregateType.ElementTypeMask) switch
            {
                AggregateType.Bool or
                AggregateType.FP32 or
                AggregateType.S32 or
                AggregateType.U32 => 4,
                AggregateType.FP64 => 8,
                _ => 0,
            };

            switch (type & AggregateType.ElementCountMask)
            {
                case AggregateType.Vector2:
                    elementSize *= 2;
                    break;
                case AggregateType.Vector3:
                    elementSize *= 3;
                    break;
                case AggregateType.Vector4:
                    elementSize *= 4;
                    break;
            }

            return elementSize;
        }
    }
}
