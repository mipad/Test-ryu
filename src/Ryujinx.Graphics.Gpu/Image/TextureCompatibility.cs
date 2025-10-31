using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Texture;
using System;
using System.Diagnostics;
using System.Numerics;

namespace Ryujinx.Graphics.Gpu.Image
{
    /// <summary>
    /// Texture format compatibility checks.
    /// </summary>
    static class TextureCompatibility
    {
        private enum FormatClass
        {
            Unclassified,
            Bc1Rgba,
            Bc2,
            Bc3,
            Bc4,
            Bc5,
            Bc6,
            Bc7,
            Etc2Rgb,
            Etc2Rgba,
            Astc4x4,
            Astc5x4,
            Astc5x5,
            Astc6x5,
            Astc6x6,
            Astc8x5,
            Astc8x6,
            Astc8x8,
            Astc10x5,
            Astc10x6,
            Astc10x8,
            Astc10x10,
            Astc12x10,
            Astc12x12,
        }

        /// <summary>
        /// Checks if a format is host incompatible.
        /// </summary>
        /// <remarks>
        /// Host incompatible formats can't be used directly, the texture data needs to be converted
        /// to a compatible format first.
        /// </remarks>
        /// <param name="info">Texture information</param>
        /// <param name="caps">Host GPU capabilities</param>
        /// <returns>True if the format is incompatible, false otherwise</returns>
        public static bool IsFormatHostIncompatible(TextureInfo info, Capabilities caps)
        {
            Format originalFormat = info.FormatInfo.Format;
            FormatInfo compatibleFormat = ToHostCompatibleFormat(info, caps);
            
            bool isIncompatible = compatibleFormat.Format != originalFormat;
            
            if (isIncompatible)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"纹理格式不兼容: 原始格式 {originalFormat} -> 兼容格式 {compatibleFormat.Format}, " +
                    $"纹理信息: {info.Width}x{info.Height}, 目标: {info.Target}");
            }
            
            return isIncompatible;
        }

        /// <summary>
        /// Converts a incompatible format to a host compatible format, or return the format directly
        /// if it is already host compatible.
        /// </summary>
        /// <remarks>
        /// This can be used to convert a incompatible compressed format to the decompressor
        /// output format.
        /// </remarks>
        /// <param name="info">Texture information</param>
        /// <param name="caps">Host GPU capabilities</param>
        /// <returns>A host compatible format</returns>
        public static FormatInfo ToHostCompatibleFormat(TextureInfo info, Capabilities caps)
        {
            Format originalFormat = info.FormatInfo.Format;
            
            Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                $"检查纹理格式兼容性: 格式={originalFormat}, 大小={info.Width}x{info.Height}, " +
                $"目标={info.Target}, 线性={info.IsLinear}");

            // The host API does not support those compressed formats.
            // We assume software decompression will be done for those textures,
            // and so we adjust the format here to match the decompressor output.

            if (!caps.SupportsAstcCompression)
            {
                if (info.FormatInfo.Format.IsAstcUnorm())
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "ASTC格式不支持，进行格式转换");
                    return GraphicsConfig.EnableTextureRecompression
                        ? new FormatInfo(Format.Bc7Unorm, 4, 4, 16, 4)
                        : new FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
                }
                else if (info.FormatInfo.Format.IsAstcSrgb())
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "ASTC sRGB格式不支持，进行格式转换");
                    return GraphicsConfig.EnableTextureRecompression
                        ? new FormatInfo(Format.Bc7Srgb, 4, 4, 16, 4)
                        : new FormatInfo(Format.R8G8B8A8Srgb, 1, 1, 4, 4);
                }
            }

            if (!HostSupportsBcFormat(info.FormatInfo.Format, info.Target, caps))
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"BC格式不支持: {info.FormatInfo.Format}, 进行软件解码");
                    
                switch (info.FormatInfo.Format)
                {
                    case Format.Bc1RgbaSrgb:
                    case Format.Bc2Srgb:
                    case Format.Bc3Srgb:
                    case Format.Bc7Srgb:
                        return new FormatInfo(Format.R8G8B8A8Srgb, 1, 1, 4, 4);
                    case Format.Bc1RgbaUnorm:
                    case Format.Bc2Unorm:
                    case Format.Bc3Unorm:
                    case Format.Bc7Unorm:
                        return new FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
                    case Format.Bc4Unorm:
                        return new FormatInfo(Format.R8Unorm, 1, 1, 1, 1);
                    case Format.Bc4Snorm:
                        return new FormatInfo(Format.R8Snorm, 1, 1, 1, 1);
                    case Format.Bc5Unorm:
                        return new FormatInfo(Format.R8G8Unorm, 1, 1, 2, 2);
                    case Format.Bc5Snorm:
                        return new FormatInfo(Format.R8G8Snorm, 1, 1, 2, 2);
                    case Format.Bc6HSfloat:
                    case Format.Bc6HUfloat:
                        return new FormatInfo(Format.R16G16B16A16Float, 1, 1, 8, 4);
                }
            }

            if (!caps.SupportsEtc2Compression)
            {
                if (info.FormatInfo.Format.IsEtc2())
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"ETC2格式不支持: {info.FormatInfo.Format}, 进行格式转换");
                }
                
                switch (info.FormatInfo.Format)
                {
                    case Format.Etc2RgbaSrgb:
                    case Format.Etc2RgbPtaSrgb:
                    case Format.Etc2RgbSrgb:
                        return new FormatInfo(Format.R8G8B8A8Srgb, 1, 1, 4, 4);
                    case Format.Etc2RgbaUnorm:
                    case Format.Etc2RgbPtaUnorm:
                    case Format.Etc2RgbUnorm:
                        return new FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
                }
            }

            if (!caps.SupportsR4G4Format && info.FormatInfo.Format == Format.R4G4Unorm)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    "R4G4格式不支持，进行格式转换");
                    
                if (caps.SupportsR4G4B4A4Format)
                {
                    return new FormatInfo(Format.R4G4B4A4Unorm, 1, 1, 2, 4);
                }
                else
                {
                    return new FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
                }
            }

            if (info.FormatInfo.Format == Format.R4G4B4A4Unorm)
            {
                if (!caps.SupportsR4G4B4A4Format)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "R4G4B4A4格式不支持，转换为R8G8B8A8");
                    return new FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
                }
            }
            else if (!caps.Supports5BitComponentFormat && info.FormatInfo.Format.Is16BitPacked())
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"16位压缩格式不支持: {info.FormatInfo.Format}, 转换为RGBA8");
                return new FormatInfo(info.FormatInfo.Format.IsBgr() ? Format.B8G8R8A8Unorm : Format.R8G8B8A8Unorm, 1, 1, 4, 4);
            }

            Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                $"纹理格式兼容，使用原始格式: {originalFormat}");
            return info.FormatInfo;
        }

        /// <summary>
        /// Checks if the host API supports a given texture compression format of the BC family.
        /// </summary>
        /// <param name="format">BC format to be checked</param>
        /// <param name="target">Target usage of the texture</param>
        /// <param name="caps">Host GPU Capabilities</param>
        /// <returns>True if the texture host supports the format with the given target usage, false otherwise</returns>
        public static bool HostSupportsBcFormat(Format format, Target target, Capabilities caps)
        {
            bool not3DOr3DCompressionSupported = target != Target.Texture3D || caps.Supports3DTextureCompression;

            bool supported = true;
            
            switch (format)
            {
                case Format.Bc1RgbaSrgb:
                case Format.Bc1RgbaUnorm:
                case Format.Bc2Srgb:
                case Format.Bc2Unorm:
                case Format.Bc3Srgb:
                case Format.Bc3Unorm:
                    supported = caps.SupportsBc123Compression && not3DOr3DCompressionSupported;
                    if (!supported)
                    {
                        Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                            $"BC1/2/3格式不支持: {format}, BC123支持={caps.SupportsBc123Compression}, 3D压缩支持={caps.Supports3DTextureCompression}");
                    }
                    break;
                case Format.Bc4Unorm:
                case Format.Bc4Snorm:
                case Format.Bc5Unorm:
                case Format.Bc5Snorm:
                    supported = caps.SupportsBc45Compression && not3DOr3DCompressionSupported;
                    if (!supported)
                    {
                        Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                            $"BC4/5格式不支持: {format}, BC45支持={caps.SupportsBc45Compression}, 3D压缩支持={caps.Supports3DTextureCompression}");
                    }
                    break;
                case Format.Bc6HSfloat:
                case Format.Bc6HUfloat:
                case Format.Bc7Srgb:
                case Format.Bc7Unorm:
                    supported = caps.SupportsBc67Compression && not3DOr3DCompressionSupported;
                    if (!supported)
                    {
                        Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                            $"BC6/7格式不支持: {format}, BC67支持={caps.SupportsBc67Compression}, 3D压缩支持={caps.Supports3DTextureCompression}");
                    }
                    break;
            }

            if (supported)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"BC格式支持: {format}, 目标={target}");
            }
            
            return supported;
        }

        /// <summary>
        /// Determines whether a texture can flush its data back to guest memory.
        /// </summary>
        /// <param name="info">Texture information</param>
        /// <param name="caps">Host GPU Capabilities</param>
        /// <returns>True if the texture can flush, false otherwise</returns>
        public static bool CanTextureFlush(TextureInfo info, Capabilities caps)
        {
            if (IsFormatHostIncompatible(info, caps))
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"纹理无法刷新: 格式不兼容 {info.FormatInfo.Format}");
                return false; // Flushing this format is not supported, as it may have been converted to another host format.
            }

            if (info.Target == Target.Texture2DMultisample ||
                info.Target == Target.Texture2DMultisampleArray)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"纹理无法刷新: 多重采样纹理不支持刷新 {info.Target}");
                return false; // Flushing multisample textures is not supported, the host does not allow getting their data.
            }

            Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                $"纹理可以刷新: {info.FormatInfo.Format}, {info.Target}");
            return true;
        }

        /// <summary>
        /// Checks if the texture format matches with the specified texture information.
        /// </summary>
        /// <param name="lhs">Texture information to compare</param>
        /// <param name="rhs">Texture information to compare with</param>
        /// <param name="forSampler">Indicates that the texture will be used for shader sampling</param>
        /// <param name="depthAlias">Indicates if aliasing between color and depth format should be allowed</param>
        /// <returns>A value indicating how well the formats match</returns>
        public static TextureMatchQuality FormatMatches(TextureInfo lhs, TextureInfo rhs, bool forSampler, bool depthAlias)
        {
            // D32F and R32F texture have the same representation internally,
            // however the R32F format is used to sample from depth textures.
            if (IsValidDepthAsColorAlias(lhs.FormatInfo.Format, rhs.FormatInfo.Format) && (forSampler || depthAlias))
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"深度/颜色格式别名: {lhs.FormatInfo.Format} -> {rhs.FormatInfo.Format}");
                return TextureMatchQuality.FormatAlias;
            }

            if (depthAlias)
            {
                // The 2D engine does not support depth-stencil formats, so it will instead
                // use equivalent color formats. We must also consider them as compatible.
                if (lhs.FormatInfo.Format == Format.S8Uint && rhs.FormatInfo.Format == Format.R8Unorm)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "S8Uint -> R8Unorm 格式别名");
                    return TextureMatchQuality.FormatAlias;
                }
                else if ((lhs.FormatInfo.Format == Format.D24UnormS8Uint ||
                          lhs.FormatInfo.Format == Format.S8UintD24Unorm ||
                          lhs.FormatInfo.Format == Format.X8UintD24Unorm) && rhs.FormatInfo.Format == Format.B8G8R8A8Unorm)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "D24S8 -> BGRA8 格式别名");
                    return TextureMatchQuality.FormatAlias;
                }
                else if (lhs.FormatInfo.Format == Format.D32FloatS8Uint && rhs.FormatInfo.Format == Format.R32G32Float)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "D32FS8 -> R32G32 格式别名");
                    return TextureMatchQuality.FormatAlias;
                }
            }

            bool perfectMatch = lhs.FormatInfo.Format == rhs.FormatInfo.Format;
            
            if (!perfectMatch)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"格式不匹配: {lhs.FormatInfo.Format} != {rhs.FormatInfo.Format}, " +
                    $"采样器={forSampler}, 深度别名={depthAlias}");
            }
            else
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"格式完美匹配: {lhs.FormatInfo.Format}");
            }
            
            return perfectMatch ? TextureMatchQuality.Perfect : TextureMatchQuality.NoMatch;
        }

        /// <summary>
        /// Checks if the texture layout specified matches with this texture layout.
        /// The layout information is composed of the Stride for linear textures, or GOB block size
        /// for block linear textures.
        /// </summary>
        /// <param name="lhs">Texture information to compare</param>
        /// <param name="rhs">Texture information to compare with</param>
        /// <returns>True if the layout matches, false otherwise</returns>
        public static bool LayoutMatches(TextureInfo lhs, TextureInfo rhs)
        {
            if (lhs.IsLinear != rhs.IsLinear)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"布局不匹配: 线性模式不同 {lhs.IsLinear} != {rhs.IsLinear}");
                return false;
            }

            // For linear textures, gob block sizes are ignored.
            // For block linear textures, the stride is ignored.
            if (rhs.IsLinear)
            {
                bool strideMatch = lhs.Stride == rhs.Stride;
                if (!strideMatch)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"线性纹理步长不匹配: {lhs.Stride} != {rhs.Stride}");
                }
                return strideMatch;
            }
            else
            {
                bool gobMatch = lhs.GobBlocksInY == rhs.GobBlocksInY &&
                       lhs.GobBlocksInZ == rhs.GobBlocksInZ;
                       
                if (!gobMatch)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"块线性纹理GOB不匹配: Y={lhs.GobBlocksInY}!={rhs.GobBlocksInY}, Z={lhs.GobBlocksInZ}!={rhs.GobBlocksInZ}");
                }
                return gobMatch;
            }
        }

        /// <summary>
        /// Obtain the minimum compatibility level of two provided view compatibility results.
        /// </summary>
        /// <param name="first">The first compatibility level</param>
        /// <param name="second">The second compatibility level</param>
        /// <returns>The minimum compatibility level of two provided view compatibility results</returns>
        public static TextureViewCompatibility PropagateViewCompatibility(TextureViewCompatibility first, TextureViewCompatibility second)
        {
            TextureViewCompatibility result = (TextureViewCompatibility)Math.Min((int)first, (int)second);
            Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                $"传播视图兼容性: {first} + {second} -> {result}");
            return result;
        }

        /// <summary>
        /// Checks if the sizes of two texture levels are copy compatible.
        /// </summary>
        /// <param name="lhs">Texture information of the texture view</param>
        /// <param name="rhs">Texture information of the texture view to match against</param>
        /// <param name="lhsLevel">Mipmap level of the texture view in relation to this texture</param>
        /// <param name="rhsLevel">Mipmap level of the texture view in relation to the second texture</param>
        /// <returns>True if both levels are view compatible</returns>
        public static bool CopySizeMatches(TextureInfo lhs, TextureInfo rhs, int lhsLevel, int rhsLevel)
        {
            Size size = GetAlignedSize(lhs, lhsLevel);
            Size otherSize = GetAlignedSize(rhs, rhsLevel);

            if (size.Width == otherSize.Width && size.Height == otherSize.Height)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"拷贝尺寸匹配: {size.Width}x{size.Height}, 层级 {lhsLevel}->{rhsLevel}");
                return true;
            }
            else if (lhs.IsLinear && rhs.IsLinear)
            {
                // Copy between linear textures with matching stride.
                int stride = BitUtils.AlignUp(Math.Max(1, lhs.Stride >> lhsLevel), Constants.StrideAlignment);

                bool strideMatch = stride == rhs.Stride;
                if (!strideMatch)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"线性纹理步长不匹配: {stride} != {rhs.Stride}, 层级 {lhsLevel}->{rhsLevel}");
                }
                return strideMatch;
            }
            else
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"拷贝尺寸不匹配: {size.Width}x{size.Height} != {otherSize.Width}x{otherSize.Height}, " +
                    $"层级 {lhsLevel}->{rhsLevel}, 线性={lhs.IsLinear}&{rhs.IsLinear}");
                return false;
            }
        }

        /// <summary>
        /// Checks if the sizes of two given textures are view compatible.
        /// </summary>
        /// <param name="lhs">Texture information of the texture view</param>
        /// <param name="rhs">Texture information of the texture view to match against</param>
        /// <param name="exact">Indicates if the sizes must be exactly equal</param>
        /// <param name="level">Mipmap level of the texture view in relation to this texture</param>
        /// <returns>The view compatibility level of the view sizes</returns>
        public static TextureViewCompatibility ViewSizeMatches(TextureInfo lhs, TextureInfo rhs, bool exact, int level)
        {
            Size lhsAlignedSize = GetAlignedSize(lhs, level);
            Size rhsAlignedSize = GetAlignedSize(rhs);

            Size lhsSize = GetSizeInBlocks(lhs, level);
            Size rhsSize = GetSizeInBlocks(rhs);

            bool alignedWidthMatches = lhsAlignedSize.Width == rhsAlignedSize.Width;

            if (lhs.FormatInfo.BytesPerPixel != rhs.FormatInfo.BytesPerPixel && IsIncompatibleFormatAliasingAllowed(lhs.FormatInfo, rhs.FormatInfo))
            {
                // If the formats are incompatible, but the texture strides match,
                // we might allow them to be copy compatible depending on the format.
                // The strides are aligned because the format with higher bytes per pixel
                // might need a bit of padding at the end due to one width not being a multiple of the other.

                Debug.Assert((1 << BitOperations.Log2((uint)lhs.FormatInfo.BytesPerPixel)) == lhs.FormatInfo.BytesPerPixel);
                Debug.Assert((1 << BitOperations.Log2((uint)rhs.FormatInfo.BytesPerPixel)) == rhs.FormatInfo.BytesPerPixel);

                int alignment = Math.Max(lhs.FormatInfo.BytesPerPixel, rhs.FormatInfo.BytesPerPixel);

                int lhsStride = BitUtils.AlignUp(lhsSize.Width * lhs.FormatInfo.BytesPerPixel, alignment);
                int rhsStride = BitUtils.AlignUp(rhsSize.Width * rhs.FormatInfo.BytesPerPixel, alignment);

                alignedWidthMatches = lhsStride == rhsStride;
                
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"不兼容格式别名检查: BPP {lhs.FormatInfo.BytesPerPixel}->{rhs.FormatInfo.BytesPerPixel}, " +
                    $"步长 {lhsStride}->{rhsStride}, 匹配={alignedWidthMatches}");
            }

            TextureViewCompatibility result = TextureViewCompatibility.Full;

            // For copies, we can copy a subset of the 3D texture slices,
            // so the depth may be different in this case.
            if (rhs.Target == Target.Texture3D && lhsSize.Depth != rhsSize.Depth)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"3D纹理深度不匹配: {lhsSize.Depth} != {rhsSize.Depth}, 降级为仅拷贝");
                result = TextureViewCompatibility.CopyOnly;
            }

            // Some APIs align the width for copy and render target textures,
            // so the width may not match in this case for different uses of the same texture.
            // To account for this, we compare the aligned width here.
            // We expect height to always match exactly, if the texture is the same.
            if (alignedWidthMatches && lhsSize.Height == rhsSize.Height)
            {
                if (exact && lhsSize.Width != rhsSize.Width)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"精确尺寸不匹配: 宽度 {lhsSize.Width} != {rhsSize.Width}, 降级为仅拷贝");
                    return TextureViewCompatibility.CopyOnly;
                }
                else if (lhsSize.Width < rhsSize.Width)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"源宽度小于目标: {lhsSize.Width} < {rhsSize.Width}, 降级为仅拷贝");
                    return TextureViewCompatibility.CopyOnly;
                }
                else
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"视图尺寸匹配: {lhsSize.Width}x{lhsSize.Height} -> {rhsSize.Width}x{rhsSize.Height}, 结果={result}");
                    return result;
                }
            }
            else if (lhs.IsLinear && rhs.IsLinear && lhsSize.Height == rhsSize.Height)
            {
                // Copy between linear textures with matching stride.
                int stride = BitUtils.AlignUp(Math.Max(1, lhs.Stride >> level), Constants.StrideAlignment);

                if (stride == rhs.Stride)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"线性纹理步长匹配: {stride}, 降级为仅拷贝");
                    return TextureViewCompatibility.CopyOnly;
                }
                else
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"线性纹理步长不匹配: {stride} != {rhs.Stride}, 布局不兼容");
                    return TextureViewCompatibility.LayoutIncompatible;
                }
            }
            else if (lhs.Target.IsMultisample() != rhs.Target.IsMultisample() && alignedWidthMatches && lhsAlignedSize.Height == rhsAlignedSize.Height)
            {
                // Copy between multisample and non-multisample textures with mismatching size is allowed,
                // as long aligned size matches.
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    "多重采样/非多重采样纹理间拷贝, 对齐尺寸匹配, 降级为仅拷贝");
                return TextureViewCompatibility.CopyOnly;
            }
            else
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"视图尺寸不匹配: 对齐尺寸 {lhsAlignedSize.Width}x{lhsAlignedSize.Height} != {rhsAlignedSize.Width}x{rhsAlignedSize.Height}, " +
                    $"块尺寸 {lhsSize.Width}x{lhsSize.Height} != {rhsSize.Width}x{rhsSize.Height}, 布局不兼容");
                return TextureViewCompatibility.LayoutIncompatible;
            }
        }

        /// <summary>
        /// Checks if the potential child texture fits within the level and layer bounds of the parent.
        /// </summary>
        /// <param name="parent">Texture information for the parent</param>
        /// <param name="child">Texture information for the child</param>
        /// <param name="layer">Base layer of the child texture</param>
        /// <param name="level">Base level of the child texture</param>
        /// <returns>Full compatiblity if the child's layer and level count fit within the parent, incompatible otherwise</returns>
        public static TextureViewCompatibility ViewSubImagesInBounds(TextureInfo parent, TextureInfo child, int layer, int level)
        {
            bool inBounds = level + child.Levels <= parent.Levels &&
                layer + child.GetSlices() <= parent.GetSlices();
                
            if (!inBounds)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"子图像超出边界: 层级 {level}+{child.Levels}>{parent.Levels} 或 " +
                    $"层 {layer}+{child.GetSlices()}>{parent.GetSlices()}");
            }
            else
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"子图像在边界内: 层级 {level}+{child.Levels}<={parent.Levels}, " +
                    $"层 {layer}+{child.GetSlices()}<={parent.GetSlices()}");
            }

            return inBounds ? TextureViewCompatibility.Full : TextureViewCompatibility.LayoutIncompatible;
        }

        /// <summary>
        /// Checks if the texture sizes of the supplied texture informations match.
        /// </summary>
        /// <param name="lhs">Texture information to compare</param>
        /// <param name="rhs">Texture information to compare with</param>
        /// <param name="exact">Indicates if the size must be exactly equal between the textures, or if <paramref name="rhs"/> is allowed to be larger</param>
        /// <returns>True if the sizes matches, false otherwise</returns>
        public static bool SizeMatches(TextureInfo lhs, TextureInfo rhs, bool exact)
        {
            if (lhs.GetLayers() != rhs.GetLayers())
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"层数不匹配: {lhs.GetLayers()} != {rhs.GetLayers()}");
                return false;
            }

            Size lhsSize = GetSizeInBlocks(lhs);
            Size rhsSize = GetSizeInBlocks(rhs);

            if (exact || lhs.IsLinear || rhs.IsLinear)
            {
                bool exactMatch = lhsSize.Width == rhsSize.Width &&
                       lhsSize.Height == rhsSize.Height &&
                       lhsSize.Depth == rhsSize.Depth;
                       
                if (!exactMatch)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"精确尺寸不匹配: {lhsSize.Width}x{lhsSize.Height}x{lhsSize.Depth} != " +
                        $"{rhsSize.Width}x{rhsSize.Height}x{rhsSize.Depth}, 线性={lhs.IsLinear}|{rhs.IsLinear}");
                }
                return exactMatch;
            }
            else
            {
                Size lhsAlignedSize = GetAlignedSize(lhs);
                Size rhsAlignedSize = GetAlignedSize(rhs);

                bool sizeMatch = lhsAlignedSize.Width == rhsAlignedSize.Width &&
                       lhsSize.Width >= rhsSize.Width &&
                       lhsSize.Height == rhsSize.Height &&
                       lhsSize.Depth == rhsSize.Depth;
                       
                if (!sizeMatch)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"尺寸不匹配: 对齐宽度 {lhsAlignedSize.Width}!={rhsAlignedSize.Width} 或 " +
                        $"宽度 {lhsSize.Width}<{rhsSize.Width} 或 " +
                        $"高度 {lhsSize.Height}!={rhsSize.Height} 或 " +
                        $"深度 {lhsSize.Depth}!={rhsSize.Depth}");
                }
                return sizeMatch;
            }
        }

        /// <summary>
        /// Gets the aligned sizes for the given dimensions, using the specified texture information.
        /// The alignment depends on the texture layout and format bytes per pixel.
        /// </summary>
        /// <param name="info">Texture information to calculate the aligned size from</param>
        /// <param name="width">The width to be aligned</param>
        /// <param name="height">The height to be aligned</param>
        /// <param name="depth">The depth to be aligned</param>
        /// <returns>The aligned texture size</returns>
        private static Size GetAlignedSize(TextureInfo info, int width, int height, int depth)
        {
            if (info.IsLinear)
            {
                return SizeCalculator.GetLinearAlignedSize(
                    width,
                    height,
                    info.FormatInfo.BlockWidth,
                    info.FormatInfo.BlockHeight,
                    info.FormatInfo.BytesPerPixel);
            }
            else
            {
                return SizeCalculator.GetBlockLinearAlignedSize(
                    width,
                    height,
                    depth,
                    info.FormatInfo.BlockWidth,
                    info.FormatInfo.BlockHeight,
                    info.FormatInfo.BytesPerPixel,
                    info.GobBlocksInY,
                    info.GobBlocksInZ,
                    info.GobBlocksInTileX);
            }
        }

        /// <summary>
        /// Gets the aligned sizes of the specified texture information.
        /// The alignment depends on the texture layout and format bytes per pixel.
        /// </summary>
        /// <param name="info">Texture information to calculate the aligned size from</param>
        /// <param name="level">Mipmap level for texture views</param>
        /// <returns>The aligned texture size</returns>
        public static Size GetAlignedSize(TextureInfo info, int level = 0)
        {
            int width = Math.Max(1, info.Width >> level);
            int height = Math.Max(1, info.Height >> level);
            int depth = Math.Max(1, info.GetDepth() >> level);

            return GetAlignedSize(info, width, height, depth);
        }

        /// <summary>
        /// Gets the size in blocks for the given texture information.
        /// For non-compressed formats, that's the same as the regular size.
        /// </summary>
        /// <param name="info">Texture information to calculate the aligned size from</param>
        /// <param name="level">Mipmap level for texture views</param>
        /// <returns>The texture size in blocks</returns>
        public static Size GetSizeInBlocks(TextureInfo info, int level = 0)
        {
            int width = Math.Max(1, info.Width >> level);
            int height = Math.Max(1, info.Height >> level);
            int depth = Math.Max(1, info.GetDepth() >> level);

            return new Size(
                BitUtils.DivRoundUp(width, info.FormatInfo.BlockWidth),
                BitUtils.DivRoundUp(height, info.FormatInfo.BlockHeight),
                depth);
        }

        /// <summary>
        /// Check if it's possible to create a view with the layout of the second texture information from the first.
        /// The layout information is composed of the Stride for linear textures, or GOB block size
        /// for block linear textures.
        /// </summary>
        /// <param name="lhs">Texture information of the texture view</param>
        /// <param name="rhs">Texture information of the texture view to compare against</param>
        /// <param name="level">Start level of the texture view, in relation with the first texture</param>
        /// <returns>True if the layout is compatible, false otherwise</returns>
        public static bool ViewLayoutCompatible(TextureInfo lhs, TextureInfo rhs, int level)
        {
            if (lhs.IsLinear != rhs.IsLinear)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"视图布局不兼容: 线性模式不同 {lhs.IsLinear} != {rhs.IsLinear}");
                return false;
            }

            // For linear textures, gob block sizes are ignored.
            // For block linear textures, the stride is ignored.
            if (rhs.IsLinear)
            {
                int stride = Math.Max(1, lhs.Stride >> level);
                stride = BitUtils.AlignUp(stride, Constants.StrideAlignment);

                bool strideMatch = stride == rhs.Stride;
                if (!strideMatch)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"线性纹理视图步长不匹配: {stride} != {rhs.Stride}, 层级={level}");
                }
                return strideMatch;
            }
            else
            {
                int height = Math.Max(1, lhs.Height >> level);
                int depth = Math.Max(1, lhs.GetDepth() >> level);

                (int gobBlocksInY, int gobBlocksInZ) = SizeCalculator.GetMipGobBlockSizes(
                    height,
                    depth,
                    lhs.FormatInfo.BlockHeight,
                    lhs.GobBlocksInY,
                    lhs.GobBlocksInZ,
                    level);

                bool gobMatch = gobBlocksInY == rhs.GobBlocksInY &&
                       gobBlocksInZ == rhs.GobBlocksInZ;
                       
                if (!gobMatch)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"块线性纹理视图GOB不匹配: Y={gobBlocksInY}!={rhs.GobBlocksInY}, " +
                        $"Z={gobBlocksInZ}!={rhs.GobBlocksInZ}, 层级={level}");
                }
                return gobMatch;
            }
        }

        /// <summary>
        /// Check if it's possible to create a view with the layout of the second texture information from the first.
        /// The layout information is composed of the Stride for linear textures, or GOB block size
        /// for block linear textures.
        /// </summary>
        /// <param name="lhs">Texture information of the texture view</param>
        /// <param name="rhs">Texture information of the texture view to compare against</param>
        /// <param name="lhsLevel">Start level of the texture view, in relation with the first texture</param>
        /// <param name="rhsLevel">Start level of the texture view, in relation with the second texture</param>
        /// <returns>True if the layout is compatible, false otherwise</returns>
        public static bool ViewLayoutCompatible(TextureInfo lhs, TextureInfo rhs, int lhsLevel, int rhsLevel)
        {
            if (lhs.IsLinear != rhs.IsLinear)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"视图布局不兼容: 线性模式不同 {lhs.IsLinear} != {rhs.IsLinear}");
                return false;
            }

            // For linear textures, gob block sizes are ignored.
            // For block linear textures, the stride is ignored.
            if (rhs.IsLinear)
            {
                int lhsStride = Math.Max(1, lhs.Stride >> lhsLevel);
                lhsStride = BitUtils.AlignUp(lhsStride, Constants.StrideAlignment);

                int rhsStride = Math.Max(1, rhs.Stride >> rhsLevel);
                rhsStride = BitUtils.AlignUp(rhsStride, Constants.StrideAlignment);

                bool strideMatch = lhsStride == rhsStride;
                if (!strideMatch)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"线性纹理视图步长不匹配: {lhsStride} != {rhsStride}, 层级 {lhsLevel}->{rhsLevel}");
                }
                return strideMatch;
            }
            else
            {
                int lhsHeight = Math.Max(1, lhs.Height >> lhsLevel);
                int lhsDepth = Math.Max(1, lhs.GetDepth() >> lhsLevel);

                (int lhsGobBlocksInY, int lhsGobBlocksInZ) = SizeCalculator.GetMipGobBlockSizes(
                    lhsHeight,
                    lhsDepth,
                    lhs.FormatInfo.BlockHeight,
                    lhs.GobBlocksInY,
                    lhs.GobBlocksInZ,
                    lhsLevel);

                int rhsHeight = Math.Max(1, rhs.Height >> rhsLevel);
                int rhsDepth = Math.Max(1, rhs.GetDepth() >> rhsLevel);

                (int rhsGobBlocksInY, int rhsGobBlocksInZ) = SizeCalculator.GetMipGobBlockSizes(
                    rhsHeight,
                    rhsDepth,
                    rhs.FormatInfo.BlockHeight,
                    rhs.GobBlocksInY,
                    rhs.GobBlocksInZ,
                    rhsLevel);

                bool gobMatch = lhsGobBlocksInY == rhsGobBlocksInY &&
                       lhsGobBlocksInZ == rhsGobBlocksInZ;
                       
                if (!gobMatch)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        $"块线性纹理视图GOB不匹配: Y={lhsGobBlocksInY}!={rhsGobBlocksInY}, " +
                        $"Z={lhsGobBlocksInZ}!={rhsGobBlocksInZ}, 层级 {lhsLevel}->{rhsLevel}");
                }
                return gobMatch;
            }
        }

        /// <summary>
        /// Checks if the view format of the first texture format is compatible with the format of the second.
        /// In general, the formats are considered compatible if the bytes per pixel values are equal,
        /// but there are more complex rules for some formats, like compressed or depth-stencil formats.
        /// This follows the host API copy compatibility rules.
        /// </summary>
        /// <param name="lhs">Texture information of the texture view</param>
        /// <param name="rhs">Texture information of the texture view</param>
        /// <param name="caps">Host GPU capabilities</param>
        /// <param name="flags">Texture search flags</param>
        /// <returns>The view compatibility level of the texture formats</returns>
        public static TextureViewCompatibility ViewFormatCompatible(TextureInfo lhs, TextureInfo rhs, Capabilities caps, TextureSearchFlags flags)
        {
            FormatInfo lhsFormat = lhs.FormatInfo;
            FormatInfo rhsFormat = rhs.FormatInfo;

            Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                $"检查视图格式兼容性: {lhsFormat.Format} -> {rhsFormat.Format}, " +
                $"标志={flags}, 主机能力=[ASTC={caps.SupportsAstcCompression}, " +
                $"BC123={caps.SupportsBc123Compression}, ETC2={caps.SupportsEtc2Compression}]");

            if (lhsFormat.Format.IsDepthOrStencil() || rhsFormat.Format.IsDepthOrStencil())
            {
                bool forSampler = flags.HasFlag(TextureSearchFlags.ForSampler);
                bool depthAlias = flags.HasFlag(TextureSearchFlags.DepthAlias);

                TextureMatchQuality matchQuality = FormatMatches(lhs, rhs, forSampler, depthAlias);

                if (matchQuality == TextureMatchQuality.Perfect)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "深度/模板格式完美匹配");
                    return TextureViewCompatibility.Full;
                }
                else if (matchQuality == TextureMatchQuality.FormatAlias)
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "深度/模板格式别名匹配");
                    return TextureViewCompatibility.FormatAlias;
                }
                else if (IsValidColorAsDepthAlias(lhsFormat.Format, rhsFormat.Format) || IsValidDepthAsColorAlias(lhsFormat.Format, rhsFormat.Format))
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "颜色/深度格式别名, 降级为仅拷贝");
                    return TextureViewCompatibility.CopyOnly;
                }
                else
                {
                    Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                        "深度/模板格式不兼容");
                    return TextureViewCompatibility.Incompatible;
                }
            }

            if (IsFormatHostIncompatible(lhs, caps) || IsFormatHostIncompatible(rhs, caps))
            {
                bool formatMatch = lhsFormat.Format == rhsFormat.Format;
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"主机不兼容格式检查: 格式匹配={formatMatch}");
                return formatMatch ? TextureViewCompatibility.Full : TextureViewCompatibility.Incompatible;
            }

            if (lhsFormat.IsCompressed && rhsFormat.IsCompressed)
            {
                FormatClass lhsClass = GetFormatClass(lhsFormat.Format);
                FormatClass rhsClass = GetFormatClass(rhsFormat.Format);

                bool classMatch = lhsClass == rhsClass;
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"压缩格式类检查: {lhsClass} == {rhsClass} = {classMatch}");
                return classMatch ? TextureViewCompatibility.Full : TextureViewCompatibility.Incompatible;
            }
            else if (lhsFormat.BytesPerPixel == rhsFormat.BytesPerPixel)
            {
                bool bothCompressed = lhs.FormatInfo.IsCompressed == rhs.FormatInfo.IsCompressed;
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"相同BPP检查: BPP={lhsFormat.BytesPerPixel}, 压缩一致={bothCompressed}");
                return bothCompressed
                    ? TextureViewCompatibility.Full
                    : TextureViewCompatibility.CopyOnly;
            }
            else if (IsIncompatibleFormatAliasingAllowed(lhsFormat, rhsFormat))
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    "不兼容格式别名允许, 降级为仅拷贝");
                return TextureViewCompatibility.CopyOnly;
            }

            Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                "视图格式不兼容");
            return TextureViewCompatibility.Incompatible;
        }

        /// <summary>
        /// Checks if it's valid to alias a color format as a depth format.
        /// </summary>
        /// <param name="lhsFormat">Source format to be checked</param>
        /// <param name="rhsFormat">Target format to be checked</param>
        /// <returns>True if it's valid to alias the formats</returns>
        private static bool IsValidColorAsDepthAlias(Format lhsFormat, Format rhsFormat)
        {
            bool valid = (lhsFormat == Format.R32Float && rhsFormat == Format.D32Float) ||
                   (lhsFormat == Format.R16Unorm && rhsFormat == Format.D16Unorm);
                   
            if (valid)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"颜色->深度格式别名有效: {lhsFormat} -> {rhsFormat}");
            }
            
            return valid;
        }

        /// <summary>
        /// Checks if it's valid to alias a depth format as a color format.
        /// </summary>
        /// <param name="lhsFormat">Source format to be checked</param>
        /// <param name="rhsFormat">Target format to be checked</param>
        /// <returns>True if it's valid to alias the formats</returns>
        private static bool IsValidDepthAsColorAlias(Format lhsFormat, Format rhsFormat)
        {
            bool valid = (lhsFormat == Format.D32Float && rhsFormat == Format.R32Float) ||
                   (lhsFormat == Format.D16Unorm && rhsFormat == Format.R16Unorm);
                   
            if (valid)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"深度->颜色格式别名有效: {lhsFormat} -> {rhsFormat}");
            }
            
            return valid;
        }

        /// <summary>
        /// Checks if aliasing of two formats that would normally be considered incompatible be allowed,
        /// using copy dependencies.
        /// </summary>
        /// <param name="lhsFormat">Format information of the first texture</param
        /// <param name="rhsFormat">Format information of the second texture</param>
        /// <returns>True if aliasing should be allowed, false otherwise</returns>
        private static bool IsIncompatibleFormatAliasingAllowed(FormatInfo lhsFormat, FormatInfo rhsFormat)
        {
            // Some games will try to alias textures with incompatible foramts, with different BPP (bytes per pixel).
            // We allow that in some cases as long Width * BPP is equal on both textures.
            // This is very conservative right now as we want to avoid copies as much as possible,
            // so we only consider the formats we have seen being aliased.

            if (rhsFormat.BytesPerPixel < lhsFormat.BytesPerPixel)
            {
                (lhsFormat, rhsFormat) = (rhsFormat, lhsFormat);
            }

            bool allowed = (lhsFormat.Format == Format.R8G8B8A8Unorm && rhsFormat.Format == Format.R32G32B32A32Float) ||
                   (lhsFormat.Format == Format.R8Unorm && rhsFormat.Format == Format.R8G8B8A8Unorm) ||
                   (lhsFormat.Format == Format.R8Unorm && rhsFormat.Format == Format.R32Uint);
                   
            if (allowed)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"不兼容格式别名允许: {lhsFormat.Format}(BPP={lhsFormat.BytesPerPixel}) -> " +
                    $"{rhsFormat.Format}(BPP={rhsFormat.BytesPerPixel})");
            }
            
            return allowed;
        }

        /// <summary>
        /// Check if the target of the first texture view information is compatible with the target of the second texture view information.
        /// This follows the host API target compatibility rules.
        /// </summary>
        /// <param name="lhs">Texture information of the texture view</param
        /// <param name="rhs">Texture information of the texture view</param>
        /// <param name="caps">Host GPU capabilities</param>
        /// <returns>True if the targets are compatible, false otherwise</returns>
        public static TextureViewCompatibility ViewTargetCompatible(TextureInfo lhs, TextureInfo rhs, ref Capabilities caps)
        {
            bool result = false;
            switch (lhs.Target)
            {
                case Target.Texture1D:
                case Target.Texture1DArray:
                    result = rhs.Target == Target.Texture1D ||
                             rhs.Target == Target.Texture1DArray;
                    break;

                case Target.Texture2D:
                    result = rhs.Target == Target.Texture2D ||
                             rhs.Target == Target.Texture2DArray;
                    break;

                case Target.Texture2DArray:
                    result = rhs.Target == Target.Texture2D ||
                             rhs.Target == Target.Texture2DArray;

                    if (rhs.Target == Target.Cubemap || rhs.Target == Target.CubemapArray)
                    {
                        Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                            $"2D数组->立方图映射: 立方图视图支持={caps.SupportsCubemapView}");
                        return caps.SupportsCubemapView ? TextureViewCompatibility.Full : TextureViewCompatibility.CopyOnly;
                    }
                    break;
                case Target.Cubemap:
                case Target.CubemapArray:
                    result = rhs.Target == Target.Cubemap ||
                             rhs.Target == Target.CubemapArray;

                    if (rhs.Target == Target.Texture2D || rhs.Target == Target.Texture2DArray)
                    {
                        Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                            $"立方图->2D映射: 立方图视图支持={caps.SupportsCubemapView}");
                        return caps.SupportsCubemapView ? TextureViewCompatibility.Full : TextureViewCompatibility.CopyOnly;
                    }
                    break;
                case Target.Texture2DMultisample:
                case Target.Texture2DMultisampleArray:
                    if (rhs.Target == Target.Texture2D || rhs.Target == Target.Texture2DArray)
                    {
                        Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                            "多重采样->非多重采样映射, 降级为仅拷贝");
                        return TextureViewCompatibility.CopyOnly;
                    }

                    result = rhs.Target == Target.Texture2DMultisample ||
                             rhs.Target == Target.Texture2DMultisampleArray;
                    break;

                case Target.Texture3D:
                    if (rhs.Target == Target.Texture2D)
                    {
                        Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                            "3D->2D映射, 降级为仅拷贝");
                        return TextureViewCompatibility.CopyOnly;
                    }

                    result = rhs.Target == Target.Texture3D;
                    break;
            }

            Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                $"视图目标兼容性: {lhs.Target} -> {rhs.Target} = {result}");
            return result ? TextureViewCompatibility.Full : TextureViewCompatibility.Incompatible;
        }

        /// <summary>
        /// Checks if the texture shader sampling parameters of two texture informations match.
        /// </summary>
        /// <param name="lhs">Texture information to compare</param>
        /// <param name="rhs">Texture information to compare with</param>
        /// <returns>True if the texture shader sampling parameters matches, false otherwise</returns>
        public static bool SamplerParamsMatches(TextureInfo lhs, TextureInfo rhs)
        {
            bool matches = lhs.DepthStencilMode == rhs.DepthStencilMode &&
                   lhs.SwizzleR == rhs.SwizzleR &&
                   lhs.SwizzleG == rhs.SwizzleG &&
                   lhs.SwizzleB == rhs.SwizzleB &&
                   lhs.SwizzleA == rhs.SwizzleA;
                   
            if (!matches)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"采样器参数不匹配: 深度模式 {lhs.DepthStencilMode}!={rhs.DepthStencilMode} 或 " +
                    $"Swizzle {lhs.SwizzleR}{lhs.SwizzleG}{lhs.SwizzleB}{lhs.SwizzleA} != " +
                    $"{rhs.SwizzleR}{rhs.SwizzleG}{rhs.SwizzleB}{rhs.SwizzleA}");
            }
            
            return matches;
        }

        /// <summary>
        /// Check if the texture target and samples count (for multisampled textures) matches.
        /// </summary>
        /// <param name="first">Texture information to compare with</param>
        /// <param name="rhs">Texture information to compare with</param>
        /// <returns>True if the texture target and samples count matches, false otherwise</returns>
        public static bool TargetAndSamplesCompatible(TextureInfo lhs, TextureInfo rhs)
        {
            bool compatible = lhs.Target == rhs.Target &&
                   lhs.SamplesInX == rhs.SamplesInX &&
                   lhs.SamplesInY == rhs.SamplesInY;
                   
            if (!compatible)
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(LogClass.Gpu, 
                    $"目标和采样数不匹配: 目标 {lhs.Target}!={rhs.Target} 或 " +
                    $"采样数 {lhs.SamplesInX}x{lhs.SamplesInY} != {rhs.SamplesInX}x{rhs.SamplesInY}");
            }
            
            return compatible;
        }

        /// <summary>
        /// Gets the texture format class, for compressed textures, or Unclassified otherwise.
        /// </summary>
        /// <param name="format">The format</param>
        /// <returns>Format class</returns>
        private static FormatClass GetFormatClass(Format format)
        {
            return format switch
            {
                Format.Bc1RgbaSrgb or Format.Bc1RgbaUnorm => FormatClass.Bc1Rgba,
                Format.Bc2Srgb or Format.Bc2Unorm => FormatClass.Bc2,
                Format.Bc3Srgb or Format.Bc3Unorm => FormatClass.Bc3,
                Format.Bc4Snorm or Format.Bc4Unorm => FormatClass.Bc4,
                Format.Bc5Snorm or Format.Bc5Unorm => FormatClass.Bc5,
                Format.Bc6HSfloat or Format.Bc6HUfloat => FormatClass.Bc6,
                Format.Bc7Srgb or Format.Bc7Unorm => FormatClass.Bc7,
                Format.Etc2RgbSrgb or Format.Etc2RgbUnorm => FormatClass.Etc2Rgb,
                Format.Etc2RgbaSrgb or Format.Etc2RgbaUnorm => FormatClass.Etc2Rgba,
                Format.Astc4x4Srgb or Format.Astc4x4Unorm => FormatClass.Astc4x4,
                Format.Astc5x4Srgb or Format.Astc5x4Unorm => FormatClass.Astc5x4,
                Format.Astc5x5Srgb or Format.Astc5x5Unorm => FormatClass.Astc5x5,
                Format.Astc6x5Srgb or Format.Astc6x5Unorm => FormatClass.Astc6x5,
                Format.Astc6x6Srgb or Format.Astc6x6Unorm => FormatClass.Astc6x6,
                Format.Astc8x5Srgb or Format.Astc8x5Unorm => FormatClass.Astc8x5,
                Format.Astc8x6Srgb or Format.Astc8x6Unorm => FormatClass.Astc8x6,
                Format.Astc8x8Srgb or Format.Astc8x8Unorm => FormatClass.Astc8x8,
                Format.Astc10x5Srgb or Format.Astc10x5Unorm => FormatClass.Astc10x5,
                Format.Astc10x6Srgb or Format.Astc10x6Unorm => FormatClass.Astc10x6,
                Format.Astc10x8Srgb or Format.Astc10x8Unorm => FormatClass.Astc10x8,
                Format.Astc10x10Srgb or Format.Astc10x10Unorm => FormatClass.Astc10x10,
                Format.Astc12x10Srgb or Format.Astc12x10Unorm => FormatClass.Astc12x10,
                Format.Astc12x12Srgb or Format.Astc12x12Unorm => FormatClass.Astc12x12,
                _ => FormatClass.Unclassified,
            };
        }
    }
}
