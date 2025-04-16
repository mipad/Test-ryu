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
using Silk.NET.Vulkan.Extensions.ARM;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Format = Ryujinx.Graphics.GAL.Format;
using PrimitiveTopology = Ryujinx.Graphics.GAL.PrimitiveTopology;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;



namespace Ryujinx.Graphics.Vulkan
{
    public sealed class VulkanRenderer : IRenderer
    {
        private ArmRasterizationOrderAttachmentAccess _armRasterizationOrderAttachmentAccess;
        private MaliTextureMirrorSampler _maliTextureMirrorSampler;
        private VulkanInstance _instance;
        private SurfaceKHR _surface;
        private VulkanPhysicalDevice _physicalDevice;
        private Device _device;
        private WindowBase _window;

        private bool _initialized;

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

        internal uint QueueFamilyIndex { get; private set; }
        internal Queue Queue { get; private set; }
        internal Queue BackgroundQueue { get; private set; }
        internal object BackgroundQueueLock { get; private set; }
        internal object QueueLock { get; private set; }

        internal MemoryAllocator MemoryAllocator { get; private set; }
        internal HostMemoryAllocator HostMemoryAllocator { get; private set; }
        internal CommandBufferPool CommandBufferPool { get; private set; }
        internal PipelineLayoutCache PipelineLayoutCache { get; private set; }
        internal BackgroundResources BackgroundResources { get; private set; }
        internal Action<Action> InterruptAction { get; private set; }
        internal SyncManager SyncManager { get; private set; }

        internal BufferManager BufferManager { get; private set; }

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
        internal bool IsNvidiaPreTuring { get; private set; }
        internal bool IsIntelArc { get; private set; }
        internal bool IsQualcommProprietary { get; private set; }
        internal bool IsMoltenVk { get; private set; }
        internal bool IsTBDR { get; private set; }
        internal bool IsSharedMemory { get; private set; }

        public string GpuVendor { get; private set; }
        public string GpuDriver { get; private set; }
        public string GpuRenderer { get; private set; }
        public string GpuVersion { get; private set; }

        public bool SupportsRasterizationOrderAttachmentAccess { get; internal set; }
    
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

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                MVKInitialization.Initialize();

                // Any device running on Darwin is using MoltenVK, even Intel and AMD vendors.
                IsMoltenVk = true;
            }
        }

        private unsafe void LoadFeatures(uint maxQueueCount, uint queueFamilyIndex)
        {
            FormatCapabilities = new FormatCapabilities(Api, _physicalDevice.PhysicalDevice);

if (Api.TryGetDeviceExtension<ArmRasterizationOrderAttachmentAccess>(_instance.Instance, _device, out var armExtension))
    {
        Capabilities.SupportsRasterizationOrderAttachmentAccess = true;
        // 可选：保存扩展方法实例供后续使用
        _armRasterizationOrderAttachmentAccess = armExtension;
    }
    
            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtConditionalRendering conditionalRenderingApi))
            {
                ConditionalRenderingApi = conditionalRenderingApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExtendedDynamicState extendedDynamicStateApi))
            {
                ExtendedDynamicStateApi = extendedDynamicStateApi;
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
                VendorUtils.GetFriendlyDriverName(driverProperties.DriverID) : GpuVendor; // Fallback to vendor name if driver is unavailable or on MacOS where vendor is preferred.

            fixed (byte* deviceName = properties.DeviceName)
            {
                GpuRenderer = Marshal.PtrToStringAnsi((IntPtr)deviceName);
            }

            GpuVersion = $"Vulkan v{ParseStandardVulkanVersion(properties.ApiVersion)}, Driver v{ParseDriverVersion(ref properties)}";

            IsAmdGcn = !IsMoltenVk && Vendor == Vendor.Amd && VendorUtils.AmdGcnRegex().IsMatch(GpuRenderer);

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

            Capabilities = new HardwareCapabilities(
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_index_type_uint8"),
                supportsCustomBorderColor,
                supportsBlendOperationAdvanced,
                propertiesBlendOperationAdvanced.AdvancedBlendCorrelatedOverlap,
                propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedSrcColor,
                propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedDstColor,
                _physicalDevice.IsDeviceExtensionPresent(KhrDrawIndirectCount.ExtensionName),
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_shader_interlock"),
                _physicalDevice.IsDeviceExtensionPresent("VK_NV_geometry_shader_passthrough"),
                features2.Features.ShaderFloat64,
                featuresShaderInt8.ShaderInt8,
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_shader_stencil_export"),
                features2.Features.ShaderStorageImageMultisample,
                _physicalDevice.IsDeviceExtensionPresent(ExtConditionalRendering.ExtensionName),
                _physicalDevice.IsDeviceExtensionPresent(ExtExtendedDynamicState.ExtensionName),
                features2.Features.MultiViewport && !(IsMoltenVk && Vendor == Vendor.Amd), // Workaround for AMD on MoltenVK issue
                featuresRobustness2.NullDescriptor || IsMoltenVk,
                supportsPushDescriptors && !IsMoltenVk,
                propertiesPushDescriptor.MaxPushDescriptors,
                featuresPrimitiveTopologyListRestart.PrimitiveTopologyListRestart,
                featuresPrimitiveTopologyListRestart.PrimitiveTopologyPatchListRestart,
                supportsTransformFeedback,
                propertiesTransformFeedback.TransformFeedbackQueries,
                features2.Features.OcclusionQueryPrecise,
                _physicalDevice.PhysicalDeviceFeatures.PipelineStatisticsQuery,
                _physicalDevice.PhysicalDeviceFeatures.GeometryShader,
                _physicalDevice.PhysicalDeviceFeatures.TessellationShader,
                _physicalDevice.IsDeviceExtensionPresent("VK_NV_viewport_array2"),
                _physicalDevice.IsDeviceExtensionPresent(ExtExternalMemoryHost.ExtensionName),
                supportsDepthClipControl && featuresDepthClipControl.DepthClipControl,
                supportsAttachmentFeedbackLoop && featuresAttachmentFeedbackLoop.AttachmentFeedbackLoopLayout,
                supportsDynamicAttachmentFeedbackLoop && featuresDynamicAttachmentFeedbackLoop.AttachmentFeedbackLoopDynamicState,
                propertiesSubgroup.SubgroupSize,
                supportedSampleCounts,
                portabilityFlags,
                vertexBufferAlignment,
                properties.Limits.SubTexelPrecisionBits,
                minResourceAlignment);

            IsSharedMemory = MemoryAllocator.IsDeviceMemoryShared(_physicalDevice);

            MemoryAllocator = new MemoryAllocator(Api, _physicalDevice, _device);

            Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExternalMemoryHost hostMemoryApi);
            HostMemoryAllocator = new HostMemoryAllocator(MemoryAllocator, Api, hostMemoryApi, _device);

            CommandBufferPool = new CommandBufferPool(Api, _device, Queue, QueueLock, queueFamilyIndex, IsQualcommProprietary);

            PipelineLayoutCache = new PipelineLayoutCache();

            BackgroundResources = new BackgroundResources(this, _device);

            BufferManager = new BufferManager(this, _device);

            SyncManager = new SyncManager(this, _device);
            _pipeline = new PipelineFull(this, _device);
            _pipeline.Initialize();

            HelperShader = new HelperShader(this, _device);

            Barriers = new BarrierBatch(this);

            _counters = new Counters(this, _device, _pipeline);
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

// ---------- 修改开始 ----------
// 1. 获取物理设备支持的扩展列表
// 1. 获取物理设备支持的扩展列表
uint extensionCount = 0;
Api.EnumerateDeviceExtensionProperties(_physicalDevice.PhysicalDevice, (byte*)null, ref extensionCount, null);
var extensions = new ExtensionProperties[extensionCount];
fixed (ExtensionProperties* pExtensions = extensions)
{
    Api.EnumerateDeviceExtensionProperties(_physicalDevice.PhysicalDevice, (byte*)null, ref extensionCount, pExtensions);
}
var supportedExtensions = extensions.Select(e => Marshal.PtrToStringAnsi((IntPtr)e.ExtensionName)).ToList();

// 2. 合并原有启用的扩展与新扩展
var enabledExtensions = _physicalDevice.GetEnabledExtensions().ToList(); // 保留原有扩展
enabledExtensions.Add(KhrSwapchain.ExtensionName); // 确保交换链扩展存在

// 3. 按需添加ARM和Mali扩展
if (supportedExtensions.Contains("VK_ARM_rasterization_order_attachment_access"))
{
    enabledExtensions.Add("VK_ARM_rasterization_order_attachment_access");
}

// 4. 配置队列信息
float queuePriority = 1.0f;
var queueCreateInfo = new DeviceQueueCreateInfo
{
    SType = StructureType.DeviceQueueCreateInfo,
    QueueFamilyIndex = queueFamilyIndex,
    QueueCount = 1,
    PQueuePriorities = &queuePriority
};

// 5. 配置设备创建信息
var deviceCreateInfo = new DeviceCreateInfo
{
    SType = StructureType.DeviceCreateInfo,
    QueueCreateInfoCount = 1,
    PQueueCreateInfos = &queueCreateInfo,
    EnabledExtensionCount = (uint)enabledExtensions.Count,
    PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(enabledExtensions),
    PEnabledFeatures = &_physicalDevice.PhysicalDeviceFeatures
};

// 6. 直接调用Vulkan API创建设备
Api.CreateDevice(_physicalDevice.PhysicalDevice, in deviceCreateInfo, null, out _device);

// 释放非托管内存
SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledExtensionNames);
// ---------- 修改结束 ----------

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrSwapchain swapchainApi))
            {
                SwapchainApi = swapchainApi;
            }

            Api.GetDeviceQueue(_device, queueFamilyIndex, 0, out var queue);
            Queue = queue;
            QueueLock = new object();

            LoadFeatures(maxQueueCount, queueFamilyIndex);

            QueueFamilyIndex = queueFamilyIndex;

            _window = new Window(this, _surface, _physicalDevice.PhysicalDevice, _device);

            _initialized = true;
        }

        internal int[] GetPushDescriptorReservedBindings(bool isOgl)
        {
            // The first call of this method determines what push descriptor layout is used for all shaders on this renderer.
            // This is chosen to minimize shaders that can't fit their uniforms on the device's max number of push descriptors.
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
            // This should be disposed when all views are destroyed.
            var storage = CreateTextureStorage(info);
            return storage.CreateView(info, 0, 0);
        }

        internal TextureStorage CreateTextureStorage(TextureCreateInfo info)
        {
            return new TextureStorage(this, _device, info);
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            BufferManager.Delete(buffer);
        }

        internal void FlushAllCommands()
        {
            _pipeline?.FlushCommandsImpl();
        }

        internal void RegisterFlush()
        {
            SyncManager.RegisterFlush();

            // Periodically free unused regions of the staging buffer to avoid doing it all at once.
            BufferManager.StagingBuffer.FreeCompleted();
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
          
