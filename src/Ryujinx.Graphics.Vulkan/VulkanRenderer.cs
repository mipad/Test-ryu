using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Ryujinx.Graphics.Vulkan.MoltenVK;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Format = Ryujinx.Graphics.GAL.Format;
using PrimitiveTopology = Ryujinx.Graphics.GAL.PrimitiveTopology;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;
using VkCompareOp = Silk.NET.Vulkan.CompareOp; // 解决 CompareOp 歧义
using VkStencilOp = Silk.NET.Vulkan.StencilOp; // 解决 StencilOp 歧义
using GALCompareOp = Ryujinx.Graphics.GAL.CompareOp; // GAL CompareOp 别名
using GALStencilOp = Ryujinx.Graphics.GAL.StencilOp; // GAL StencilOp 别名

namespace Ryujinx.Graphics.Vulkan
{
    // 新增：缺失的类型定义 - 在 Ryujinx.Graphics.GAL 中不存在，需要自定义
    internal enum CullMode
    {
        None = 0,
        Front = 1,
        Back = 2,
        FrontAndBack = 3
    }

    internal struct StencilOpState
    {
        public GALStencilOp Fail { get; set; }
        public GALStencilOp Pass { get; set; }
        public GALStencilOp DepthFail { get; set; }
        public GALCompareOp Compare { get; set; }
        public uint CompareMask { get; set; }
        public uint WriteMask { get; set; }
        public uint Reference { get; set; }

        public override bool Equals(object obj)
        {
            return obj is StencilOpState other &&
                   Fail == other.Fail &&
                   Pass == other.Pass &&
                   DepthFail == other.DepthFail &&
                   Compare == other.Compare &&
                   CompareMask == other.CompareMask &&
                   WriteMask == other.WriteMask &&
                   Reference == other.Reference;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Fail, Pass, DepthFail, Compare, CompareMask, WriteMask, Reference);
        }
    }

    internal struct TransformFeedbackState
    {
        public bool Enabled { get; set; }
        public TransformFeedbackBufferState[] Buffers { get; set; }
    }

    internal struct TransformFeedbackBufferState
    {
        public BufferHandle Buffer { get; set; }
        public int Offset { get; set; }
        public int Size { get; set; }
    }

    internal struct ConditionalRenderingCondition
    {
        public BufferHandle Buffer { get; set; }
        public int Offset { get; set; }
        public bool IsEqual { get; set; }
    }

    internal struct ImageCopyInfo
    {
        public ITexture Source { get; set; }
        public ITexture Destination { get; set; }
        public int SrcX { get; set; }
        public int SrcY { get; set; }
        public int DstX { get; set; }
        public int DstY { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    // 新增：DMA 加速器类
    internal class DmaAccelerator
    {
        private readonly VulkanRenderer _renderer;
        private readonly BufferManager _bufferManager;

        public DmaAccelerator(VulkanRenderer renderer, BufferManager bufferManager)
        {
            _renderer = renderer;
            _bufferManager = bufferManager;
        }

        public bool BufferClear(ulong address, ulong size, uint value)
        {
            // 实现缓冲区清除加速逻辑
            // 这里可以使用计算着色器或专用传输操作
            return false; // 暂时返回 false，需要实际实现
        }

        public bool BufferCopy(ulong srcAddress, ulong dstAddress, ulong size)
        {
            // 实现缓冲区复制加速逻辑
            return false; // 暂时返回 false，需要实际实现
        }

        public bool ImageToBuffer(ImageCopyInfo copyInfo)
        {
            // 实现图像到缓冲区的 DMA 传输
            return false; // 暂时返回 false，需要实际实现
        }

        public bool BufferToImage(ImageCopyInfo copyInfo)
        {
            // 实现缓冲区到图像的 DMA 传输
            return false; // 暂时返回 false，需要实际实现
        }

        public void Dispose()
        {
            // 清理资源
        }
    }

    // 新增：状态结构定义 - 使用自定义类型
    internal struct State
    {
        public bool CullEnable { get; set; }
        public CullMode CullMode { get; set; }
        public bool DepthTestEnable { get; set; }
        public GALCompareOp DepthCompareOp { get; set; } // 使用 GALCompareOp 别名
        public bool StencilTestEnable { get; set; }
        public StencilOpState StencilOps { get; set; }
        // 可以添加更多状态字段
    }

    // 扩展 CullMode 以添加 Convert 方法
    internal static class CullModeExtensions
    {
        public static CullModeFlags Convert(this CullMode mode)
        {
            return mode switch
            {
                CullMode.Front => CullModeFlags.FrontBit,
                CullMode.Back => CullModeFlags.BackBit,
                CullMode.FrontAndBack => CullModeFlags.FrontAndBack,
                _ => CullModeFlags.None
            };
        }
    }

    // 扩展 CompareOp 以添加 Convert 方法 - 使用 GALCompareOp
    internal static class CompareOpExtensions
    {
        public static VkCompareOp Convert(this GALCompareOp op)
        {
            return op switch
            {
                GALCompareOp.Never => VkCompareOp.Never,
                GALCompareOp.Less => VkCompareOp.Less,
                GALCompareOp.Equal => VkCompareOp.Equal,
                GALCompareOp.LessOrEqual => VkCompareOp.LessOrEqual,
                GALCompareOp.Greater => VkCompareOp.Greater,
                GALCompareOp.NotEqual => VkCompareOp.NotEqual,
                GALCompareOp.GreaterOrEqual => VkCompareOp.GreaterOrEqual,
                GALCompareOp.Always => VkCompareOp.Always,
                _ => VkCompareOp.Never
            };
        }
    }

    // 扩展 StencilOp 以添加 Convert 方法 - 使用 GALStencilOp
    internal static class StencilOpExtensions
    {
        public static VkStencilOp Convert(this GALStencilOp op)
        {
            return op switch
            {
                GALStencilOp.Keep => VkStencilOp.Keep,
                GALStencilOp.Zero => VkStencilOp.Zero,
                GALStencilOp.Replace => VkStencilOp.Replace,
                GALStencilOp.IncrementAndClamp => VkStencilOp.IncrementAndClamp,
                GALStencilOp.DecrementAndClamp => VkStencilOp.DecrementAndClamp,
                GALStencilOp.Invert => VkStencilOp.Invert,
                GALStencilOp.IncrementAndWrap => VkStencilOp.IncrementAndWrap,
                GALStencilOp.DecrementAndWrap => VkStencilOp.DecrementAndWrap,
                _ => VkStencilOp.Keep
            };
        }
    }

    unsafe public sealed class VulkanRenderer : IRenderer
    {
        private VulkanInstance _instance;
        private SurfaceKHR _surface;
        private VulkanPhysicalDevice _physicalDevice;
        private Device _device;
        private WindowBase _window;
        private CommandBufferPool _computeCommandPool;
        private bool _initialized;

        // JNI/Lifecycle-Flag
        internal volatile bool PresentAllowed = true;

        public uint ProgramCount { get; set; } = 0;

        // 新增：绘制计数器，借鉴 yuzu 的智能批处理机制
        private int _drawCounter = 0;
        private const int DRAWS_TO_DISPATCH = 2048; // 可调参数，针对不同平台优化
        private const int DRAWS_TO_FLUSH = 4096;

        internal KhrTimelineSemaphore TimelineSemaphoreApi { get; private set; }
        internal FormatCapabilities FormatCapabilities { get; private set; }
        internal HardwareCapabilities Capabilities;

        internal Vk Api { get; private set; }
        internal KhrSurface SurfaceApi { get; private set; }
        internal KhrSwapchain SwapchainApi { get; private set; }
        internal ExtConditionalRendering ConditionalRenderingApi { get; private set; }
        internal ExtExtendedDynamicState ExtendedDynamicStateApi { get; private set; }
        internal KhrPushDescriptor PushDescriptorApi { get; private set; }
        internal ExtTransformFeedback TransformFeedbackApi { get; private set; }
        internal KhrDrawIndirectCount DrawIndirectCountApi { get; private set; }
        internal ExtAttachmentFeedbackLoopDynamicState DynamicFeedbackLoopApi { get; private set; }
        internal ExtExtendedDynamicState3 ExtendedDynamicState3Api { get; private set; } // 新增：动态状态3扩展
        
        internal bool SupportsFragmentDensityMap { get; private set; }
        internal bool SupportsFragmentDensityMap2 { get; private set; }

        internal uint QueueFamilyIndex { get; private set; }
        internal Queue Queue { get; private set; }
        internal Queue BackgroundQueue { get; private set; }
        internal object BackgroundQueueLock { get; private set; }
        internal object QueueLock { get; private set; }

        // 新增：动态状态跟踪器，借鉴 yuzu 的细粒度状态管理
        private DynamicStateTracker _dynamicStateTracker;

        // NEU: SurfaceLock, um Create/Destroy/Queries zu serialisieren
        internal object SurfaceLock { get; private set; }

        internal MemoryAllocator MemoryAllocator { get; private set; }
        internal HostMemoryAllocator HostMemoryAllocator { get; private set; }
        internal CommandBufferPool CommandBufferPool { get; private set; }
        internal PipelineLayoutCache PipelineLayoutCache { get; private set; }
        internal BackgroundResources BackgroundResources { get; private set; }
        internal Action<Action> InterruptAction { get; private set; }
        internal SyncManager SyncManager { get; private set; }

        internal BufferManager BufferManager { get; private set; }

        // 新增：DMA 加速器，借鉴 yuzu 的 AccelerateDMA
        internal DmaAccelerator DmaAccelerator { get; private set; }

        internal HashSet<ShaderCollection> Shaders { get; }
        internal HashSet<ITexture> Textures { get; }
        internal HashSet<SamplerHolder> Samplers { get; }

        private VulkanDebugMessenger _debugMessenger;
        private Counters _counters;

        private PipelineFull _pipeline;

        internal HelperShader HelperShader { get; private set; }
        internal PipelineFull PipelineInternal => _pipeline;

        internal BarrierBatch Barriers { get; private set; }

        public IPipeline Pipeline => _pipeline;

        public IWindow Window => _window;

        public SurfaceTransformFlagsKHR CurrentTransform => _window.CurrentTransform;

        public Device Device => _device;
        
        private readonly Func<Instance, Vk, SurfaceKHR> _getSurface;
        private readonly Func<string[]> _getRequiredExtensions;
        private readonly string _preferredGpuId;

        private int[] _pdReservedBindings;
        private readonly static int[] _pdReservedBindingsNvn = { 3, 18, 21, 36, 30 };
        private readonly static int[] _pdReservedBindingsOgl = { 17, 18, 34, 35, 36 };

        internal Vendor Vendor { get; private set; }
        internal bool IsAmdWindows { get; private set; }
        internal bool IsIntelWindows { get; private set; }
        internal bool IsAmdGcn { get; private set; }
        internal bool IsAmdRdna3 { get; private set; }
        internal bool IsNvidiaPreTuring { get; private set; }
        internal bool IsIntelArc { get; set; }
        internal bool IsQualcommProprietary { get; private set; }
        internal bool IsMoltenVk { get; private set; }
        internal bool IsTBDR { get; private set; }
        internal bool IsSharedMemory { get; private set; }

        public string GpuVendor { get; private set; }
        public string GpuDriver { get; private set; }
        public string GpuRenderer { get; private set; }
        public string GpuVersion { get; private set; }

        public bool PreferThreading => true;

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        public VulkanRenderer(Vk api, Func<Instance, Vk, SurfaceKHR> surfaceFunc, Func<string[]> requiredExtensionsFunc, string preferredGpuId)
        {
            _getSurface = surfaceFunc;
            _getRequiredExtensions = requiredExtensionsFunc;
            _preferredGpuId = preferredGpuId;
            Api = api;
            Shaders = new HashSet<ShaderCollection>();
            Textures = new HashSet<ITexture>();
            Samplers = new HashSet<SamplerHolder>();

            // 新增：初始化动态状态跟踪器
            _dynamicStateTracker = new DynamicStateTracker();

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                MVKInitialization.Initialize();
                IsMoltenVk = true;
            }
        }

        // 修复：智能命令缓冲区刷新机制
        internal void FlushWork()
        {
            // 只在特定绘制计数时检查刷新
            if ((++_drawCounter & 0x7) != 0x7) return;

            if (_drawCounter >= DRAWS_TO_FLUSH)
            {
                FlushAllCommands();
                _drawCounter = 0;
            }
            else if (_drawCounter >= DRAWS_TO_DISPATCH)
            {
                // 只提交到工作线程，不立即刷新
                CommandBufferPool?.SubmitWork();
            }
        }

        // 新增：注册绘制调用
        internal void RegisterDraw()
        {
            FlushWork();
        }

        // 修复：动态状态更新方法 - 移除不存在的参数
        internal void UpdateDynamicStates()
        {
            // 这个方法需要根据实际的状态参数来实现
            // 暂时留空，等待实际的状态参数
        }

        // 修复：变换反馈处理方法 - 使用正确的参数
        internal void HandleTransformFeedback(bool enabled)
        {
            if (TransformFeedbackApi == null || !Capabilities.SupportsTransformFeedback) return;

            // 启用/禁用变换反馈计数器
            // 注意：Counters 类中没有 EnableTransformFeedback 方法，暂时注释掉
            // _counters?.EnableTransformFeedback(enabled);

            if (enabled)
            {
                // 绑定变换反馈缓冲区
                // 这里需要实际的缓冲区绑定逻辑
            }
        }

        // 修复：条件渲染加速方法 - 使用正确的参数
        public bool AccelerateConditionalRendering(BufferHandle buffer, int offset, bool isEqual)
        {
            if (ConditionalRenderingApi == null) return false;

            // 注意：BufferManager 中没有 FlushCaching 方法，暂时注释掉
            // BufferManager.FlushCaching();
            
            // 注意：Counters 类中没有 AccelerateHostConditionalRendering 方法，暂时返回 false
            return false; // _counters?.AccelerateHostConditionalRendering(buffer, offset, isEqual) ?? false;
        }

        // 修复：DMA 加速方法 - 使用正确的参数
        public bool AccelerateDmaBufferClear(BufferHandle buffer, int offset, int size, uint value)
        {
            return DmaAccelerator?.BufferClear((ulong)offset, (ulong)size, value) ?? false;
        }

        public bool AccelerateDmaBufferCopy(BufferHandle srcBuffer, int srcOffset, BufferHandle dstBuffer, int dstOffset, int size)
        {
            return DmaAccelerator?.BufferCopy((ulong)srcOffset, (ulong)dstOffset, (ulong)size) ?? false;
        }

        public bool AccelerateDmaImageToBuffer(ITexture source, ITexture destination, int srcX, int srcY, int dstX, int dstY, int width, int height)
        {
            var copyInfo = new ImageCopyInfo
            {
                Source = source,
                Destination = destination,
                SrcX = srcX,
                SrcY = srcY,
                DstX = dstX,
                DstY = dstY,
                Width = width,
                Height = height
            };
            return DmaAccelerator?.ImageToBuffer(copyInfo) ?? false;
        }

        public bool AccelerateDmaBufferToImage(ITexture source, ITexture destination, int srcX, int srcY, int dstX, int dstY, int width, int height)
        {
            var copyInfo = new ImageCopyInfo
            {
                Source = source,
                Destination = destination,
                SrcX = srcX,
                SrcY = srcY,
                DstX = dstX,
                DstY = dstY,
                Width = width,
                Height = height
            };
            return DmaAccelerator?.BufferToImage(copyInfo) ?? false;
        }

        private unsafe void LoadFeatures(uint maxQueueCount, uint queueFamilyIndex)
        {
            FormatCapabilities = new FormatCapabilities(Api, _physicalDevice.PhysicalDevice);

            uint computeFamilyIndex = FindComputeQueueFamily();

            if (computeFamilyIndex != uint.MaxValue && computeFamilyIndex != queueFamilyIndex)
            {
                Queue computeQueue;
                Api.GetDeviceQueue(_device, computeFamilyIndex, 0, &computeQueue);

                _computeCommandPool = new CommandBufferPool(
                    Api,
                    _device,
                    computeQueue,
                    new object(),
                    computeFamilyIndex,
                    IsQualcommProprietary,
                    false);
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtConditionalRendering conditionalRenderingApi))
            {
                ConditionalRenderingApi = conditionalRenderingApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrTimelineSemaphore timelineSemaphoreApi))
            {
                TimelineSemaphoreApi = timelineSemaphoreApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExtendedDynamicState extendedDynamicStateApi))
            {
                ExtendedDynamicStateApi = extendedDynamicStateApi;
            }

            // 新增：扩展动态状态3支持
            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExtendedDynamicState3 extendedDynamicState3Api))
            {
                ExtendedDynamicState3Api = extendedDynamicState3Api;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrPushDescriptor pushDescriptorApi))
            {
                PushDescriptorApi = pushDescriptorApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtTransformFeedback transformFeedbackApi))
            {
                TransformFeedbackApi = transformFeedbackApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrDrawIndirectCount drawIndirectCountApi))
            {
                DrawIndirectCountApi = drawIndirectCountApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtAttachmentFeedbackLoopDynamicState dynamicFeedbackLoopApi))
            {
                DynamicFeedbackLoopApi = dynamicFeedbackLoopApi;
            }

            SupportsFragmentDensityMap = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_density_map");
            SupportsFragmentDensityMap2 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_density_map2");

            if (maxQueueCount >= 2)
            {
                Api.GetDeviceQueue(_device, queueFamilyIndex, 1, out var backgroundQueue);
                BackgroundQueue = backgroundQueue;
                BackgroundQueueLock = new object();
            }

            PhysicalDeviceProperties2 properties2 = new()
            {
                SType = StructureType.PhysicalDeviceProperties2,
            };

            PhysicalDeviceSubgroupProperties propertiesSubgroup = new()
            {
                SType = StructureType.PhysicalDeviceSubgroupProperties,
                PNext = properties2.PNext,
            };

            properties2.PNext = &propertiesSubgroup;

            PhysicalDeviceBlendOperationAdvancedPropertiesEXT propertiesBlendOperationAdvanced = new()
            {
                SType = StructureType.PhysicalDeviceBlendOperationAdvancedPropertiesExt,
            };

            bool supportsBlendOperationAdvanced = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_blend_operation_advanced");

            if (supportsBlendOperationAdvanced)
            {
                propertiesBlendOperationAdvanced.PNext = properties2.PNext;
                properties2.PNext = &propertiesBlendOperationAdvanced;
            }

            bool supportsTransformFeedback = _physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName);

            PhysicalDeviceTransformFeedbackPropertiesEXT propertiesTransformFeedback = new()
            {
                SType = StructureType.PhysicalDeviceTransformFeedbackPropertiesExt,
            };

            if (supportsTransformFeedback)
            {
                propertiesTransformFeedback.PNext = properties2.PNext;
                properties2.PNext = &propertiesTransformFeedback;
            }

            PhysicalDevicePortabilitySubsetPropertiesKHR propertiesPortabilitySubset = new()
            {
                SType = StructureType.PhysicalDevicePortabilitySubsetPropertiesKhr,
            };

            bool supportsPushDescriptors = _physicalDevice.IsDeviceExtensionPresent(KhrPushDescriptor.ExtensionName);

            PhysicalDevicePushDescriptorPropertiesKHR propertiesPushDescriptor = new PhysicalDevicePushDescriptorPropertiesKHR()
            {
                SType = StructureType.PhysicalDevicePushDescriptorPropertiesKhr
            };

            if (supportsPushDescriptors)
            {
                propertiesPushDescriptor.PNext = properties2.PNext;
                properties2.PNext = &propertiesPushDescriptor;
            }

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };

            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT featuresPrimitiveTopologyListRestart = new()
            {
                SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
            };

            PhysicalDeviceRobustness2FeaturesEXT featuresRobustness2 = new()
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
            };

            PhysicalDeviceShaderFloat16Int8FeaturesKHR featuresShaderInt8 = new()
            {
                SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
            };

            PhysicalDeviceCustomBorderColorFeaturesEXT featuresCustomBorderColor = new()
            {
                SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
            };

            PhysicalDeviceDepthClipControlFeaturesEXT featuresDepthClipControl = new()
            {
                SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
            };

            PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT featuresAttachmentFeedbackLoop = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
            };

            PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT featuresDynamicAttachmentFeedbackLoop = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
            };

            PhysicalDevicePortabilitySubsetFeaturesKHR featuresPortabilitySubset = new()
            {
                SType = StructureType.PhysicalDevicePortabilitySubsetFeaturesKhr,
            };

            // 新增：扩展动态状态3特性
            PhysicalDeviceExtendedDynamicState3FeaturesEXT featuresExtendedDynamicState3 = new()
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicState3FeaturesExt,
            };

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
            {
                features2.PNext = &featuresPrimitiveTopologyListRestart;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
            {
                featuresRobustness2.PNext = features2.PNext;
                features2.PNext = &featuresRobustness2;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_KHR_shader_float16_int8"))
            {
                featuresShaderInt8.PNext = features2.PNext;
                features2.PNext = &featuresShaderInt8;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color"))
            {
                featuresCustomBorderColor.PNext = features2.PNext;
                features2.PNext = &featuresCustomBorderColor;
            }

            bool supportsDepthClipControl = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control");

            if (supportsDepthClipControl)
            {
                featuresDepthClipControl.PNext = features2.PNext;
                features2.PNext = &featuresDepthClipControl;
            }

            bool supportsAttachmentFeedbackLoop = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout");

            if (supportsAttachmentFeedbackLoop)
            {
                featuresAttachmentFeedbackLoop.PNext = features2.PNext;
                features2.PNext = &featuresAttachmentFeedbackLoop;
            }

            bool supportsDynamicAttachmentFeedbackLoop = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state");

            if (supportsDynamicAttachmentFeedbackLoop)
            {
                featuresDynamicAttachmentFeedbackLoop.PNext = features2.PNext;
                features2.PNext = &featuresDynamicAttachmentFeedbackLoop;
            }

            // 新增：扩展动态状态3支持检测
            bool supportsExtendedDynamicState3 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_extended_dynamic_state3");

            if (supportsExtendedDynamicState3)
            {
                featuresExtendedDynamicState3.PNext = features2.PNext;
                features2.PNext = &featuresExtendedDynamicState3;
            }

            bool usePortability = _physicalDevice.IsDeviceExtensionPresent("VK_KHR_portability_subset");

            if (usePortability)
            {
                propertiesPortabilitySubset.PNext = properties2.PNext;
                properties2.PNext = &propertiesPortabilitySubset;

                featuresPortabilitySubset.PNext = features2.PNext;
                features2.PNext = &featuresPortabilitySubset;
            }

            Api.GetPhysicalDeviceProperties2(_physicalDevice.PhysicalDevice, &properties2);
            Api.GetPhysicalDeviceFeatures2(_physicalDevice.PhysicalDevice, &features2);

            var portabilityFlags = PortabilitySubsetFlags.None;
            uint vertexBufferAlignment = 1;

            if (usePortability)
            {
                vertexBufferAlignment = propertiesPortabilitySubset.MinVertexInputBindingStrideAlignment;

                portabilityFlags |= featuresPortabilitySubset.TriangleFans ? 0 : PortabilitySubsetFlags.NoTriangleFans;
                portabilityFlags |= featuresPortabilitySubset.PointPolygons ? 0 : PortabilitySubsetFlags.NoPointMode;
                portabilityFlags |= featuresPortabilitySubset.ImageView2DOn3DImage ? 0 : PortabilitySubsetFlags.No3DImageView;
                portabilityFlags |= featuresPortabilitySubset.SamplerMipLodBias ? 0 : PortabilitySubsetFlags.NoLodBias;
            }

            bool supportsCustomBorderColor = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color") &&
                                             featuresCustomBorderColor.CustomBorderColors &&
                                             featuresCustomBorderColor.CustomBorderColorWithoutFormat;

            ref var properties = ref properties2.Properties;

            var hasDriverProperties = _physicalDevice.TryGetPhysicalDeviceDriverPropertiesKHR(Api, out var driverProperties);

            Vendor = VendorUtils.FromId(properties.VendorID);

            IsAmdWindows = Vendor == Vendor.Amd && OperatingSystem.IsWindows();
            IsIntelWindows = Vendor == Vendor.Intel && OperatingSystem.IsWindows();
            IsTBDR =
                Vendor == Vendor.Apple ||
                Vendor == Vendor.Qualcomm ||
                Vendor == Vendor.ARM ||
                Vendor == Vendor.Broadcom ||
                Vendor == Vendor.ImgTec;

            GpuVendor = VendorUtils.GetNameFromId(properties.VendorID);
            GpuDriver = hasDriverProperties && !OperatingSystem.IsMacOS() ?
                VendorUtils.GetFriendlyDriverName(driverProperties.DriverID) : GpuVendor;

            fixed (byte* deviceName = properties.DeviceName)
            {
                GpuRenderer = Marshal.PtrToStringAnsi((IntPtr)deviceName);
            }

            GpuVersion = $"Vulkan v{ParseStandardVulkanVersion(properties.ApiVersion)}, Driver v{ParseDriverVersion(ref properties)}";

            IsAmdGcn = !IsMoltenVk && Vendor == Vendor.Amd && VendorUtils.AmdGcnRegex().IsMatch(GpuRenderer);

            IsAmdRdna3 = Vendor == Vendor.Amd && (VendorUtils.AmdRdna3Regex().IsMatch(GpuRenderer)
                                                  // ROG Ally (X) Device IDs
                                                  || properties.DeviceID is 0x15BF or 0x15C8);

            if (Vendor == Vendor.Nvidia)
            {
                var match = VendorUtils.NvidiaConsumerClassRegex().Match(GpuRenderer);

                if (match != null && int.TryParse(match.Groups[2].Value, out int gpuNumber))
                {
                    IsNvidiaPreTuring = gpuNumber < 2000;
                }
                else if (GpuRenderer.Contains("TITAN") && !GpuRenderer.Contains("RTX"))
                {
                    IsNvidiaPreTuring = true;
                }
            }
            else if (Vendor == Vendor.Intel)
            {
                IsIntelArc = GpuRenderer.StartsWith("Intel(R) Arc(TM)");
            }

            IsQualcommProprietary = hasDriverProperties && driverProperties.DriverID == DriverId.QualcommProprietary;

            ulong minResourceAlignment = Math.Max(
                Math.Max(
                    properties.Limits.MinStorageBufferOffsetAlignment,
                    properties.Limits.MinUniformBufferOffsetAlignment),
                properties.Limits.MinTexelBufferOffsetAlignment
            );

            SampleCountFlags supportedSampleCounts =
                properties.Limits.FramebufferColorSampleCounts &
                properties.Limits.FramebufferDepthSampleCounts &
                properties.Limits.FramebufferStencilSampleCounts;

            // 修复：更新能力集构造函数调用，添加缺失的 SupportsExtendedDynamicState3 参数
            bool supportsExtendedDynamicState3Feature = supportsExtendedDynamicState3 && featuresExtendedDynamicState3.ExtendedDynamicState3;

            Capabilities = new HardwareCapabilities(
                supportsIndexTypeUint8: _physicalDevice.IsDeviceExtensionPresent("VK_EXT_index_type_uint8"),
                supportsCustomBorderColor: supportsCustomBorderColor,
                supportsBlendEquationAdvanced: supportsBlendOperationAdvanced,
                supportsBlendEquationAdvancedCorrelatedOverlap: propertiesBlendOperationAdvanced.AdvancedBlendCorrelatedOverlap,
                supportsBlendEquationAdvancedNonPreMultipliedSrcColor: propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedSrcColor,
                supportsBlendEquationAdvancedNonPreMultipliedDstColor: propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedDstColor,
                supportsIndirectParameters: _physicalDevice.IsDeviceExtensionPresent(KhrDrawIndirectCount.ExtensionName),
                supportsFragmentShaderInterlock: _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_shader_interlock"),
                supportsGeometryShaderPassthrough: _physicalDevice.IsDeviceExtensionPresent("VK_NV_geometry_shader_passthrough"),
                supportsShaderFloat64: features2.Features.ShaderFloat64,
                supportsShaderInt8: featuresShaderInt8.ShaderInt8,
                supportsShaderStencilExport: _physicalDevice.IsDeviceExtensionPresent("VK_EXT_shader_stencil_export"),
                supportsShaderStorageImageMultisample: features2.Features.ShaderStorageImageMultisample,
                supportsConditionalRendering: _physicalDevice.IsDeviceExtensionPresent(ExtConditionalRendering.ExtensionName),
                supportsExtendedDynamicState: _physicalDevice.IsDeviceExtensionPresent(ExtExtendedDynamicState.ExtensionName),
                supportsExtendedDynamicState3: supportsExtendedDynamicState3Feature, // 新增：动态状态3支持
                supportsMultiView: features2.Features.MultiViewport && !(IsMoltenVk && Vendor == Vendor.Amd),
                supportsNullDescriptors: featuresRobustness2.NullDescriptor || IsMoltenVk,
                supportsPushDescriptors: supportsPushDescriptors && !IsMoltenVk,
                maxPushDescriptors: propertiesPushDescriptor.MaxPushDescriptors,
                supportsPrimitiveTopologyListRestart: featuresPrimitiveTopologyListRestart.PrimitiveTopologyListRestart,
                supportsPrimitiveTopologyPatchListRestart: featuresPrimitiveTopologyListRestart.PrimitiveTopologyPatchListRestart,
                supportsTransformFeedback: supportsTransformFeedback,
                supportsTransformFeedbackQueries: propertiesTransformFeedback.TransformFeedbackQueries,
                supportsPreciseOcclusionQueries: features2.Features.OcclusionQueryPrecise,
                supportsPipelineStatisticsQuery: _physicalDevice.PhysicalDeviceFeatures.PipelineStatisticsQuery,
                supportsGeometryShader: _physicalDevice.PhysicalDeviceFeatures.GeometryShader,
                supportsTessellationShader: _physicalDevice.PhysicalDeviceFeatures.TessellationShader,
                supportsViewportArray2: _physicalDevice.IsDeviceExtensionPresent("VK_NV_viewport_array2"),
                supportsHostImportedMemory: _physicalDevice.IsDeviceExtensionPresent(ExtExternalMemoryHost.ExtensionName),
                supportsDepthClipControl: supportsDepthClipControl && featuresDepthClipControl.DepthClipControl,
                supportsAttachmentFeedbackLoop: supportsAttachmentFeedbackLoop && featuresAttachmentFeedbackLoop.AttachmentFeedbackLoopLayout,
                supportsDynamicAttachmentFeedbackLoop: supportsDynamicAttachmentFeedbackLoop && featuresDynamicAttachmentFeedbackLoop.AttachmentFeedbackLoopDynamicState,
                subgroupSize: propertiesSubgroup.SubgroupSize,
                supportedSampleCounts: supportedSampleCounts,
                portabilitySubset: portabilityFlags,
                vertexBufferAlignment: vertexBufferAlignment,
                subTexelPrecisionBits: properties.Limits.SubTexelPrecisionBits,
                minResourceAlignment: minResourceAlignment);

            IsSharedMemory = MemoryAllocator.IsDeviceMemoryShared(_physicalDevice);

            MemoryAllocator = new MemoryAllocator(Api, _physicalDevice, _device);

            Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExternalMemoryHost hostMemoryApi);
            HostMemoryAllocator = new HostMemoryAllocator(MemoryAllocator, Api, hostMemoryApi, _device);

            CommandBufferPool = new CommandBufferPool(Api, _device, Queue, QueueLock, queueFamilyIndex, IsQualcommProprietary);

            PipelineLayoutCache = new PipelineLayoutCache();

            BackgroundResources = new BackgroundResources(this, _device);

            BufferManager = new BufferManager(this, _device);

            // 新增：初始化 DMA 加速器
            DmaAccelerator = new DmaAccelerator(this, BufferManager);

            SyncManager = new SyncManager(this, _device);
            _pipeline = new PipelineFull(this, _device);
            _pipeline.Initialize();

            HelperShader = new HelperShader(this, _device);

            Barriers = new BarrierBatch(this);

            _counters = new Counters(this, _device, _pipeline);
        }

        private uint FindComputeQueueFamily()
        {
            unsafe 
            {
                uint queueCount = 0;
                Api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice.PhysicalDevice, &queueCount, null);
                
                var queueFamilies = new QueueFamilyProperties[queueCount];
                
                fixed (QueueFamilyProperties* pQueueFamilies = queueFamilies)
                {
                    Api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice.PhysicalDevice, &queueCount, pQueueFamilies);
                }

                for (uint i = 0; i < queueCount; i++)
                {
                    ref var property = ref queueFamilies[i];
                    if (property.QueueFlags.HasFlag(QueueFlags.ComputeBit) &&
                        !property.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                    {
                        return i;
                    }
                }
            }

            return uint.MaxValue;
        }

        private void SetupContext(GraphicsDebugLevel logLevel)
        {
            _instance = VulkanInitialization.CreateInstance(Api, logLevel, _getRequiredExtensions());
            _debugMessenger = new VulkanDebugMessenger(Api, _instance.Instance, logLevel);

            if (Api.TryGetInstanceExtension(_instance.Instance, out KhrSurface surfaceApi))
            {
                SurfaceApi = surfaceApi;
            }

            _surface = _getSurface(_instance.Instance, Api);
            _physicalDevice = VulkanInitialization.FindSuitablePhysicalDevice(Api, _instance, _surface, _preferredGpuId);

            var queueFamilyIndex = VulkanInitialization.FindSuitableQueueFamily(Api, _physicalDevice, _surface, out uint maxQueueCount);

            _device = VulkanInitialization.CreateDevice(Api, _physicalDevice, queueFamilyIndex, maxQueueCount);

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrSwapchain swapchainApi))
            {
                SwapchainApi = swapchainApi;
            }

            Api.GetDeviceQueue(_device, queueFamilyIndex, 0, out var queue);
            Queue = queue;
            QueueLock = new object();

            // Init Locks
            SurfaceLock = new object();
            if (maxQueueCount >= 2)
            {
                Api.GetDeviceQueue(_device, queueFamilyIndex, 1, out var backgroundQueue);
                BackgroundQueue = backgroundQueue;
                BackgroundQueueLock = new object();
            }

            LoadFeatures(maxQueueCount, queueFamilyIndex);

            QueueFamilyIndex = queueFamilyIndex;

            _window = new Window(this, _surface, _physicalDevice.PhysicalDevice, _device);

            _initialized = true;
        }

        internal int[] GetPushDescriptorReservedBindings(bool isOgl)
        {
            if (_pdReservedBindings == null)
            {
                if (Capabilities.MaxPushDescriptors <= Constants.MaxUniformBuffersPerStage * 2)
                {
                    _pdReservedBindings = isOgl ? _pdReservedBindingsOgl : _pdReservedBindingsNvn;
                }
                else
                {
                    _pdReservedBindings = Array.Empty<int>();
                }
            }

            return _pdReservedBindings;
        }

        public BufferHandle CreateBuffer(int size, BufferAccess access)
        {
            return BufferManager.CreateWithHandle(this, size, access.HasFlag(BufferAccess.SparseCompatible), access.Convert(), access.HasFlag(BufferAccess.Stream));
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            return BufferManager.CreateHostImported(this, pointer, size);
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            return BufferManager.CreateSparse(this, storageBuffers);
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            return new ImageArray(this, size, isBuffer);
        }

        public IProgram CreateProgram(ShaderSource[] sources, ShaderInfo info)
        {
            ProgramCount++;

            bool isCompute = sources.Length == 1 && sources[0].Stage == ShaderStage.Compute;

            if (info.State.HasValue || isCompute)
            {
                return new ShaderCollection(this, _device, sources, info.ResourceLayout, info.State ?? default, info.FromCache);
            }

            return new ShaderCollection(this, _device, sources, info.ResourceLayout);
        }

        internal ShaderCollection CreateProgramWithMinimalLayout(ShaderSource[] sources, ResourceLayout resourceLayout, SpecDescription[] specDescription = null)
        {
            return new ShaderCollection(this, _device, sources, resourceLayout, specDescription, isMinimal: true);
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            return new SamplerHolder(this, _device, info);
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            if (info.Target == Target.TextureBuffer)
            {
                return new TextureBuffer(this, info);
            }

            return CreateTextureView(info);
        }

        public ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            return new TextureArray(this, size, isBuffer);
        }

        internal TextureView CreateTextureView(TextureCreateInfo info)
        {
            var storage = CreateTextureStorage(info);
            return storage.CreateView(info, 0, 0);
        }

        internal TextureStorage CreateTextureStorage(TextureCreateInfo info)
        {
            if (info.Width == 0 || info.Height == 0 || info.Depth == 0)
            {
                throw new ArgumentException("Invalid texture dimensions");
            }
            return new TextureStorage(this, _device, info);
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            BufferManager.Delete(buffer);
        }

        internal void FlushAllCommands()
        {
            _pipeline?.FlushCommandsImpl();
            _drawCounter = 0; // 重置绘制计数器
        }

        internal void RegisterFlush()
        {
            SyncManager.RegisterFlush();
            BufferManager.StagingBuffer.FreeCompleted();
            _drawCounter = 0; // 重置绘制计数器
        }

        // 新增：改进的帧生命周期管理
        public void TickFrame()
        {
            _drawCounter = 0;
            
            // 添加描述符队列的帧清理
            PipelineLayoutCache?.TickFrame();
            BufferManager?.TickFrame();
            
            // 动态状态跟踪器重置
            _dynamicStateTracker?.Reset();
            
            // 动态调整批处理大小（基于性能指标）
            AdjustBatchSizes();
        }

        // 新增：动态调整批处理大小
        private void AdjustBatchSizes()
        {
            // 基于帧时间和性能指标动态调整 DRAWS_TO_DISPATCH 和 DRAWS_TO_FLUSH
            // 这里可以实现自适应批处理大小调整逻辑
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            return BufferManager.GetData(buffer, offset, size);
        }

        public unsafe Capabilities GetCapabilities()
        {
            FormatFeatureFlags compressedFormatFeatureFlags =
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.SampledImageFilterLinearBit |
                FormatFeatureFlags.BlitSrcBit |
                FormatFeatureFlags.TransferSrcBit |
                FormatFeatureFlags.TransferDstBit;

            bool supportsBc123CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Bc1RgbaSrgb,
                Format.Bc1RgbaUnorm,
                Format.Bc2Srgb,
                Format.Bc2Unorm,
                Format.Bc3Srgb,
                Format.Bc3Unorm);

            bool supportsBc45CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Bc4Snorm,
                Format.Bc4Unorm,
                Format.Bc5Snorm,
                Format.Bc5Unorm);

            bool supportsBc67CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Bc6HSfloat,
                Format.Bc6HUfloat,
                Format.Bc7Srgb,
                Format.Bc7Unorm);

            bool supportsEtc2CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Etc2RgbaSrgb,
                Format.Etc2RgbaUnorm,
                Format.Etc2RgbPtaSrgb,
                Format.Etc2RgbPtaUnorm,
                Format.Etc2RgbSrgb,
                Format.Etc2RgbUnorm);

            bool supports5BitComponentFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.R5G6B5Unorm,
                Format.R5G5B5A1Unorm,
                Format.R5G5B5X1Unorm,
                Format.B5G6R5Unorm,
                Format.B5G5R5A1Unorm,
                Format.A1B5G5R5Unorm);

            bool supportsR4G4B4A4Format = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.R4G4B4A4Unorm);

            bool supportsAstcFormats = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Astc4x4Unorm,
                Format.Astc5x4Unorm,
                Format.Astc5x5Unorm,
                Format.Astc6x5Unorm,
                Format.Astc6x6Unorm,
                Format.Astc8x5Unorm,
                Format.Astc8x6Unorm,
                Format.Astc8x8Unorm,
                Format.Astc10x5Unorm,
                Format.Astc10x6Unorm,
                Format.Astc10x8Unorm,
                Format.Astc10x10Unorm,
                Format.Astc12x10Unorm,
                Format.Astc12x12Unorm,
                Format.Astc4x4Srgb,
                Format.Astc5x4Srgb,
                Format.Astc5x5Srgb,
                Format.Astc6x5Srgb,
                Format.Astc6x6Srgb,
                Format.Astc8x5Srgb,
                Format.Astc8x6Srgb,
                Format.Astc8x8Srgb,
                Format.Astc10x5Srgb,
                Format.Astc10x6Srgb,
                Format.Astc10x8Srgb,
                Format.Astc10x10Srgb,
                Format.Astc12x10Srgb,
                Format.Astc12x12Srgb);

            PhysicalDeviceVulkan12Features featuresVk12 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
            };

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &featuresVk12,
            };

            Api.GetPhysicalDeviceFeatures2(_physicalDevice.PhysicalDevice, &features2);

            var limits = _physicalDevice.PhysicalDeviceProperties.Limits;
            var mainQueueProperties = _physicalDevice.QueueFamilyProperties[QueueFamilyIndex];

            SystemMemoryType memoryType;

            if (IsSharedMemory)
            {
                memoryType = SystemMemoryType.UnifiedMemory;
            }
            else
            {
                memoryType = Vendor == Vendor.Nvidia ?
                    SystemMemoryType.DedicatedMemorySlowStorage :
                    SystemMemoryType.DedicatedMemory;
            }

            bool supportsFragmentDensityMap = SupportsFragmentDensityMap;
            bool supportsFragmentDensityMap2 = SupportsFragmentDensityMap2;

            // 修复：使用明确的参数名调用 Capabilities 构造函数
            return new Capabilities(
                api: TargetApi.Vulkan,
                vendorName: GpuVendor,
                memoryType: memoryType,
                hasFrontFacingBug: IsIntelWindows,
                hasVectorIndexingBug: IsQualcommProprietary,
                needsFragmentOutputSpecialization: IsMoltenVk,
                reduceShaderPrecision: IsMoltenVk,
                supportsAstcCompression: features2.Features.TextureCompressionAstcLdr && supportsAstcFormats,
                supportsBc123Compression: supportsBc123CompressionFormat,
                supportsBc45Compression: supportsBc45CompressionFormat,
                supportsBc67Compression: supportsBc67CompressionFormat,
                supportsEtc2Compression: supportsEtc2CompressionFormat,
                supports3DTextureCompression: true,
                supportsBgraFormat: true,
                supportsR4G4Format: false,
                supportsR4G4B4A4Format: supportsR4G4B4A4Format,
                supportsScaledVertexFormats: FormatCapabilities.SupportsScaledVertexFormats(),
                supportsSnormBufferTextureFormat: true,
                supports5BitComponentFormat: supports5BitComponentFormat,
                supportsSparseBuffer: features2.Features.SparseBinding && mainQueueProperties.QueueFlags.HasFlag(QueueFlags.SparseBindingBit),
                supportsBlendEquationAdvanced: Capabilities.SupportsBlendEquationAdvanced,
                supportsFragmentShaderInterlock: Capabilities.SupportsFragmentShaderInterlock,
                supportsFragmentShaderOrderingIntel: false,
                supportsGeometryShader: Capabilities.SupportsGeometryShader,
                supportsGeometryShaderPassthrough: Capabilities.SupportsGeometryShaderPassthrough,
                supportsTransformFeedback: Capabilities.SupportsTransformFeedback,
                supportsImageLoadFormatted: features2.Features.ShaderStorageImageReadWithoutFormat,
                supportsLayerVertexTessellation: featuresVk12.ShaderOutputLayer,
                supportsMismatchingViewFormat: true,
                supportsCubemapView: !IsAmdGcn,
                supportsNonConstantTextureOffset: false,
                supportsQuads: false,
                supportsSeparateSampler: true,
                supportsShaderBallot: false,
                supportsShaderBallotDivergence: Vendor != Vendor.Qualcomm,
                supportsShaderBarrierDivergence: Vendor != Vendor.Intel,
                supportsShaderFloat64: Capabilities.SupportsShaderFloat64,
                supportsTextureGatherOffsets: features2.Features.ShaderImageGatherExtended && !IsMoltenVk,
                supportsTextureShadowLod: false,
                supportsVertexStoreAndAtomics: features2.Features.VertexPipelineStoresAndAtomics,
                supportsViewportIndexVertexTessellation: featuresVk12.ShaderOutputViewportIndex,
                supportsViewportMask: Capabilities.SupportsViewportArray2,
                supportsViewportSwizzle: false,
                supportsIndirectParameters: true,
                supportsDepthClipControl: Capabilities.SupportsDepthClipControl,
                supportsFragmentDensityMap: supportsFragmentDensityMap,
                supportsFragmentDensityMap2: supportsFragmentDensityMap2,
                // 新增：动态状态支持
                supportsExtendedDynamicState: Capabilities.SupportsExtendedDynamicState,
                supportsExtendedDynamicState3: Capabilities.SupportsExtendedDynamicState3,
                uniformBufferSetIndex: PipelineBase.UniformSetIndex,
                storageBufferSetIndex: PipelineBase.StorageSetIndex,
                textureSetIndex: PipelineBase.TextureSetIndex,
                imageSetIndex: PipelineBase.ImageSetIndex,
                extraSetBaseIndex: PipelineBase.DescriptorSetLayouts,
                maximumExtraSets: Math.Max(0, (int)limits.MaxBoundDescriptorSets - PipelineBase.DescriptorSetLayouts),
                maximumUniformBuffersPerStage: Constants.MaxUniformBuffersPerStage,
                maximumStorageBuffersPerStage: Constants.MaxStorageBuffersPerStage,
                maximumTexturesPerStage: Constants.MaxTexturesPerStage,
                maximumImagesPerStage: Constants.MaxImagesPerStage,
                maximumComputeSharedMemorySize: (int)limits.MaxComputeSharedMemorySize,
                maximumSupportedAnisotropy: (int)limits.MaxSamplerAnisotropy,
                shaderSubgroupSize: (int)Capabilities.SubgroupSize,
                storageBufferOffsetAlignment: (int)limits.MinStorageBufferOffsetAlignment,
                textureBufferOffsetAlignment: (int)limits.MinTexelBufferOffsetAlignment,
                gatherBiasPrecision: IsIntelWindows || IsAmdWindows ? (int)Capabilities.SubTexelPrecisionBits : 0,
                maximumGpuMemory: GetTotalGPUMemory());
        }

        private ulong GetTotalGPUMemory()
        {
            ulong totalMemory = 0;

            Api.GetPhysicalDeviceMemoryProperties(_physicalDevice.PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);

            for (int i = 0; i < memoryProperties.MemoryHeapCount; i++)
            {
                var heap = memoryProperties.MemoryHeaps[i];
                if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) == MemoryHeapFlags.DeviceLocalBit)
                {
                    totalMemory += heap.Size;
                }
            }

            return totalMemory;
        }

        public HardwareInfo GetHardwareInfo()
        {
            return new HardwareInfo(GpuVendor, GpuRenderer, GpuDriver);
        }

        public static DeviceInfo[] GetPhysicalDevices()
        {
            try
            {
                return VulkanInitialization.GetSuitablePhysicalDevices(Vk.GetApi());
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, $"Error querying Vulkan devices: {ex.Message}");
                return Array.Empty<DeviceInfo>();
            }
        }

        public static DeviceInfo[] GetPhysicalDevices(Vk api)
        {
            try
            {
                return VulkanInitialization.GetSuitablePhysicalDevices(api);
            }
            catch (Exception)
            {
                return Array.Empty<DeviceInfo>();
            }
        }

        private static string ParseStandardVulkanVersion(uint version)
        {
            return $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";
        }

        private static string ParseDriverVersion(ref PhysicalDeviceProperties properties)
        {
            uint driverVersionRaw = properties.DriverVersion;

            if (properties.VendorID == 0x10DE)
            {
                return $"{(driverVersionRaw >> 22) & 0x3FF}.{(driverVersionRaw >> 14) & 0xFF}.{(driverVersionRaw >> 6) & 0xFF}.{driverVersionRaw & 0x3F}";
            }

            return ParseStandardVulkanVersion(driverVersionRaw);
        }

        internal PrimitiveTopology TopologyRemap(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads => PrimitiveTopology.Triangles,
                PrimitiveTopology.QuadStrip => PrimitiveTopology.TriangleStrip,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.NoTriangleFans)
                    ? PrimitiveTopology.Triangles
                    : topology,
                _ => topology,
            };
        }

        internal bool TopologyUnsupported(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads => true,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.NoTriangleFans),
                _ => false,
            };
        }

        private void PrintGpuInformation()
        {
            Logger.Notice.Print(LogClass.Gpu, $"{GpuVendor} {GpuRenderer} ({GpuVersion})");
            Logger.Notice.Print(LogClass.Gpu, $"GPU Memory: {GetTotalGPUMemory() / (1024 * 1024)} MiB");
            
            // 新增：打印动态状态支持信息
            if (Capabilities.SupportsExtendedDynamicState)
            {
                Logger.Notice.Print(LogClass.Gpu, "Extended Dynamic State: Supported");
            }
            if (Capabilities.SupportsExtendedDynamicState3)
            {
                Logger.Notice.Print(LogClass.Gpu, "Extended Dynamic State 3: Supported");
            }
        }

        public void Initialize(GraphicsDebugLevel logLevel)
        {
            SetupContext(logLevel);
            PrintGpuInformation();
        }

        internal bool NeedsVertexBufferAlignment(int attrScalarAlignment, out int alignment)
        {
            if (Capabilities.VertexBufferAlignment > 1)
            {
                alignment = (int)Capabilities.VertexBufferAlignment;
                return true;
            }
            else if (Vendor != Vendor.Nvidia)
            {
                alignment = attrScalarAlignment;
                return true;
            }

            alignment = 1;
            return false;
        }

        public void PreFrame()
        {
            SyncManager.Cleanup();
            // 新增：每帧重置动态状态跟踪器
            _dynamicStateTracker?.ResetDirtyFlags();
        }

        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            return _counters.QueueReport(type, resultHandler, divisor, hostReserved);
        }

        public void ResetCounter(CounterType type)
        {
            _counters.QueueReset(type);
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            BufferManager.SetData(buffer, offset, data, _pipeline.CurrentCommandBuffer, _pipeline.EndRenderPassDelegate);
        }

        public void UpdateCounters()
        {
            _counters.Update();
        }

        public void ResetCounterPool()
        {
            _counters.ResetCounterPool();
        }

        public void ResetFutureCounters(CommandBuffer cmd, int count)
        {
            _counters?.ResetFutureCounters(cmd, count);
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            action();
        }

        public void CreateSync(ulong id, bool strict)
        {
            SyncManager.Create(id, strict);
        }

        public IProgram LoadProgramBinary(byte[] programBinary, bool isFragment, ShaderInfo info)
        {
            throw new NotImplementedException();
        }

        public void WaitSync(ulong id)
        {
            SyncManager.Wait(id);
        }

        public ulong GetCurrentSync()
        {
            return SyncManager.GetCurrent();
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            InterruptAction = interruptAction;
        }

        public void Screenshot()
        {
            _window.ScreenCaptureRequested = true;
        }

        public void OnScreenCaptured(ScreenCaptureImageInfo bitmap)
        {
            ScreenCaptured?.Invoke(this, bitmap);
        }

        public bool SupportsRenderPassBarrier(PipelineStageFlags flags)
        {
            return !(IsMoltenVk || IsQualcommProprietary);
        }

        // 新增：改进的同步机制，借鉴 yuzu 的精确同步
        public void WaitForIdle()
        {
            if (TimelineSemaphoreApi != null)
            {
                // 使用时间线信号量进行精确同步
                SyncManager.WaitForIdle();
            }
            else
            {
                // 回退到传统同步
                FlushAllCommands();
            }
        }

        // ===== Surface/Present Lifecycle helpers =====

        public unsafe bool RecreateSurface()
        {
            if (!PresentAllowed || SurfaceLock == null)
            {
                return false;
            }

            lock (SurfaceLock)
            {
                try
                {
                    if (_surface.Handle != 0)
                    {
                        SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
                        _surface = new SurfaceKHR(0);
                    }

                    _surface = _getSurface(_instance.Instance, Api);
                    if (_surface.Handle == 0)
                    {
                        return false;
                    }

                    ( _window as Window )?.SetSurface(_surface);
                    ( _window as Window )?.SetSurfaceQueryAllowed(true);
          
                    return true;
                }
                catch (Exception ex)
                {
                    return false;
                }
            }
        }

        public unsafe void ReleaseSurface()
        {
            if (SurfaceLock == null)
            {
                return;
            }

            lock (SurfaceLock)
            {
                try
                {
                    ( _window as Window )?.SetSurfaceQueryAllowed(false);

                    if (_surface.Handle != 0)
                    {
                        SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
                        _surface = new SurfaceKHR(0);
                    }
                }
                catch (Exception ex)
                {
                }

                ( _window as Window )?.OnSurfaceLost();
            }
        }

        public void SetPresentEnabled(bool enabled)
        {
            if (!_initialized || SurfaceLock == null)
            {
                return;
            }

            PresentAllowed = enabled;

            if (!enabled)
            {
                ( _window as Window )?.SetSurfaceQueryAllowed(false);
                ReleaseSurface();
            }
            else
            {
                _ = RecreateSurface();
            }
        }

        public void SetPresentAllowed(bool allowed)
        {
            if (!_initialized || SurfaceLock == null)
            {
                return;
            }

            PresentAllowed = allowed;

            if (allowed)
            {
                try
                {
                    ( _window as Window )?.SetSurfaceQueryAllowed(true);
                    _window?.SetSize(0, 0);
                    _ = RecreateSurface();
                }
                catch (Exception ex)
                {
                }
            }
            else
            {
                ( _window as Window )?.SetSurfaceQueryAllowed(false);
            }
        }

        public unsafe void Dispose()
        {
            if (!_initialized)
            {
                return;
            }

            CommandBufferPool?.Dispose();
            _computeCommandPool?.Dispose();
            BackgroundResources?.Dispose();
            _counters?.Dispose();
            _window?.Dispose();
            HelperShader?.Dispose();
            _pipeline?.Dispose();
            BufferManager?.Dispose();
            PipelineLayoutCache?.Dispose();
            Barriers?.Dispose();
            DmaAccelerator?.Dispose(); // 新增：清理 DMA 加速器

            MemoryAllocator?.Dispose();

            foreach (var shader in Shaders) shader.Dispose();
            foreach (var texture in Textures) texture.Release();
            foreach (var sampler in Samplers) sampler.Dispose();

            if (_surface.Handle != 0)
            {
                SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
            }

            Api.DestroyDevice(_device, null);

            _debugMessenger?.Dispose();

            // Last step destroy the instance
            _instance?.Dispose();
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return Capabilities.SupportsHostImportedMemory &&
                HostMemoryAllocator.TryImport(BufferManager.HostImportedBufferMemoryRequirements, BufferManager.DefaultBufferMemoryFlags, address, size);
        }
    }

    // 新增：动态状态跟踪器类
    internal class DynamicStateTracker
    {
        private CullModeFlags _lastCullMode;
        private bool _lastCullEnable;
        private bool _lastDepthTestEnable;
        private VkCompareOp _lastDepthCompareOp; // 使用 VkCompareOp 解决歧义
        private bool _lastStencilTestEnable;
        private StencilOpState _lastStencilOps;

        public DynamicStateTracker()
        {
            Reset();
        }

        public void Reset()
        {
            _lastCullMode = CullModeFlags.None;
            _lastCullEnable = false;
            _lastDepthTestEnable = false;
            _lastDepthCompareOp = VkCompareOp.Never; // 使用 VkCompareOp
            _lastStencilTestEnable = false;
            _lastStencilOps = default;
        }

        public void ResetDirtyFlags()
        {
            // 保留当前状态但清除脏标志，用于帧开始
        }

        public bool ShouldUpdateCullMode(CullModeFlags newMode, bool newEnable)
        {
            bool shouldUpdate = _lastCullMode != newMode || _lastCullEnable != newEnable;
            if (shouldUpdate)
            {
                _lastCullMode = newMode;
                _lastCullEnable = newEnable;
            }
            return shouldUpdate;
        }

        public bool ShouldUpdateDepthTest(bool newEnable, GALCompareOp newCompareOp) // 使用 GALCompareOp
        {
            // 修复：使用明确的 CompareOpExtensions.Convert 方法解决歧义
            VkCompareOp vkCompareOp = CompareOpExtensions.Convert(newCompareOp);
            bool shouldUpdate = _lastDepthTestEnable != newEnable || _lastDepthCompareOp != vkCompareOp;
            if (shouldUpdate)
            {
                _lastDepthTestEnable = newEnable;
                _lastDepthCompareOp = vkCompareOp;
            }
            return shouldUpdate;
        }

        public bool ShouldUpdateStencil(bool newEnable, StencilOpState newOps)
        {
            bool shouldUpdate = _lastStencilTestEnable != newEnable || !_lastStencilOps.Equals(newOps);
            if (shouldUpdate)
            {
                _lastStencilTestEnable = newEnable;
                _lastStencilOps = newOps;
            }
            return shouldUpdate;
        }
    }
}