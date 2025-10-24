using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Format = Ryujinx.Graphics.GAL.Format;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class TextureStorage : IDisposable
    {
        private struct TextureSliceInfo
        {
            public int BindCount;
        }

        private const MemoryPropertyFlags DefaultImageMemoryFlags =
            MemoryPropertyFlags.DeviceLocalBit;

        private const ImageUsageFlags DefaultUsageFlags =
            ImageUsageFlags.SampledBit |
            ImageUsageFlags.TransferSrcBit |
            ImageUsageFlags.TransferDstBit;

        public const AccessFlags DefaultAccessMask =
            AccessFlags.ShaderReadBit |
            AccessFlags.ShaderWriteBit |
            AccessFlags.ColorAttachmentReadBit |
            AccessFlags.ColorAttachmentWriteBit |
            AccessFlags.DepthStencilAttachmentReadBit |
            AccessFlags.DepthStencilAttachmentWriteBit |
            AccessFlags.TransferReadBit |
            AccessFlags.TransferWriteBit;

        private readonly VulkanRenderer _gd;

        private readonly Device _device;

        private TextureCreateInfo _info;
        private TextureCreateInfo _originalInfo; // 保存原始格式信息

        public TextureCreateInfo Info => _info;

        public bool Disposed { get; private set; }

        private readonly Image _image;
        private readonly Auto<DisposableImage> _imageAuto;
        private readonly Auto<MemoryAllocation> _allocationAuto;
        private readonly int _depthOrLayers;
        private Auto<MemoryAllocation> _foreignAllocationAuto;

        private Dictionary<Format, TextureStorage> _aliasedStorages;

        private AccessFlags _lastModificationAccess;
        private PipelineStageFlags _lastModificationStage;
        private AccessFlags _lastReadAccess;
        private PipelineStageFlags _lastReadStage;

        private int _viewsCount;
        private readonly ulong _size;

        private int _bindCount;
        private readonly TextureSliceInfo[] _slices;

        public VkFormat VkFormat { get; }

        // 添加回收相关字段
        private long _lastUsedTime;
        private int _useCount;
        private readonly ulong _estimatedMemoryUsage;
        private bool _isCompressed; // 标记是否已被压缩
        private Format _originalFormat; // 原始格式

        public ulong EstimatedMemoryUsage => _estimatedMemoryUsage;
        public bool IsCompressed => _isCompressed;
        public Format OriginalFormat => _originalFormat;

        public unsafe TextureStorage(
            VulkanRenderer gd,
            Device device,
            TextureCreateInfo info,
            Auto<MemoryAllocation> foreignAllocation = null,
            bool tryCompress = true) // 添加压缩尝试参数
        {
            _gd = gd;
            _device = device;
            _info = info;
            _originalInfo = info; // 保存原始信息
            _originalFormat = info.Format;

            // 尝试自动压缩纹理格式以节省内存
            if (tryCompress && ShouldCompressTexture(info))
            {
                var compressedFormat = GetCompressedFormat(info.Format);
                if (compressedFormat != info.Format)
                {
                    _info = NewCreateInfoWith(ref info, compressedFormat, info.BytesPerPixel);
                    _isCompressed = true;
                    Logger.Info?.Print(LogClass.Gpu, $"Texture compressed from {info.Format} to {compressedFormat}");
                }
            }

            var format = _gd.FormatCapabilities.ConvertToVkFormat(_info.Format, true);
            var levels = (uint)_info.Levels;
            var layers = (uint)_info.GetLayers();
            var depth = (uint)(_info.Target == Target.Texture3D ? _info.Depth : 1);

            VkFormat = format;
            _depthOrLayers = _info.GetDepthOrLayers();

            var type = _info.Target.Convert();

            var extent = new Extent3D((uint)_info.Width, (uint)_info.Height, depth);

            var sampleCountFlags = ConvertToSampleCountFlags(gd.Capabilities.SupportedSampleCounts, (uint)_info.Samples);

            // 修改调用处，传递 isNotMsOrSupportsStorage 和 supportsAttachmentFeedbackLoop 参数
            bool isNotMsOrSupportsStorage = !_info.Target.IsMultisample() || gd.Capabilities.SupportsShaderStorageImageMultisample;
            bool supportsAttachmentFeedbackLoop = gd.Capabilities.SupportsAttachmentFeedbackLoop;
            var usage = GetImageUsage(_info.Format, isNotMsOrSupportsStorage, supportsAttachmentFeedbackLoop);

            var flags = ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit;

            // This flag causes mipmapped texture arrays to break on AMD GCN, so for that copy dependencies are forced for aliasing as cube.
            bool isCube = _info.Target == Target.Cubemap || _info.Target == Target.CubemapArray;
            bool cubeCompatible = gd.IsAmdGcn ? isCube : (_info.Width == _info.Height && layers >= 6);

            if (type == ImageType.Type2D && cubeCompatible)
            {
                flags |= ImageCreateFlags.CreateCubeCompatibleBit;
            }

            if (type == ImageType.Type3D && !gd.Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.No3DImageView))
            {
                flags |= ImageCreateFlags.Create2DArrayCompatibleBit;
            }

            var imageCreateInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = type,
                Format = format,
                Extent = extent,
                MipLevels = levels,
                ArrayLayers = layers,
                Samples = sampleCountFlags,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
                Flags = flags,
            };

            gd.Api.CreateImage(device, in imageCreateInfo, null, out _image).ThrowOnError();

            if (foreignAllocation == null)
            {
                gd.Api.GetImageMemoryRequirements(device, _image, out var requirements);
                var allocation = gd.MemoryAllocator.AllocateDeviceMemory(requirements, DefaultImageMemoryFlags);

                if (allocation.Memory.Handle == 0UL)
                {
                    gd.Api.DestroyImage(device, _image, null);
                    throw new Exception("Image initialization failed.");
                }

                _size = requirements.Size;

                gd.Api.BindImageMemory(device, _image, allocation.Memory, allocation.Offset).ThrowOnError();

                _allocationAuto = new Auto<MemoryAllocation>(allocation);
                _imageAuto = new Auto<DisposableImage>(new DisposableImage(_gd.Api, device, _image), null, _allocationAuto);

                InitialTransition(ImageLayout.Undefined, ImageLayout.General);
            }
            else
            {
                _foreignAllocationAuto = foreignAllocation;
                foreignAllocation.IncrementReferenceCount();
                var allocation = foreignAllocation.GetUnsafe();

                gd.Api.BindImageMemory(device, _image, allocation.Memory, allocation.Offset).ThrowOnError();

                _imageAuto = new Auto<DisposableImage>(new DisposableImage(_gd.Api, device, _image));

                InitialTransition(ImageLayout.Preinitialized, ImageLayout.General);
            }

            _slices = new TextureSliceInfo[levels * _depthOrLayers];

            // 估算内存使用量
            _estimatedMemoryUsage = CalculateEstimatedMemoryUsage(_info);
            
            // 初始化使用时间
            UpdateLastUsedTime();
        }

        /// <summary>
        /// 检查纹理是否应该被压缩
        /// </summary>
        private bool ShouldCompressTexture(TextureCreateInfo info)
        {
            // 不压缩小纹理
            if (info.Width <= 64 && info.Height <= 64)
                return false;
                
            // 不压缩深度/模板格式
            if (info.Format.IsDepthOrStencil())
                return false;
                
            // 不压缩已经压缩的格式
            if (info.Format.IsCompressed())
                return false;
                
            return true;
        }

        /// <summary>
        /// 获取压缩后的格式
        /// </summary>
        private Format GetCompressedFormat(Format originalFormat)
        {
            return originalFormat switch
            {
                // RGBA -> 压缩格式
                Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm => 
                    _gd.Capabilities.SupportsBc123Compression ? Format.Bc3Unorm : originalFormat,
                    
                // RGB -> 压缩格式  
                Format.R8G8B8Unorm => 
                    _gd.Capabilities.SupportsBc123Compression ? Format.Bc1RgbaUnorm : originalFormat,
                    
                // 高精度格式 -> 中等精度
                Format.R16G16B16A16Unorm or Format.R16G16B16A16Float =>
                    Format.R8G8B8A8Unorm,
                    
                // 单通道 -> 保持原样或使用BC4
                Format.R8Unorm => 
                    _gd.Capabilities.SupportsBc45Compression ? Format.Bc4Unorm : originalFormat,
                    
                // 双通道 -> 保持原样或使用BC5  
                Format.R8G8Unorm =>
                    _gd.Capabilities.SupportsBc45Compression ? Format.Bc5Unorm : originalFormat,
                    
                _ => originalFormat
            };
        }

        /// <summary>
        /// 估算纹理内存使用量
        /// </summary>
        private ulong CalculateEstimatedMemoryUsage(TextureCreateInfo info)
        {
            ulong size = 0;
            int width = info.Width;
            int height = info.Height;
            int depth = info.Depth;
            
            for (int level = 0; level < info.Levels; level++)
            {
                int mipSize = info.GetMipSize(level);
                size += (ulong)mipSize;
                
                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);
                
                if (info.Target == Target.Texture3D)
                {
                    depth = Math.Max(1, depth >> 1);
                }
            }
            
            // 考虑多重采样
            if (info.Samples > 1)
            {
                size *= (ulong)info.Samples;
            }
            
            return size;
        }

        /// <summary>
        /// 更新最后使用时间
        /// </summary>
        public void UpdateLastUsedTime()
        {
            _lastUsedTime = Stopwatch.GetTimestamp();
            _useCount++;
        }

        /// <summary>
        /// 检查纹理是否可以被回收
        /// </summary>
        public bool CanBeReclaimed(long currentTime, bool aggressive)
        {
            if (Disposed || _bindCount > 0 || _viewsCount > 0)
            {
                return false;
            }
            
            long timeSinceLastUse = currentTime - _lastUsedTime;
            long timeoutTicks = aggressive ? 
                (long)(Stopwatch.Frequency * 15) :  // 激进模式：15秒未使用
                (long)(Stopwatch.Frequency * 60);   // 普通模式：60秒未使用
                
            return timeSinceLastUse > timeoutTicks;
        }

        /// <summary>
        /// 获取内存节省比例
        /// </summary>
        public float GetMemorySavingRatio()
        {
            if (!_isCompressed)
                return 1.0f;
                
            ulong originalSize = CalculateEstimatedMemoryUsage(_originalInfo);
            ulong currentSize = _estimatedMemoryUsage;
            
            return originalSize > 0 ? (float)currentSize / originalSize : 1.0f;
        }

        public TextureStorage CreateAliasedColorForDepthStorageUnsafe(Format format)
        {
            var colorFormat = format switch
            {
                Format.S8Uint => Format.R8Unorm,
                Format.D16Unorm => Format.R16Unorm,
                Format.D24UnormS8Uint or Format.S8UintD24Unorm or Format.X8UintD24Unorm => Format.R8G8B8A8Unorm,
                Format.D32Float => Format.R32Float,
                Format.D32FloatS8Uint => Format.R32G32Float,
                _ => throw new ArgumentException($"\"{format}\" is not a supported depth or stencil format."),
            };

            return CreateAliasedStorageUnsafe(colorFormat);
        }

        public TextureStorage CreateAliasedStorageUnsafe(Format format)
        {
            if (_aliasedStorages == null || !_aliasedStorages.TryGetValue(format, out var storage))
            {
                _aliasedStorages ??= new Dictionary<Format, TextureStorage>();

                var info = NewCreateInfoWith(ref _info, format, _info.BytesPerPixel);

                storage = new TextureStorage(_gd, _device, info, _allocationAuto);

                _aliasedStorages.Add(format, storage);
            }

            return storage;
        }

        public static TextureCreateInfo NewCreateInfoWith(ref TextureCreateInfo info, Format format, int bytesPerPixel)
        {
            return NewCreateInfoWith(ref info, format, bytesPerPixel, info.Width, info.Height);
        }

        public static TextureCreateInfo NewCreateInfoWith(
            ref TextureCreateInfo info,
            Format format,
            int bytesPerPixel,
            int width,
            int height)
        {
            return new TextureCreateInfo(
                width,
                height,
                info.Depth,
                info.Levels,
                info.Samples,
                info.BlockWidth,
                info.BlockHeight,
                bytesPerPixel,
                format,
                info.DepthStencilMode,
                info.Target,
                info.SwizzleR,
                info.SwizzleG,
                info.SwizzleB,
                info.SwizzleA);
        }

        public Auto<DisposableImage> GetImage()
        {
            return _imageAuto;
        }

        public Image GetImageForViewCreation()
        {
            return _image;
        }

        public bool HasCommandBufferDependency(CommandBufferScoped cbs)
        {
            if (_foreignAllocationAuto != null)
            {
                return _foreignAllocationAuto.HasCommandBufferDependency(cbs);
            }
            else if (_allocationAuto != null)
            {
                return _allocationAuto.HasCommandBufferDependency(cbs);
            }

            return false;
        }

        private unsafe void InitialTransition(ImageLayout srcLayout, ImageLayout dstLayout)
        {
            CommandBufferScoped cbs;
            bool useTempCbs = !_gd.CommandBufferPool.OwnedByCurrentThread;

            if (useTempCbs)
            {
                cbs = _gd.BackgroundResources.Get().GetPool().Rent();
            }
            else
            {
                if (_gd.PipelineInternal != null)
                {
                    cbs = _gd.PipelineInternal.GetPreloadCommandBuffer();
                }
                else
                {
                    cbs = _gd.CommandBufferPool.Rent();
                    useTempCbs = true;
                }
            }

            var aspectFlags = _info.Format.ConvertAspectFlags();

            var subresourceRange = new ImageSubresourceRange(aspectFlags, 0, (uint)_info.Levels, 0, (uint)_info.GetLayers());

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = DefaultAccessMask,
                OldLayout = srcLayout,
                NewLayout = dstLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _imageAuto.Get(cbs).Value,
                SubresourceRange = subresourceRange,
            };

            _gd.Api.CmdPipelineBarrier(
                cbs.CommandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                in barrier);

            if (useTempCbs)
            {
                cbs.Dispose();
            }
        }

        public static ImageUsageFlags GetImageUsage(Format format, bool isNotMsOrSupportsStorage, bool supportsAttachmentFeedbackLoop)
        {
            var usage = DefaultUsageFlags;

            if (format.IsDepthOrStencil())
            {
                usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            }
            else if (format.IsRtColorCompatible())
            {
                usage |= ImageUsageFlags.ColorAttachmentBit;
            }

            if (format.IsImageCompatible() && isNotMsOrSupportsStorage)
            {
                usage |= ImageUsageFlags.StorageBit;
            }

            if (supportsAttachmentFeedbackLoop &&
                (usage & (ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.ColorAttachmentBit)) != 0)
            {
                usage |= ImageUsageFlags.AttachmentFeedbackLoopBitExt;
            }

            return usage;
        }

        public static SampleCountFlags ConvertToSampleCountFlags(SampleCountFlags supportedSampleCounts, uint samples)
        {
            if (samples == 0 || samples > (uint)SampleCountFlags.Count64Bit)
            {
                return SampleCountFlags.Count1Bit;
            }

            // Round up to the nearest power of two.
            SampleCountFlags converted = (SampleCountFlags)(1u << (31 - BitOperations.LeadingZeroCount(samples)));

            // Pick nearest sample count that the host actually supports.
            while (converted != SampleCountFlags.Count1Bit && (converted & supportedSampleCounts) == 0)
            {
                converted = (SampleCountFlags)((uint)converted >> 1);
            }

            return converted;
        }

        public TextureView CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            return new TextureView(_gd, _device, info, this, firstLayer, firstLevel);
        }

        public void CopyFromOrToBuffer(
            CommandBuffer commandBuffer,
            VkBuffer buffer,
            Image image,
            int size,
            bool to,
            int x,
            int y,
            int dstLayer,
            int dstLevel,
            int dstLayers,
            int dstLevels,
            bool singleSlice,
            ImageAspectFlags aspectFlags,
            bool forFlush)
        {
            bool is3D = Info.Target == Target.Texture3D;
            int width = Info.Width;
            int height = Info.Height;
            int depth = is3D && !singleSlice ? Info.Depth : 1;
            int layer = is3D ? 0 : dstLayer;
            int layers = dstLayers;
            int levels = dstLevels;

            int offset = 0;

            for (int level = 0; level < levels; level++)
            {
                int mipSize = Info.GetMipSize(level);

                if (forFlush)
                {
                    mipSize = GetBufferDataLength(mipSize);
                }

                int endOffset = offset + mipSize;

                if ((uint)endOffset > (uint)size)
                {
                    break;
                }

                int rowLength = (Info.GetMipStride(level) / Info.BytesPerPixel) * Info.BlockWidth;

                var sl = new ImageSubresourceLayers(
                    aspectFlags,
                    (uint)(dstLevel + level),
                    (uint)layer,
                    (uint)layers);

                var extent = new Extent3D((uint)width, (uint)height, (uint)depth);

                int z = is3D ? dstLayer : 0;

                var region = new BufferImageCopy(
                    (ulong)offset,
                    (uint)BitUtils.AlignUp(rowLength, Info.BlockWidth),
                    (uint)BitUtils.AlignUp(height, Info.BlockHeight),
                    sl,
                    new Offset3D(x, y, z),
                    extent);

                if (to)
                {
                    _gd.Api.CmdCopyImageToBuffer(commandBuffer, image, ImageLayout.General, buffer, 1, in region);
                }
                else
                {
                    _gd.Api.CmdCopyBufferToImage(commandBuffer, buffer, image, ImageLayout.General, 1, in region);
                }

                offset += mipSize;

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);

                if (Info.Target == Target.Texture3D)
                {
                    depth = Math.Max(1, depth >> 1);
                }
            }
        }

        private int GetBufferDataLength(int length)
        {
            if (NeedsD24S8Conversion())
            {
                return length * 2;
            }

            return length;
        }

        private bool NeedsD24S8Conversion()
        {
            return FormatCapabilities.IsD24S8(Info.Format) && VkFormat == VkFormat.D32SfloatS8Uint;
        }

        public void AddStoreOpUsage(bool depthStencil)
        {
            _lastModificationStage = depthStencil ?
                PipelineStageFlags.LateFragmentTestsBit :
                PipelineStageFlags.ColorAttachmentOutputBit;

            _lastModificationAccess = depthStencil ?
                AccessFlags.DepthStencilAttachmentWriteBit :
                AccessFlags.ColorAttachmentWriteBit;
        }

        public void QueueLoadOpBarrier(CommandBufferScoped cbs, bool depthStencil)
        {
            PipelineStageFlags srcStageFlags = _lastReadStage | _lastModificationStage;
            PipelineStageFlags dstStageFlags = depthStencil ?
                PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit :
                PipelineStageFlags.ColorAttachmentOutputBit;

            AccessFlags srcAccessFlags = _lastModificationAccess | _lastReadAccess;
            AccessFlags dstAccessFlags = depthStencil ?
                AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit :
                AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit;

            if (srcAccessFlags != AccessFlags.None)
            {
                ImageAspectFlags aspectFlags = Info.Format.ConvertAspectFlags();
                ImageMemoryBarrier barrier = TextureView.GetImageBarrier(
                    _imageAuto.Get(cbs).Value,
                    srcAccessFlags,
                    dstAccessFlags,
                    aspectFlags,
                    0,
                    0,
                    _info.GetLayers(),
                    _info.Levels);

                _gd.Barriers.QueueBarrier(barrier, this, srcStageFlags, dstStageFlags);

                _lastReadStage = PipelineStageFlags.None;
                _lastReadAccess = AccessFlags.None;
            }

            _lastModificationStage = depthStencil ?
                PipelineStageFlags.LateFragmentTestsBit :
                PipelineStageFlags.ColorAttachmentOutputBit;

            _lastModificationAccess = depthStencil ?
                AccessFlags.DepthStencilAttachmentWriteBit :
                AccessFlags.ColorAttachmentWriteBit;
        }

        public void QueueWriteToReadBarrier(CommandBufferScoped cbs, AccessFlags dstAccessFlags, PipelineStageFlags dstStageFlags)
        {
            _lastReadAccess |= dstAccessFlags;
            _lastReadStage |= dstStageFlags;

            if (_lastModificationAccess != AccessFlags.None)
            {
                ImageAspectFlags aspectFlags = Info.Format.ConvertAspectFlags();
                ImageMemoryBarrier barrier = TextureView.GetImageBarrier(
                    _imageAuto.Get(cbs).Value,
                    _lastModificationAccess,
                    dstAccessFlags,
                    aspectFlags,
                    0,
                    0,
                    _info.GetLayers(),
                    _info.Levels);

                _gd.Barriers.QueueBarrier(barrier, this, _lastModificationStage, dstStageFlags);

                _lastModificationAccess = AccessFlags.None;
            }
        }

        public void AddBinding(TextureView view)
        {
            UpdateLastUsedTime();
            
            // Assumes a view only has a first level.

            int index = view.FirstLevel * _depthOrLayers + view.FirstLayer;
            int layers = view.Layers;

            for (int i = 0; i < layers; i++)
            {
                ref TextureSliceInfo info = ref _slices[index++];

                info.BindCount++;
            }

            _bindCount++;
        }

        public void ClearBindings()
        {
            if (_bindCount != 0)
            {
                Array.Clear(_slices, 0, _slices.Length);

                _bindCount = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBound(TextureView view)
        {
            if (_bindCount != 0)
            {
                int index = view.FirstLevel * _depthOrLayers + view.FirstLayer;
                int layers = view.Layers;

                for (int i = 0; i < layers; i++)
                {
                    ref TextureSliceInfo info = ref _slices[index++];

                    if (info.BindCount != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void IncrementViewsCount()
        {
            _viewsCount++;
        }

        public void DecrementViewsCount()
        {
            if (--_viewsCount == 0)
            {
                _gd.PipelineInternal?.FlushCommandsIfWeightExceeding(_imageAuto, _size);

                Dispose();
            }
            else if (_viewsCount < 0)
            {
                _viewsCount = 0;
            }
        }

        public void Dispose()
        {
            if (Disposed)
                return;
                
            Disposed = true;

            if (_aliasedStorages != null)
            {
                foreach (var storage in _aliasedStorages.Values)
                {
                    storage.Dispose();
                }

                _aliasedStorages.Clear();
            }

            _imageAuto.Dispose();
            _allocationAuto?.Dispose();
            _foreignAllocationAuto?.DecrementReferenceCount();
            _foreignAllocationAuto = null;
        }
    }
}