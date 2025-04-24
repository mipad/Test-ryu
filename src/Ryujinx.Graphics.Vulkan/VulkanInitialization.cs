using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan
{
    public unsafe static class VulkanInitialization
    {
        private const uint InvalidIndex = uint.MaxValue;
        private static readonly uint _minimalVulkanVersion = Vk.Version11.Value;
        private static readonly uint _minimalInstanceVulkanVersion = Vk.Version12.Value;
        private static readonly uint _maximumVulkanVersion = Vk.Version12.Value; 
        // 在类顶部添加以下常量定义：
private const string ExtBlendOperationAdvanced = "VK_EXT_blend_operation_advanced";
private const string ExtDescriptorIndexing = "VK_EXT_descriptor_indexing";
private const string ExtShaderStencilExport = "VK_EXT_shader_stencil_export";
private const string KhrShaderFloat16Int8 = "VK_KHR_shader_float16_int8";
private const string ExtShaderSubgroupBallot = "VK_EXT_shader_subgroup_ballot";
private const string NvGeometryShaderPassthrough = "VK_NV_geometry_shader_passthrough";
private const string NvViewportArray2 = "VK_NV_viewport_array2";
private const string KhrPortabilitySubset = "VK_KHR_portability_subset";
private const string Ext4444Formats = "VK_EXT_4444_formats";
private const string Khr8bitStorage = "VK_KHR_8bit_storage";
private const string KhrMaintenance2 = "VK_KHR_maintenance2";
        
        private const string ExtRobustness2 = "VK_EXT_robustness2";
   private const string ExtFragmentInterlock = "VK_EXT_fragment_shader_interlock";      
        private const string ExtAttachmentFeedbackLoopLayout = "VK_EXT_attachment_feedback_loop_layout";
   private const string ExtAttachmentFeedbackLoopDynamicState = "VK_EXT_attachment_feedback_loop_dynamic_state";
   private const string ExtPrimitiveTopologyListRestart = "VK_EXT_primitive_topology_list_restart";
   private const string ExtCustomBorderColor = "VK_EXT_custom_border_color";
        private const string ExtIndexTypeUint8 = "VK_EXT_index_type_uint8";
   private const string ExtDepthClipControl = "VK_EXT_depth_clip_control";
        private const string AppName = "Ryujinx.Graphics.Vulkan";
        private const int QueuesCount = 2;

        private static readonly string[] _desirableExtensions = {
    ExtConditionalRendering.ExtensionName,
    ExtExtendedDynamicState.ExtensionName,
    ExtTransformFeedback.ExtensionName,
    KhrDrawIndirectCount.ExtensionName,
    KhrPushDescriptor.ExtensionName,
    ExtExternalMemoryHost.ExtensionName,
    ExtBlendOperationAdvanced,        // 替换为常量
    ExtCustomBorderColor,
    ExtDescriptorIndexing,            // 替换为常量
    ExtFragmentInterlock,
    ExtIndexTypeUint8,
    ExtPrimitiveTopologyListRestart,
    ExtRobustness2,
    ExtShaderStencilExport,           // 替换为常量
    KhrShaderFloat16Int8,             // 替换为常量
    ExtShaderSubgroupBallot,          // 替换为常量
    NvGeometryShaderPassthrough,      // 替换为常量
    NvViewportArray2,                 // 替换为常量
    ExtDepthClipControl,
    KhrPortabilitySubset,             // 替换为常量
    Ext4444Formats,                   // 替换为常量
    Khr8bitStorage,                   // 替换为常量
    KhrMaintenance2,                  // 替换为常量
    ExtAttachmentFeedbackLoopLayout,
    ExtAttachmentFeedbackLoopDynamicState
};

private static ApplicationInfo CreateApplicationInfo(IntPtr appNamePtr)
   {
       return new ApplicationInfo
       {
           PApplicationName = (byte*)appNamePtr,
           ApplicationVersion = 1,
           PEngineName = (byte*)appNamePtr,
           EngineVersion = 1,
           ApiVersion = _maximumVulkanVersion
       };
   }

private static void SetupExternalMemoryHostFeatures(
    VulkanPhysicalDevice physicalDevice,
    ref void* pExtendedFeatures)
{
    if (!physicalDevice.IsDeviceExtensionPresent(ExtExternalMemoryHost.ExtensionName))
        return;

    // 该扩展无需特殊功能结构体，但为了统一代码结构，可以添加空操作或占位符
    // 如果有需要启用特定功能，可在此处添加对应的结构体初始化
    // 例如：
    // var features = new PhysicalDeviceExternalMemoryHostPropertiesEXT
    // {
    //     SType = StructureType.PhysicalDeviceExternalMemoryHostPropertiesExt,
    //     PNext = pExtendedFeatures,
    // };
    // pExtendedFeatures = &features;

    // 当前仅启用扩展，无需额外操作
}

private static void SetupAttachmentFeedbackLoopDynamicStateFeatures(
       VulkanPhysicalDevice physicalDevice,
       ref void* pExtendedFeatures,
       PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT supportedFeatures)
   {
       if (!physicalDevice.IsDeviceExtensionPresent(ExtAttachmentFeedbackLoopDynamicState) ||
           !supportedFeatures.AttachmentFeedbackLoopDynamicState)
           return;

       var features = new PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT
       {
           SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
           PNext = pExtendedFeatures,
           AttachmentFeedbackLoopDynamicState = true
       };
       pExtendedFeatures = &features;
   }

private static void SetupDepthClipControlFeatures(
       VulkanPhysicalDevice physicalDevice,
       ref void* pExtendedFeatures,
       PhysicalDeviceDepthClipControlFeaturesEXT supportedFeatures)
   {
       if (!physicalDevice.IsDeviceExtensionPresent(ExtDepthClipControl) ||
           !supportedFeatures.DepthClipControl)
           return;

       var features = new PhysicalDeviceDepthClipControlFeaturesEXT
       {
           SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
           PNext = pExtendedFeatures,
           DepthClipControl = true
       };
       pExtendedFeatures = &features;
   }

private static void SetupAttachmentFeedbackLoopFeatures(
       VulkanPhysicalDevice physicalDevice,
       ref void* pExtendedFeatures,
       PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT supportedFeatures)
   {
       if (!physicalDevice.IsDeviceExtensionPresent(ExtAttachmentFeedbackLoopLayout) ||
           !supportedFeatures.AttachmentFeedbackLoopLayout)
           return;

       var features = new PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT
       {
           SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
           PNext = pExtendedFeatures,
           AttachmentFeedbackLoopLayout = true
       };
       pExtendedFeatures = &features;
   }

private static void SetupPrimitiveTopologyListRestartFeatures(
       VulkanPhysicalDevice physicalDevice,
       ref void* pExtendedFeatures,
       PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT supportedFeatures)
   {
       //const string ExtPrimitiveTopology = "VK_EXT_primitive_topology_list_restart";
       if (!physicalDevice.IsDeviceExtensionPresent(ExtPrimitiveTopologyListRestart))
           return;

       var features = new PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT
       {
           SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
           PNext = pExtendedFeatures,
           PrimitiveTopologyListRestart = supportedFeatures.PrimitiveTopologyListRestart,
           PrimitiveTopologyPatchListRestart = supportedFeatures.PrimitiveTopologyPatchListRestart
       };
       pExtendedFeatures = &features;
   }

private static void SetupCustomBorderColorFeatures(
    VulkanPhysicalDevice physicalDevice,
    ref void* pExtendedFeatures,
    PhysicalDeviceCustomBorderColorFeaturesEXT supportedFeatures)
{
    //const string ExtCustomBorderColor = "VK_EXT_custom_border_color";
    if (!physicalDevice.IsDeviceExtensionPresent(ExtCustomBorderColor) ||
        !supportedFeatures.CustomBorderColors ||
        !supportedFeatures.CustomBorderColorWithoutFormat)
        return;

    var features = new PhysicalDeviceCustomBorderColorFeaturesEXT
    {
        SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
        PNext = pExtendedFeatures,
        CustomBorderColors = true,
        CustomBorderColorWithoutFormat = true
    };
    pExtendedFeatures = &features;
}

private static void SetupFragmentShaderInterlockFeatures(
    VulkanPhysicalDevice physicalDevice,
    ref void* pExtendedFeatures)
{
    //const string ExtFragmentInterlock = "VK_EXT_fragment_shader_interlock";
    if (!physicalDevice.IsDeviceExtensionPresent(ExtFragmentInterlock))
        return;

    var features = new PhysicalDeviceFragmentShaderInterlockFeaturesEXT
    {
        SType = StructureType.PhysicalDeviceFragmentShaderInterlockFeaturesExt,
        PNext = pExtendedFeatures,
        FragmentShaderPixelInterlock = true
    };
    pExtendedFeatures = &features;
}
   
private static void SetupRobustness2Features(
    VulkanPhysicalDevice physicalDevice,
    ref void* pExtendedFeatures,
    PhysicalDeviceRobustness2FeaturesEXT supportedFeatures)
{
    const string ExtRobustness2 = "VK_EXT_robustness2";
    if (!physicalDevice.IsDeviceExtensionPresent(ExtRobustness2))
        return;

    var features = new PhysicalDeviceRobustness2FeaturesEXT
    {
        SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
        PNext = pExtendedFeatures,
        NullDescriptor = supportedFeatures.NullDescriptor
    };
    pExtendedFeatures = &features;
}
   
   
private static void SetupTransformFeedbackFeatures(
    VulkanPhysicalDevice physicalDevice,
    ref void* pExtendedFeatures,
    PhysicalDeviceTransformFeedbackFeaturesEXT supportedFeatures)
{
    if (!physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName))
        return;

    var features = new PhysicalDeviceTransformFeedbackFeaturesEXT
    {
        SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
        PNext = pExtendedFeatures,
        TransformFeedback = supportedFeatures.TransformFeedback
    };
    pExtendedFeatures = &features;
}
   
        private static readonly string[] _requiredExtensions = {
            KhrSwapchain.ExtensionName,
        };

        internal static VulkanInstance CreateInstance(Vk api, GraphicsDebugLevel logLevel, string[] requiredExtensions)
        {
            var enabledLayers = new List<string>();

            var instanceExtensions = VulkanInstance.GetInstanceExtensions(api);
            var instanceLayers = VulkanInstance.GetInstanceLayers(api);

            void AddAvailableLayer(string layerName)
            {
                if (instanceLayers.Contains(layerName))
                {
                    enabledLayers.Add(layerName);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Missing layer {layerName}");
                }
            }

            if (logLevel != GraphicsDebugLevel.None)
            {
                AddAvailableLayer("VK_LAYER_KHRONOS_validation");
            }

            var enabledExtensions = requiredExtensions;

            if (instanceExtensions.Contains("VK_EXT_debug_utils"))
            {
                enabledExtensions = enabledExtensions.Append(ExtDebugUtils.ExtensionName).ToArray();
            }

            var appName = Marshal.StringToHGlobalAnsi(AppName);

            var applicationInfo = CreateApplicationInfo(appName);

            IntPtr* ppEnabledExtensions = stackalloc IntPtr[enabledExtensions.Length];
            IntPtr* ppEnabledLayers = stackalloc IntPtr[enabledLayers.Count];

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                ppEnabledExtensions[i] = Marshal.StringToHGlobalAnsi(enabledExtensions[i]);
            }

            for (int i = 0; i < enabledLayers.Count; i++)
            {
                ppEnabledLayers[i] = Marshal.StringToHGlobalAnsi(enabledLayers[i]);
            }

            var instanceCreateInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                PpEnabledExtensionNames = (byte**)ppEnabledExtensions,
                PpEnabledLayerNames = (byte**)ppEnabledLayers,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                EnabledLayerCount = (uint)enabledLayers.Count,
            };

            Result result = VulkanInstance.Create(api, ref instanceCreateInfo, out var instance);

            Marshal.FreeHGlobal(appName);

            for (int i = 0; i < enabledExtensions.Length; i++)
{
    IntPtr ptr = ppEnabledExtensions[i];
    Marshal.FreeHGlobal(ptr);
}

            for (int i = 0; i < enabledLayers.Count; i++)
            {
                Marshal.FreeHGlobal(ppEnabledLayers[i]);
            }

            result.ThrowOnError();

            return instance;
        }

        internal static VulkanPhysicalDevice FindSuitablePhysicalDevice(Vk api, VulkanInstance instance, SurfaceKHR surface, string preferredGpuId)
        {
            instance.EnumeratePhysicalDevices(out var physicalDevices).ThrowOnError();

            // First we try to pick the user preferred GPU.
            for (int i = 0; i < physicalDevices.Length; i++)
            {
                if (IsPreferredAndSuitableDevice(api, physicalDevices[i], surface, preferredGpuId))
                {
                    return physicalDevices[i];
                }
            }

            // If we fail to do that, just use the first compatible GPU.
            for (int i = 0; i < physicalDevices.Length; i++)
            {
                if (IsSuitableDevice(api, physicalDevices[i], surface))
                {
                    return physicalDevices[i];
                }
            }

            throw new VulkanException("Initialization failed, none of the available GPUs meets the minimum requirements.");
        }

        internal static DeviceInfo[] GetSuitablePhysicalDevices(Vk api)
        {
            var appName = Marshal.StringToHGlobalAnsi(AppName);

            var applicationInfo = CreateApplicationInfo(appName);

            var instanceCreateInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                PpEnabledExtensionNames = null,
                PpEnabledLayerNames = null,
                EnabledExtensionCount = 0,
                EnabledLayerCount = 0,
            };

            Result result = VulkanInstance.Create(api, ref instanceCreateInfo, out var rawInstance);

            Marshal.FreeHGlobal(appName);

            result.ThrowOnError();

            using VulkanInstance instance = rawInstance;

            // We currently assume that the instance is compatible with Vulkan 1.2
            // TODO: Remove this once we relax our initialization codepaths.
            if (instance.InstanceVersion < _minimalInstanceVulkanVersion)
            {
                return Array.Empty<DeviceInfo>();
            }

            instance.EnumeratePhysicalDevices(out VulkanPhysicalDevice[] physicalDevices).ThrowOnError();

            List<DeviceInfo> deviceInfos = new();

            foreach (VulkanPhysicalDevice physicalDevice in physicalDevices)
            {
                if (physicalDevice.PhysicalDeviceProperties.ApiVersion < _minimalVulkanVersion)
                {
                    continue;
                }

                deviceInfos.Add(physicalDevice.ToDeviceInfo());
            }

            return deviceInfos.ToArray();
        }

        private static bool IsPreferredAndSuitableDevice(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface, string preferredGpuId)
        {
            if (physicalDevice.Id != preferredGpuId)
            {
                return false;
            }

            return IsSuitableDevice(api, physicalDevice, surface);
        }

        private static bool IsSuitableDevice(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface)
        {
            var supportedExtensions = new HashSet<string>(physicalDevice.DeviceExtensions);
       return _requiredExtensions.All(supportedExtensions.Contains) 
       && FindSuitableQueueFamily(api, physicalDevice, surface, out _) != InvalidIndex;
   }

        internal static uint FindSuitableQueueFamily(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface, out uint queueCount)
        {
            const QueueFlags RequiredFlags = QueueFlags.GraphicsBit | QueueFlags.ComputeBit;

            var khrSurface = new KhrSurface(api.Context);

            for (uint index = 0; index < physicalDevice.QueueFamilyProperties.Length; index++)
            {
                ref QueueFamilyProperties property = ref physicalDevice.QueueFamilyProperties[index];

                khrSurface.GetPhysicalDeviceSurfaceSupport(physicalDevice.PhysicalDevice, index, surface, out var surfaceSupported).ThrowOnError();

                if (property.QueueFlags.HasFlag(RequiredFlags) && surfaceSupported)
                {
                    queueCount = property.QueueCount;

                    return index;
                }
            }

            queueCount = 0;

            return InvalidIndex;
        }

        internal static Device CreateDevice(Vk api, VulkanPhysicalDevice physicalDevice, uint queueFamilyIndex, uint queueCount)
        {
            if (queueCount > QueuesCount)
            {
                queueCount = QueuesCount;
            }

            float* queuePriorities = stackalloc float[(int)queueCount];

            for (int i = 0; i < queueCount; i++)
            {
                queuePriorities[i] = 1f;
            }

            var queueCreateInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                QueueCount = queueCount,
                PQueuePriorities = queuePriorities,
            };

            bool useRobustBufferAccess = VendorUtils.FromId(physicalDevice.PhysicalDeviceProperties.VendorID) == Vendor.Nvidia;

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };

            PhysicalDeviceVulkan11Features supportedFeaturesVk11 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan11Features,
                PNext = features2.PNext,
            };

            features2.PNext = &supportedFeaturesVk11;

            PhysicalDeviceCustomBorderColorFeaturesEXT supportedFeaturesCustomBorderColor = new()
            {
                SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtCustomBorderColor))
            {
                features2.PNext = &supportedFeaturesCustomBorderColor;
            }

            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT supportedFeaturesPrimitiveTopologyListRestart = new()
            {
                SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtPrimitiveTopologyListRestart))
            {
                features2.PNext = &supportedFeaturesPrimitiveTopologyListRestart;
            }

            PhysicalDeviceTransformFeedbackFeaturesEXT supportedFeaturesTransformFeedback = new()
            {
                SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName))
            {
                features2.PNext = &supportedFeaturesTransformFeedback;
            }

            PhysicalDeviceRobustness2FeaturesEXT supportedFeaturesRobustness2 = new()
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtRobustness2))
            {
                supportedFeaturesRobustness2.PNext = features2.PNext;

                features2.PNext = &supportedFeaturesRobustness2;
            }

            PhysicalDeviceDepthClipControlFeaturesEXT supportedFeaturesDepthClipControl = new()
            {
                SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtDepthClipControl))
            {
                features2.PNext = &supportedFeaturesDepthClipControl;
            }

            PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT supportedFeaturesAttachmentFeedbackLoopLayout = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtAttachmentFeedbackLoopLayout))
            {
                features2.PNext = &supportedFeaturesAttachmentFeedbackLoopLayout;
            }

            PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT supportedFeaturesDynamicAttachmentFeedbackLoopLayout = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtAttachmentFeedbackLoopDynamicState))
            {
                features2.PNext = &supportedFeaturesDynamicAttachmentFeedbackLoopLayout;
            }

            PhysicalDeviceVulkan12Features supportedPhysicalDeviceVulkan12Features = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = features2.PNext,
            };

            features2.PNext = &supportedPhysicalDeviceVulkan12Features;

            api.GetPhysicalDeviceFeatures2(physicalDevice.PhysicalDevice, &features2);

            var supportedFeatures = features2.Features;

            var features = new PhysicalDeviceFeatures
            {
                DepthBiasClamp = supportedFeatures.DepthBiasClamp,
                DepthClamp = supportedFeatures.DepthClamp,
                DualSrcBlend = supportedFeatures.DualSrcBlend,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
                GeometryShader = supportedFeatures.GeometryShader,
                ImageCubeArray = supportedFeatures.ImageCubeArray,
                IndependentBlend = supportedFeatures.IndependentBlend,
                LogicOp = supportedFeatures.LogicOp,
                OcclusionQueryPrecise = supportedFeatures.OcclusionQueryPrecise,
                MultiViewport = supportedFeatures.MultiViewport,
                PipelineStatisticsQuery = supportedFeatures.PipelineStatisticsQuery,
                SamplerAnisotropy = supportedFeatures.SamplerAnisotropy,
                ShaderClipDistance = supportedFeatures.ShaderClipDistance,
                ShaderFloat64 = supportedFeatures.ShaderFloat64,
                ShaderImageGatherExtended = supportedFeatures.ShaderImageGatherExtended,
                ShaderStorageImageMultisample = supportedFeatures.ShaderStorageImageMultisample,
                ShaderStorageImageReadWithoutFormat = supportedFeatures.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat,
                TessellationShader = supportedFeatures.TessellationShader,
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                RobustBufferAccess = useRobustBufferAccess,
                SampleRateShading = supportedFeatures.SampleRateShading,
            };

            void* pExtendedFeatures = null;

            PhysicalDeviceTransformFeedbackFeaturesEXT featuresTransformFeedback;

            SetupTransformFeedbackFeatures(physicalDevice, ref pExtendedFeatures, supportedFeaturesTransformFeedback);

            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT featuresPrimitiveTopologyListRestart;

            SetupPrimitiveTopologyListRestartFeatures(physicalDevice, ref pExtendedFeatures, supportedFeaturesPrimitiveTopologyListRestart);

            SetupRobustness2Features(physicalDevice, ref pExtendedFeatures, supportedFeaturesRobustness2);

            var featuresExtendedDynamicState = new PhysicalDeviceExtendedDynamicStateFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicStateFeaturesExt,
                PNext = pExtendedFeatures,
                ExtendedDynamicState = physicalDevice.IsDeviceExtensionPresent(ExtExtendedDynamicState.ExtensionName),
            };

            pExtendedFeatures = &featuresExtendedDynamicState;

            var featuresVk11 = new PhysicalDeviceVulkan11Features
            {
                SType = StructureType.PhysicalDeviceVulkan11Features,
                PNext = pExtendedFeatures,
                ShaderDrawParameters = supportedFeaturesVk11.ShaderDrawParameters,
            };

            pExtendedFeatures = &featuresVk11;

            var featuresVk12 = new PhysicalDeviceVulkan12Features
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = pExtendedFeatures,
                DescriptorIndexing = supportedPhysicalDeviceVulkan12Features.DescriptorIndexing,
                DrawIndirectCount = supportedPhysicalDeviceVulkan12Features.DrawIndirectCount,
                UniformBufferStandardLayout = supportedPhysicalDeviceVulkan12Features.UniformBufferStandardLayout,
                UniformAndStorageBuffer8BitAccess = supportedPhysicalDeviceVulkan12Features.UniformAndStorageBuffer8BitAccess,
                StorageBuffer8BitAccess = supportedPhysicalDeviceVulkan12Features.StorageBuffer8BitAccess,
            };

            pExtendedFeatures = &featuresVk12;

            PhysicalDeviceIndexTypeUint8FeaturesEXT featuresIndexU8;

            if (physicalDevice.IsDeviceExtensionPresent(ExtIndexTypeUint8))
            {
                featuresIndexU8 = new PhysicalDeviceIndexTypeUint8FeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
                    PNext = pExtendedFeatures,
                    IndexTypeUint8 = true,
                };

                pExtendedFeatures = &featuresIndexU8;
            }

            SetupFragmentShaderInterlockFeatures(physicalDevice, ref pExtendedFeatures);

            SetupCustomBorderColorFeatures(physicalDevice, ref pExtendedFeatures, supportedFeaturesCustomBorderColor);

            SetupDepthClipControlFeatures(physicalDevice, ref pExtendedFeatures, supportedFeaturesDepthClipControl);

 SetupExternalMemoryHostFeatures(physicalDevice, ref pExtendedFeatures); // 新增
           SetupAttachmentFeedbackLoopFeatures(physicalDevice, ref pExtendedFeatures, supportedFeaturesAttachmentFeedbackLoopLayout);

            
            SetupAttachmentFeedbackLoopDynamicStateFeatures(
    physicalDevice,
    ref pExtendedFeatures,
    supportedFeaturesDynamicAttachmentFeedbackLoopLayout);

            var enabledExtensions = _requiredExtensions.Union(_desirableExtensions.Intersect(physicalDevice.DeviceExtensions)).ToArray();

            IntPtr* ppEnabledExtensions = stackalloc IntPtr[enabledExtensions.Length];

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                ppEnabledExtensions[i] = Marshal.StringToHGlobalAnsi(enabledExtensions[i]);
            }

            var deviceCreateInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = pExtendedFeatures,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo,
                PpEnabledExtensionNames = (byte**)ppEnabledExtensions,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                PEnabledFeatures = &features,
            };

            api.CreateDevice(physicalDevice.PhysicalDevice, in deviceCreateInfo, null, out var device).ThrowOnError();

            for (int i = 0; i < enabledExtensions.Length; i++)
{
    IntPtr ptr = ppEnabledExtensions[i];
    Marshal.FreeHGlobal(ptr);
}

            return device;
        }
    }
}
