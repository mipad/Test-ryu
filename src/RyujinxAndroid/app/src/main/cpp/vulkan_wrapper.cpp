// Copyright 2016 Google Inc. All Rights Reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// This file is generated.
#include "vulkan_wrapper.h"
#include <dlfcn.h>
#include <string.h>

// Global library handle
static void* s_libvulkan = nullptr;

// Helper macro to safely load function pointers
#define LOAD_GLOBAL_FUNC(name) \
    vk##name = reinterpret_cast<PFN_vk##name>(dlsym(s_libvulkan, "vk" #name)); \
    if (!vk##name) { \
        vk##name = reinterpret_cast<PFN_vk##name>(dlsym(s_libvulkan, "vk" #name "KHR")); \
    }

#define LOAD_INSTANCE_FUNC(instance, name) \
    vk##name = reinterpret_cast<PFN_vk##name>(vkGetInstanceProcAddr(instance, "vk" #name)); \
    if (!vk##name) { \
        vk##name = reinterpret_cast<PFN_vk##name>(vkGetInstanceProcAddr(instance, "vk" #name "KHR")); \
    } \
    if (!vk##name) { \
        vk##name = reinterpret_cast<PFN_vk##name>(vkGetInstanceProcAddr(instance, "vk" #name "EXT")); \
    }

#define LOAD_DEVICE_FUNC(device, name) \
    vk##name = reinterpret_cast<PFN_vk##name>(vkGetDeviceProcAddr(device, "vk" #name)); \
    if (!vk##name) { \
        vk##name = reinterpret_cast<PFN_vk##name>(vkGetDeviceProcAddr(device, "vk" #name "KHR")); \
    } \
    if (!vk##name) { \
        vk##name = reinterpret_cast<PFN_vk##name>(vkGetDeviceProcAddr(device, "vk" #name "EXT")); \
    }

int InitVulkan(void) {
    // Try to load libvulkan.so.1 first, then fall back to libvulkan.so
    s_libvulkan = dlopen("libvulkan.so.1", RTLD_NOW | RTLD_LOCAL);
    if (!s_libvulkan) {
        s_libvulkan = dlopen("libvulkan.so", RTLD_NOW | RTLD_LOCAL);
    }
    
    if (!s_libvulkan) {
        return 0;
    }

    // Load global functions (available without instance)
    LOAD_GLOBAL_FUNC(GetInstanceProcAddr);
    LOAD_GLOBAL_FUNC(EnumerateInstanceVersion);
    LOAD_GLOBAL_FUNC(CreateInstance);
    LOAD_GLOBAL_FUNC(EnumerateInstanceExtensionProperties);
    LOAD_GLOBAL_FUNC(EnumerateInstanceLayerProperties);

    // Check if we successfully loaded the essential functions
    if (!vkGetInstanceProcAddr || !vkCreateInstance) {
        dlclose(s_libvulkan);
        s_libvulkan = nullptr;
        return 0;
    }

    return 1;
}

int LoadInstanceFunctions(VkInstance instance) {
    if (!instance || !s_libvulkan) {
        return 0;
    }

    // Load core instance functions
    LOAD_INSTANCE_FUNC(instance, DestroyInstance);
    LOAD_INSTANCE_FUNC(instance, EnumeratePhysicalDevices);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceFeatures);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceFeatures2);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceFormatProperties);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceImageFormatProperties);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceProperties);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceProperties2);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceQueueFamilyProperties);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceMemoryProperties);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceMemoryProperties2);
    LOAD_INSTANCE_FUNC(instance, GetDeviceProcAddr);
    LOAD_INSTANCE_FUNC(instance, CreateDevice);
    LOAD_INSTANCE_FUNC(instance, EnumerateDeviceExtensionProperties);
    LOAD_INSTANCE_FUNC(instance, EnumerateDeviceLayerProperties);

    // Load surface extension functions
    LOAD_INSTANCE_FUNC(instance, DestroySurfaceKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceSurfaceSupportKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceSurfaceCapabilitiesKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceSurfaceFormatsKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceSurfacePresentModesKHR);

    // Load display extension functions
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceDisplayPropertiesKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceDisplayPlanePropertiesKHR);
    LOAD_INSTANCE_FUNC(instance, GetDisplayPlaneSupportedDisplaysKHR);
    LOAD_INSTANCE_FUNC(instance, GetDisplayModePropertiesKHR);
    LOAD_INSTANCE_FUNC(instance, CreateDisplayModeKHR);
    LOAD_INSTANCE_FUNC(instance, GetDisplayPlaneCapabilitiesKHR);
    LOAD_INSTANCE_FUNC(instance, CreateDisplayPlaneSurfaceKHR);

    // Load debug extension functions
    LOAD_INSTANCE_FUNC(instance, CreateDebugUtilsMessengerEXT);
    LOAD_INSTANCE_FUNC(instance, DestroyDebugUtilsMessengerEXT);
    LOAD_INSTANCE_FUNC(instance, SubmitDebugUtilsMessageEXT);
    LOAD_INSTANCE_FUNC(instance, SetDebugUtilsObjectNameEXT);
    LOAD_INSTANCE_FUNC(instance, SetDebugUtilsObjectTagEXT);
    LOAD_INSTANCE_FUNC(instance, QueueBeginDebugUtilsLabelEXT);
    LOAD_INSTANCE_FUNC(instance, QueueEndDebugUtilsLabelEXT);
    LOAD_INSTANCE_FUNC(instance, QueueInsertDebugUtilsLabelEXT);

    // Legacy debug report functions
    LOAD_INSTANCE_FUNC(instance, CreateDebugReportCallbackEXT);
    LOAD_INSTANCE_FUNC(instance, DestroyDebugReportCallbackEXT);
    LOAD_INSTANCE_FUNC(instance, DebugReportMessageEXT);

    // Platform-specific surface functions
#ifdef VK_USE_PLATFORM_XLIB_KHR
    LOAD_INSTANCE_FUNC(instance, CreateXlibSurfaceKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceXlibPresentationSupportKHR);
#endif

#ifdef VK_USE_PLATFORM_XCB_KHR
    LOAD_INSTANCE_FUNC(instance, CreateXcbSurfaceKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceXcbPresentationSupportKHR);
#endif

#ifdef VK_USE_PLATFORM_WAYLAND_KHR
    LOAD_INSTANCE_FUNC(instance, CreateWaylandSurfaceKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceWaylandPresentationSupportKHR);
#endif

#ifdef VK_USE_PLATFORM_MIR_KHR
    LOAD_INSTANCE_FUNC(instance, CreateMirSurfaceKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceMirPresentationSupportKHR);
#endif

#ifdef VK_USE_PLATFORM_ANDROID_KHR
    LOAD_INSTANCE_FUNC(instance, CreateAndroidSurfaceKHR);
#endif

#ifdef VK_USE_PLATFORM_WIN32_KHR
    LOAD_INSTANCE_FUNC(instance, CreateWin32SurfaceKHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceWin32PresentationSupportKHR);
#endif

#ifdef VK_USE_PLATFORM_METAL_EXT
    LOAD_INSTANCE_FUNC(instance, CreateMetalSurfaceEXT);
#endif

    // Load device properties2 functions
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceFeatures2KHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceProperties2KHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceFormatProperties2KHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceImageFormatProperties2KHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceQueueFamilyProperties2KHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceMemoryProperties2KHR);
    LOAD_INSTANCE_FUNC(instance, GetPhysicalDeviceSparseImageFormatProperties2KHR);

    return 1;
}

int LoadDeviceFunctions(VkDevice device) {
    if (!device) {
        return 0;
    }

    // Load core device functions
    LOAD_DEVICE_FUNC(device, DestroyDevice);
    LOAD_DEVICE_FUNC(device, GetDeviceQueue);
    LOAD_DEVICE_FUNC(device, QueueSubmit);
    LOAD_DEVICE_FUNC(device, QueueWaitIdle);
    LOAD_DEVICE_FUNC(device, DeviceWaitIdle);
    LOAD_DEVICE_FUNC(device, AllocateMemory);
    LOAD_DEVICE_FUNC(device, FreeMemory);
    LOAD_DEVICE_FUNC(device, MapMemory);
    LOAD_DEVICE_FUNC(device, UnmapMemory);
    LOAD_DEVICE_FUNC(device, FlushMappedMemoryRanges);
    LOAD_DEVICE_FUNC(device, InvalidateMappedMemoryRanges);
    LOAD_DEVICE_FUNC(device, GetDeviceMemoryCommitment);
    LOAD_DEVICE_FUNC(device, BindBufferMemory);
    LOAD_DEVICE_FUNC(device, BindImageMemory);
    LOAD_DEVICE_FUNC(device, GetBufferMemoryRequirements);
    LOAD_DEVICE_FUNC(device, GetBufferMemoryRequirements2);
    LOAD_DEVICE_FUNC(device, GetImageMemoryRequirements);
    LOAD_DEVICE_FUNC(device, GetImageMemoryRequirements2);
    LOAD_DEVICE_FUNC(device, GetImageSparseMemoryRequirements);
    LOAD_DEVICE_FUNC(device, GetPhysicalDeviceSparseImageFormatProperties);
    LOAD_DEVICE_FUNC(device, QueueBindSparse);
    LOAD_DEVICE_FUNC(device, CreateFence);
    LOAD_DEVICE_FUNC(device, DestroyFence);
    LOAD_DEVICE_FUNC(device, ResetFences);
    LOAD_DEVICE_FUNC(device, GetFenceStatus);
    LOAD_DEVICE_FUNC(device, WaitForFences);
    LOAD_DEVICE_FUNC(device, CreateSemaphore);
    LOAD_DEVICE_FUNC(device, DestroySemaphore);
    LOAD_DEVICE_FUNC(device, GetSemaphoreCounterValue);
    LOAD_DEVICE_FUNC(device, WaitSemaphores);
    LOAD_DEVICE_FUNC(device, SignalSemaphore);
    LOAD_DEVICE_FUNC(device, CreateEvent);
    LOAD_DEVICE_FUNC(device, DestroyEvent);
    LOAD_DEVICE_FUNC(device, GetEventStatus);
    LOAD_DEVICE_FUNC(device, SetEvent);
    LOAD_DEVICE_FUNC(device, ResetEvent);
    LOAD_DEVICE_FUNC(device, CreateQueryPool);
    LOAD_DEVICE_FUNC(device, DestroyQueryPool);
    LOAD_DEVICE_FUNC(device, GetQueryPoolResults);
    LOAD_DEVICE_FUNC(device, ResetQueryPool);
    LOAD_DEVICE_FUNC(device, CreateBuffer);
    LOAD_DEVICE_FUNC(device, DestroyBuffer);
    LOAD_DEVICE_FUNC(device, CreateBufferView);
    LOAD_DEVICE_FUNC(device, DestroyBufferView);
    LOAD_DEVICE_FUNC(device, CreateImage);
    LOAD_DEVICE_FUNC(device, DestroyImage);
    LOAD_DEVICE_FUNC(device, GetImageSubresourceLayout);
    LOAD_DEVICE_FUNC(device, CreateImageView);
    LOAD_DEVICE_FUNC(device, DestroyImageView);
    LOAD_DEVICE_FUNC(device, CreateShaderModule);
    LOAD_DEVICE_FUNC(device, DestroyShaderModule);
    LOAD_DEVICE_FUNC(device, CreatePipelineCache);
    LOAD_DEVICE_FUNC(device, DestroyPipelineCache);
    LOAD_DEVICE_FUNC(device, GetPipelineCacheData);
    LOAD_DEVICE_FUNC(device, MergePipelineCaches);
    LOAD_DEVICE_FUNC(device, CreateGraphicsPipelines);
    LOAD_DEVICE_FUNC(device, CreateComputePipelines);
    LOAD_DEVICE_FUNC(device, DestroyPipeline);
    LOAD_DEVICE_FUNC(device, CreatePipelineLayout);
    LOAD_DEVICE_FUNC(device, DestroyPipelineLayout);
    LOAD_DEVICE_FUNC(device, CreateSampler);
    LOAD_DEVICE_FUNC(device, DestroySampler);
    LOAD_DEVICE_FUNC(device, CreateDescriptorSetLayout);
    LOAD_DEVICE_FUNC(device, DestroyDescriptorSetLayout);
    LOAD_DEVICE_FUNC(device, CreateDescriptorPool);
    LOAD_DEVICE_FUNC(device, DestroyDescriptorPool);
    LOAD_DEVICE_FUNC(device, ResetDescriptorPool);
    LOAD_DEVICE_FUNC(device, AllocateDescriptorSets);
    LOAD_DEVICE_FUNC(device, FreeDescriptorSets);
    LOAD_DEVICE_FUNC(device, UpdateDescriptorSets);
    LOAD_DEVICE_FUNC(device, CreateFramebuffer);
    LOAD_DEVICE_FUNC(device, DestroyFramebuffer);
    LOAD_DEVICE_FUNC(device, CreateRenderPass);
    LOAD_DEVICE_FUNC(device, CreateRenderPass2);
    LOAD_DEVICE_FUNC(device, DestroyRenderPass);
    LOAD_DEVICE_FUNC(device, GetRenderAreaGranularity);
    LOAD_DEVICE_FUNC(device, CreateCommandPool);
    LOAD_DEVICE_FUNC(device, DestroyCommandPool);
    LOAD_DEVICE_FUNC(device, ResetCommandPool);
    LOAD_DEVICE_FUNC(device, AllocateCommandBuffers);
    LOAD_DEVICE_FUNC(device, FreeCommandBuffers);
    LOAD_DEVICE_FUNC(device, BeginCommandBuffer);
    LOAD_DEVICE_FUNC(device, EndCommandBuffer);
    LOAD_DEVICE_FUNC(device, ResetCommandBuffer);

    // Load command buffer functions
    LOAD_DEVICE_FUNC(device, CmdBindPipeline);
    LOAD_DEVICE_FUNC(device, CmdSetViewport);
    LOAD_DEVICE_FUNC(device, CmdSetScissor);
    LOAD_DEVICE_FUNC(device, CmdSetLineWidth);
    LOAD_DEVICE_FUNC(device, CmdSetDepthBias);
    LOAD_DEVICE_FUNC(device, CmdSetBlendConstants);
    LOAD_DEVICE_FUNC(device, CmdSetDepthBounds);
    LOAD_DEVICE_FUNC(device, CmdSetStencilCompareMask);
    LOAD_DEVICE_FUNC(device, CmdSetStencilWriteMask);
    LOAD_DEVICE_FUNC(device, CmdSetStencilReference);
    LOAD_DEVICE_FUNC(device, CmdBindDescriptorSets);
    LOAD_DEVICE_FUNC(device, CmdBindIndexBuffer);
    LOAD_DEVICE_FUNC(device, CmdBindVertexBuffers);
    LOAD_DEVICE_FUNC(device, CmdBindVertexBuffers2);
    LOAD_DEVICE_FUNC(device, CmdDraw);
    LOAD_DEVICE_FUNC(device, CmdDrawIndexed);
    LOAD_DEVICE_FUNC(device, CmdDrawIndirect);
    LOAD_DEVICE_FUNC(device, CmdDrawIndexedIndirect);
    LOAD_DEVICE_FUNC(device, CmdDrawIndirectCount);
    LOAD_DEVICE_FUNC(device, CmdDrawIndexedIndirectCount);
    LOAD_DEVICE_FUNC(device, CmdDispatch);
    LOAD_DEVICE_FUNC(device, CmdDispatchIndirect);
    LOAD_DEVICE_FUNC(device, CmdCopyBuffer);
    LOAD_DEVICE_FUNC(device, CmdCopyImage);
    LOAD_DEVICE_FUNC(device, CmdBlitImage);
    LOAD_DEVICE_FUNC(device, CmdCopyBufferToImage);
    LOAD_DEVICE_FUNC(device, CmdCopyImageToBuffer);
    LOAD_DEVICE_FUNC(device, CmdUpdateBuffer);
    LOAD_DEVICE_FUNC(device, CmdFillBuffer);
    LOAD_DEVICE_FUNC(device, CmdClearColorImage);
    LOAD_DEVICE_FUNC(device, CmdClearDepthStencilImage);
    LOAD_DEVICE_FUNC(device, CmdClearAttachments);
    LOAD_DEVICE_FUNC(device, CmdResolveImage);
    LOAD_DEVICE_FUNC(device, CmdSetEvent);
    LOAD_DEVICE_FUNC(device, CmdResetEvent);
    LOAD_DEVICE_FUNC(device, CmdWaitEvents);
    LOAD_DEVICE_FUNC(device, CmdPipelineBarrier);
    LOAD_DEVICE_FUNC(device, CmdBeginQuery);
    LOAD_DEVICE_FUNC(device, CmdEndQuery);
    LOAD_DEVICE_FUNC(device, CmdResetQueryPool);
    LOAD_DEVICE_FUNC(device, CmdWriteTimestamp);
    LOAD_DEVICE_FUNC(device, CmdCopyQueryPoolResults);
    LOAD_DEVICE_FUNC(device, CmdPushConstants);
    LOAD_DEVICE_FUNC(device, CmdBeginRenderPass);
    LOAD_DEVICE_FUNC(device, CmdBeginRenderPass2);
    LOAD_DEVICE_FUNC(device, CmdNextSubpass);
    LOAD_DEVICE_FUNC(device, CmdNextSubpass2);
    LOAD_DEVICE_FUNC(device, CmdEndRenderPass);
    LOAD_DEVICE_FUNC(device, CmdEndRenderPass2);
    LOAD_DEVICE_FUNC(device, CmdExecuteCommands);

    // Load swapchain functions
    LOAD_DEVICE_FUNC(device, CreateSwapchainKHR);
    LOAD_DEVICE_FUNC(device, DestroySwapchainKHR);
    LOAD_DEVICE_FUNC(device, GetSwapchainImagesKHR);
    LOAD_DEVICE_FUNC(device, AcquireNextImageKHR);
    LOAD_DEVICE_FUNC(device, QueuePresentKHR);

    // Load display swapchain functions
    LOAD_DEVICE_FUNC(device, CreateSharedSwapchainsKHR);

    // Load debug utils command functions
    LOAD_DEVICE_FUNC(device, CmdBeginDebugUtilsLabelEXT);
    LOAD_DEVICE_FUNC(device, CmdEndDebugUtilsLabelEXT);
    LOAD_DEVICE_FUNC(device, CmdInsertDebugUtilsLabelEXT);

    // Load maintenance functions
    LOAD_DEVICE_FUNC(device, TrimCommandPoolKHR);
    LOAD_DEVICE_FUNC(device, GetDescriptorSetLayoutSupportKHR);

    // Load bind memory2 functions
    LOAD_DEVICE_FUNC(device, BindBufferMemory2KHR);
    LOAD_DEVICE_FUNC(device, BindImageMemory2KHR);

    // Load renderpass2 functions
    LOAD_DEVICE_FUNC(device, CreateRenderPass2KHR);
    LOAD_DEVICE_FUNC(device, CmdBeginRenderPass2KHR);
    LOAD_DEVICE_FUNC(device, CmdNextSubpass2KHR);
    LOAD_DEVICE_FUNC(device, CmdEndRenderPass2KHR);

    // Load timeline semaphore functions
    LOAD_DEVICE_FUNC(device, GetSemaphoreCounterValueKHR);
    LOAD_DEVICE_FUNC(device, WaitSemaphoresKHR);
    LOAD_DEVICE_FUNC(device, SignalSemaphoreKHR);

    // Load host query reset functions
    LOAD_DEVICE_FUNC(device, ResetQueryPoolEXT);

    // Load buffer device address functions
    LOAD_DEVICE_FUNC(device, GetBufferDeviceAddressKHR);
    LOAD_DEVICE_FUNC(device, GetBufferOpaqueCaptureAddressKHR);
    LOAD_DEVICE_FUNC(device, GetDeviceMemoryOpaqueCaptureAddressKHR);

    // Load deferred host operations functions
    LOAD_DEVICE_FUNC(device, CreateDeferredOperationKHR);
    LOAD_DEVICE_FUNC(device, DestroyDeferredOperationKHR);
    LOAD_DEVICE_FUNC(device, GetDeferredOperationMaxConcurrencyKHR);
    LOAD_DEVICE_FUNC(device, GetDeferredOperationResultKHR);
    LOAD_DEVICE_FUNC(device, DeferredOperationJoinKHR);

    // Load pipeline executable properties functions
    LOAD_DEVICE_FUNC(device, GetPipelineExecutablePropertiesKHR);
    LOAD_DEVICE_FUNC(device, GetPipelineExecutableStatisticsKHR);
    LOAD_DEVICE_FUNC(device, GetPipelineExecutableInternalRepresentationsKHR);

    // Load synchronization2 functions
    LOAD_DEVICE_FUNC(device, CmdSetEvent2KHR);
    LOAD_DEVICE_FUNC(device, CmdResetEvent2KHR);
    LOAD_DEVICE_FUNC(device, CmdWaitEvents2KHR);
    LOAD_DEVICE_FUNC(device, CmdPipelineBarrier2KHR);
    LOAD_DEVICE_FUNC(device, CmdWriteTimestamp2KHR);
    LOAD_DEVICE_FUNC(device, QueueSubmit2KHR);
    LOAD_DEVICE_FUNC(device, CmdWriteBufferMarker2AMD);
    LOAD_DEVICE_FUNC(device, GetQueueCheckpointData2NV);

    // Load copy commands2 functions
    LOAD_DEVICE_FUNC(device, CmdCopyBuffer2KHR);
    LOAD_DEVICE_FUNC(device, CmdCopyImage2KHR);
    LOAD_DEVICE_FUNC(device, CmdCopyBufferToImage2KHR);
    LOAD_DEVICE_FUNC(device, CmdCopyImageToBuffer2KHR);
    LOAD_DEVICE_FUNC(device, CmdBlitImage2KHR);
    LOAD_DEVICE_FUNC(device, CmdResolveImage2KHR);

    // Load conditional rendering functions
    LOAD_DEVICE_FUNC(device, CmdBeginConditionalRenderingEXT);
    LOAD_DEVICE_FUNC(device, CmdEndConditionalRenderingEXT);

    // Load transform feedback functions
    LOAD_DEVICE_FUNC(device, CmdBindTransformFeedbackBuffersEXT);
    LOAD_DEVICE_FUNC(device, CmdBeginTransformFeedbackEXT);
    LOAD_DEVICE_FUNC(device, CmdEndTransformFeedbackEXT);
    LOAD_DEVICE_FUNC(device, CmdBeginQueryIndexedEXT);
    LOAD_DEVICE_FUNC(device, CmdEndQueryIndexedEXT);
    LOAD_DEVICE_FUNC(device, CmdDrawIndirectByteCountEXT);

    return 1;
}

// Initialize all function pointers to nullptr

// Global functions
PFN_vkGetInstanceProcAddr vkGetInstanceProcAddr = nullptr;
PFN_vkEnumerateInstanceVersion vkEnumerateInstanceVersion = nullptr;
PFN_vkCreateInstance vkCreateInstance = nullptr;
PFN_vkEnumerateInstanceExtensionProperties vkEnumerateInstanceExtensionProperties = nullptr;
PFN_vkEnumerateInstanceLayerProperties vkEnumerateInstanceLayerProperties = nullptr;

// Instance-level functions
PFN_vkDestroyInstance vkDestroyInstance = nullptr;
PFN_vkEnumeratePhysicalDevices vkEnumeratePhysicalDevices = nullptr;
PFN_vkGetPhysicalDeviceFeatures vkGetPhysicalDeviceFeatures = nullptr;
PFN_vkGetPhysicalDeviceFeatures2 vkGetPhysicalDeviceFeatures2 = nullptr;
PFN_vkGetPhysicalDeviceFormatProperties vkGetPhysicalDeviceFormatProperties = nullptr;
PFN_vkGetPhysicalDeviceImageFormatProperties vkGetPhysicalDeviceImageFormatProperties = nullptr;
PFN_vkGetPhysicalDeviceProperties vkGetPhysicalDeviceProperties = nullptr;
PFN_vkGetPhysicalDeviceProperties2 vkGetPhysicalDeviceProperties2 = nullptr;
PFN_vkGetPhysicalDeviceQueueFamilyProperties vkGetPhysicalDeviceQueueFamilyProperties = nullptr;
PFN_vkGetPhysicalDeviceMemoryProperties vkGetPhysicalDeviceMemoryProperties = nullptr;
PFN_vkGetPhysicalDeviceMemoryProperties2 vkGetPhysicalDeviceMemoryProperties2 = nullptr;
PFN_vkGetDeviceProcAddr vkGetDeviceProcAddr = nullptr;
PFN_vkCreateDevice vkCreateDevice = nullptr;
PFN_vkEnumerateDeviceExtensionProperties vkEnumerateDeviceExtensionProperties = nullptr;
PFN_vkEnumerateDeviceLayerProperties vkEnumerateDeviceLayerProperties = nullptr;

// Device-level functions
PFN_vkDestroyDevice vkDestroyDevice = nullptr;
PFN_vkGetDeviceQueue vkGetDeviceQueue = nullptr;
PFN_vkQueueSubmit vkQueueSubmit = nullptr;
PFN_vkQueueWaitIdle vkQueueWaitIdle = nullptr;
PFN_vkDeviceWaitIdle vkDeviceWaitIdle = nullptr;
PFN_vkAllocateMemory vkAllocateMemory = nullptr;
PFN_vkFreeMemory vkFreeMemory = nullptr;
PFN_vkMapMemory vkMapMemory = nullptr;
PFN_vkUnmapMemory vkUnmapMemory = nullptr;
PFN_vkFlushMappedMemoryRanges vkFlushMappedMemoryRanges = nullptr;
PFN_vkInvalidateMappedMemoryRanges vkInvalidateMappedMemoryRanges = nullptr;
PFN_vkGetDeviceMemoryCommitment vkGetDeviceMemoryCommitment = nullptr;
PFN_vkBindBufferMemory vkBindBufferMemory = nullptr;
PFN_vkBindImageMemory vkBindImageMemory = nullptr;
PFN_vkGetBufferMemoryRequirements vkGetBufferMemoryRequirements = nullptr;
PFN_vkGetBufferMemoryRequirements2 vkGetBufferMemoryRequirements2 = nullptr;
PFN_vkGetImageMemoryRequirements vkGetImageMemoryRequirements = nullptr;
PFN_vkGetImageMemoryRequirements2 vkGetImageMemoryRequirements2 = nullptr;
PFN_vkGetImageSparseMemoryRequirements vkGetImageSparseMemoryRequirements = nullptr;
PFN_vkGetPhysicalDeviceSparseImageFormatProperties vkGetPhysicalDeviceSparseImageFormatProperties = nullptr;
PFN_vkQueueBindSparse vkQueueBindSparse = nullptr;
PFN_vkCreateFence vkCreateFence = nullptr;
PFN_vkDestroyFence vkDestroyFence = nullptr;
PFN_vkResetFences vkResetFences = nullptr;
PFN_vkGetFenceStatus vkGetFenceStatus = nullptr;
PFN_vkWaitForFences vkWaitForFences = nullptr;
PFN_vkCreateSemaphore vkCreateSemaphore = nullptr;
PFN_vkDestroySemaphore vkDestroySemaphore = nullptr;
PFN_vkGetSemaphoreCounterValue vkGetSemaphoreCounterValue = nullptr;
PFN_vkWaitSemaphores vkWaitSemaphores = nullptr;
PFN_vkSignalSemaphore vkSignalSemaphore = nullptr;
PFN_vkCreateEvent vkCreateEvent = nullptr;
PFN_vkDestroyEvent vkDestroyEvent = nullptr;
PFN_vkGetEventStatus vkGetEventStatus = nullptr;
PFN_vkSetEvent vkSetEvent = nullptr;
PFN_vkResetEvent vkResetEvent = nullptr;
PFN_vkCreateQueryPool vkCreateQueryPool = nullptr;
PFN_vkDestroyQueryPool vkDestroyQueryPool = nullptr;
PFN_vkGetQueryPoolResults vkGetQueryPoolResults = nullptr;
PFN_vkResetQueryPool vkResetQueryPool = nullptr;
PFN_vkCreateBuffer vkCreateBuffer = nullptr;
PFN_vkDestroyBuffer vkDestroyBuffer = nullptr;
PFN_vkCreateBufferView vkCreateBufferView = nullptr;
PFN_vkDestroyBufferView vkDestroyBufferView = nullptr;
PFN_vkCreateImage vkCreateImage = nullptr;
PFN_vkDestroyImage vkDestroyImage = nullptr;
PFN_vkGetImageSubresourceLayout vkGetImageSubresourceLayout = nullptr;
PFN_vkCreateImageView vkCreateImageView = nullptr;
PFN_vkDestroyImageView vkDestroyImageView = nullptr;
PFN_vkCreateShaderModule vkCreateShaderModule = nullptr;
PFN_vkDestroyShaderModule vkDestroyShaderModule = nullptr;
PFN_vkCreatePipelineCache vkCreatePipelineCache = nullptr;
PFN_vkDestroyPipelineCache vkDestroyPipelineCache = nullptr;
PFN_vkGetPipelineCacheData vkGetPipelineCacheData = nullptr;
PFN_vkMergePipelineCaches vkMergePipelineCaches = nullptr;
PFN_vkCreateGraphicsPipelines vkCreateGraphicsPipelines = nullptr;
PFN_vkCreateComputePipelines vkCreateComputePipelines = nullptr;
PFN_vkDestroyPipeline vkDestroyPipeline = nullptr;
PFN_vkCreatePipelineLayout vkCreatePipelineLayout = nullptr;
PFN_vkDestroyPipelineLayout vkDestroyPipelineLayout = nullptr;
PFN_vkCreateSampler vkCreateSampler = nullptr;
PFN_vkDestroySampler vkDestroySampler = nullptr;
PFN_vkCreateDescriptorSetLayout vkCreateDescriptorSetLayout = nullptr;
PFN_vkDestroyDescriptorSetLayout vkDestroyDescriptorSetLayout = nullptr;
PFN_vkCreateDescriptorPool vkCreateDescriptorPool = nullptr;
PFN_vkDestroyDescriptorPool vkDestroyDescriptorPool = nullptr;
PFN_vkResetDescriptorPool vkResetDescriptorPool = nullptr;
PFN_vkAllocateDescriptorSets vkAllocateDescriptorSets = nullptr;
PFN_vkFreeDescriptorSets vkFreeDescriptorSets = nullptr;
PFN_vkUpdateDescriptorSets vkUpdateDescriptorSets = nullptr;
PFN_vkCreateFramebuffer vkCreateFramebuffer = nullptr;
PFN_vkDestroyFramebuffer vkDestroyFramebuffer = nullptr;
PFN_vkCreateRenderPass vkCreateRenderPass = nullptr;
PFN_vkCreateRenderPass2 vkCreateRenderPass2 = nullptr;
PFN_vkDestroyRenderPass vkDestroyRenderPass = nullptr;
PFN_vkGetRenderAreaGranularity vkGetRenderAreaGranularity = nullptr;
PFN_vkCreateCommandPool vkCreateCommandPool = nullptr;
PFN_vkDestroyCommandPool vkDestroyCommandPool = nullptr;
PFN_vkResetCommandPool vkResetCommandPool = nullptr;
PFN_vkAllocateCommandBuffers vkAllocateCommandBuffers = nullptr;
PFN_vkFreeCommandBuffers vkFreeCommandBuffers = nullptr;
PFN_vkBeginCommandBuffer vkBeginCommandBuffer = nullptr;
PFN_vkEndCommandBuffer vkEndCommandBuffer = nullptr;
PFN_vkResetCommandBuffer vkResetCommandBuffer = nullptr;
PFN_vkCmdBindPipeline vkCmdBindPipeline = nullptr;
PFN_vkCmdSetViewport vkCmdSetViewport = nullptr;
PFN_vkCmdSetScissor vkCmdSetScissor = nullptr;
PFN_vkCmdSetLineWidth vkCmdSetLineWidth = nullptr;
PFN_vkCmdSetDepthBias vkCmdSetDepthBias = nullptr;
PFN_vkCmdSetBlendConstants vkCmdSetBlendConstants = nullptr;
PFN_vkCmdSetDepthBounds vkCmdSetDepthBounds = nullptr;
PFN_vkCmdSetStencilCompareMask vkCmdSetStencilCompareMask = nullptr;
PFN_vkCmdSetStencilWriteMask vkCmdSetStencilWriteMask = nullptr;
PFN_vkCmdSetStencilReference vkCmdSetStencilReference = nullptr;
PFN_vkCmdBindDescriptorSets vkCmdBindDescriptorSets = nullptr;
PFN_vkCmdBindIndexBuffer vkCmdBindIndexBuffer = nullptr;
PFN_vkCmdBindVertexBuffers vkCmdBindVertexBuffers = nullptr;
PFN_vkCmdBindVertexBuffers2 vkCmdBindVertexBuffers2 = nullptr;
PFN_vkCmdDraw vkCmdDraw = nullptr;
PFN_vkCmdDrawIndexed vkCmdDrawIndexed = nullptr;
PFN_vkCmdDrawIndirect vkCmdDrawIndirect = nullptr;
PFN_vkCmdDrawIndexedIndirect vkCmdDrawIndexedIndirect = nullptr;
PFN_vkCmdDrawIndirectCount vkCmdDrawIndirectCount = nullptr;
PFN_vkCmdDrawIndexedIndirectCount vkCmdDrawIndexedIndirectCount = nullptr;
PFN_vkCmdDispatch vkCmdDispatch = nullptr;
PFN_vkCmdDispatchIndirect vkCmdDispatchIndirect = nullptr;
PFN_vkCmdCopyBuffer vkCmdCopyBuffer = nullptr;
PFN_vkCmdCopyImage vkCmdCopyImage = nullptr;
PFN_vkCmdBlitImage vkCmdBlitImage = nullptr;
PFN_vkCmdCopyBufferToImage vkCmdCopyBufferToImage = nullptr;
PFN_vkCmdCopyImageToBuffer vkCmdCopyImageToBuffer = nullptr;
PFN_vkCmdUpdateBuffer vkCmdUpdateBuffer = nullptr;
PFN_vkCmdFillBuffer vkCmdFillBuffer = nullptr;
PFN_vkCmdClearColorImage vkCmdClearColorImage = nullptr;
PFN_vkCmdClearDepthStencilImage vkCmdClearDepthStencilImage = nullptr;
PFN_vkCmdClearAttachments vkCmdClearAttachments = nullptr;
PFN_vkCmdResolveImage vkCmdResolveImage = nullptr;
PFN_vkCmdSetEvent vkCmdSetEvent = nullptr;
PFN_vkCmdResetEvent vkCmdResetEvent = nullptr;
PFN_vkCmdWaitEvents vkCmdWaitEvents = nullptr;
PFN_vkCmdPipelineBarrier vkCmdPipelineBarrier = nullptr;
PFN_vkCmdBeginQuery vkCmdBeginQuery = nullptr;
PFN_vkCmdEndQuery vkCmdEndQuery = nullptr;
PFN_vkCmdResetQueryPool vkCmdResetQueryPool = nullptr;
PFN_vkCmdWriteTimestamp vkCmdWriteTimestamp = nullptr;
PFN_vkCmdCopyQueryPoolResults vkCmdCopyQueryPoolResults = nullptr;
PFN_vkCmdPushConstants vkCmdPushConstants = nullptr;
PFN_vkCmdBeginRenderPass vkCmdBeginRenderPass = nullptr;
PFN_vkCmdBeginRenderPass2 vkCmdBeginRenderPass2 = nullptr;
PFN_vkCmdNextSubpass vkCmdNextSubpass = nullptr;
PFN_vkCmdNextSubpass2 vkCmdNextSubpass2 = nullptr;
PFN_vkCmdEndRenderPass vkCmdEndRenderPass = nullptr;
PFN_vkCmdEndRenderPass2 vkCmdEndRenderPass2 = nullptr;
PFN_vkCmdExecuteCommands vkCmdExecuteCommands = nullptr;

// VK_KHR_surface
PFN_vkDestroySurfaceKHR vkDestroySurfaceKHR = nullptr;
PFN_vkGetPhysicalDeviceSurfaceSupportKHR vkGetPhysicalDeviceSurfaceSupportKHR = nullptr;
PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR vkGetPhysicalDeviceSurfaceCapabilitiesKHR = nullptr;
PFN_vkGetPhysicalDeviceSurfaceFormatsKHR vkGetPhysicalDeviceSurfaceFormatsKHR = nullptr;
PFN_vkGetPhysicalDeviceSurfacePresentModesKHR vkGetPhysicalDeviceSurfacePresentModesKHR = nullptr;

// VK_KHR_swapchain
PFN_vkCreateSwapchainKHR vkCreateSwapchainKHR = nullptr;
PFN_vkDestroySwapchainKHR vkDestroySwapchainKHR = nullptr;
PFN_vkGetSwapchainImagesKHR vkGetSwapchainImagesKHR = nullptr;
PFN_vkAcquireNextImageKHR vkAcquireNextImageKHR = nullptr;
PFN_vkQueuePresentKHR vkQueuePresentKHR = nullptr;

// VK_KHR_display
PFN_vkGetPhysicalDeviceDisplayPropertiesKHR vkGetPhysicalDeviceDisplayPropertiesKHR = nullptr;
PFN_vkGetPhysicalDeviceDisplayPlanePropertiesKHR vkGetPhysicalDeviceDisplayPlanePropertiesKHR = nullptr;
PFN_vkGetDisplayPlaneSupportedDisplaysKHR vkGetDisplayPlaneSupportedDisplaysKHR = nullptr;
PFN_vkGetDisplayModePropertiesKHR vkGetDisplayModePropertiesKHR = nullptr;
PFN_vkCreateDisplayModeKHR vkCreateDisplayModeKHR = nullptr;
PFN_vkGetDisplayPlaneCapabilitiesKHR vkGetDisplayPlaneCapabilitiesKHR = nullptr;
PFN_vkCreateDisplayPlaneSurfaceKHR vkCreateDisplayPlaneSurfaceKHR = nullptr;

// VK_KHR_display_swapchain
PFN_vkCreateSharedSwapchainsKHR vkCreateSharedSwapchainsKHR = nullptr;

// VK_EXT_debug_utils
PFN_vkCreateDebugUtilsMessengerEXT vkCreateDebugUtilsMessengerEXT = nullptr;
PFN_vkDestroyDebugUtilsMessengerEXT vkDestroyDebugUtilsMessengerEXT = nullptr;
PFN_vkSubmitDebugUtilsMessageEXT vkSubmitDebugUtilsMessageEXT = nullptr;
PFN_vkCmdBeginDebugUtilsLabelEXT vkCmdBeginDebugUtilsLabelEXT = nullptr;
PFN_vkCmdEndDebugUtilsLabelEXT vkCmdEndDebugUtilsLabelEXT = nullptr;
PFN_vkCmdInsertDebugUtilsLabelEXT vkCmdInsertDebugUtilsLabelEXT = nullptr;
PFN_vkSetDebugUtilsObjectNameEXT vkSetDebugUtilsObjectNameEXT = nullptr;
PFN_vkSetDebugUtilsObjectTagEXT vkSetDebugUtilsObjectTagEXT = nullptr;
PFN_vkQueueBeginDebugUtilsLabelEXT vkQueueBeginDebugUtilsLabelEXT = nullptr;
PFN_vkQueueEndDebugUtilsLabelEXT vkQueueEndDebugUtilsLabelEXT = nullptr;
PFN_vkQueueInsertDebugUtilsLabelEXT vkQueueInsertDebugUtilsLabelEXT = nullptr;

// VK_EXT_debug_report
PFN_vkCreateDebugReportCallbackEXT vkCreateDebugReportCallbackEXT = nullptr;
PFN_vkDestroyDebugReportCallbackEXT vkDestroyDebugReportCallbackEXT = nullptr;
PFN_vkDebugReportMessageEXT vkDebugReportMessageEXT = nullptr;

// Platform-specific surface functions
#ifdef VK_USE_PLATFORM_XLIB_KHR
PFN_vkCreateXlibSurfaceKHR vkCreateXlibSurfaceKHR = nullptr;
PFN_vkGetPhysicalDeviceXlibPresentationSupportKHR vkGetPhysicalDeviceXlibPresentationSupportKHR = nullptr;
#endif

#ifdef VK_USE_PLATFORM_XCB_KHR
PFN_vkCreateXcbSurfaceKHR vkCreateXcbSurfaceKHR = nullptr;
PFN_vkGetPhysicalDeviceXcbPresentationSupportKHR vkGetPhysicalDeviceXcbPresentationSupportKHR = nullptr;
#endif

#ifdef VK_USE_PLATFORM_WAYLAND_KHR
PFN_vkCreateWaylandSurfaceKHR vkCreateWaylandSurfaceKHR = nullptr;
PFN_vkGetPhysicalDeviceWaylandPresentationSupportKHR vkGetPhysicalDeviceWaylandPresentationSupportKHR = nullptr;
#endif

#ifdef VK_USE_PLATFORM_MIR_KHR
PFN_vkCreateMirSurfaceKHR vkCreateMirSurfaceKHR = nullptr;
PFN_vkGetPhysicalDeviceMirPresentationSupportKHR vkGetPhysicalDeviceMirPresentationSupportKHR = nullptr;
#endif

#ifdef VK_USE_PLATFORM_ANDROID_KHR
PFN_vkCreateAndroidSurfaceKHR vkCreateAndroidSurfaceKHR = nullptr;
#endif

#ifdef VK_USE_PLATFORM_WIN32_KHR
PFN_vkCreateWin32SurfaceKHR vkCreateWin32SurfaceKHR = nullptr;
PFN_vkGetPhysicalDeviceWin32PresentationSupportKHR vkGetPhysicalDeviceWin32PresentationSupportKHR = nullptr;
#endif

#ifdef VK_USE_PLATFORM_METAL_EXT
PFN_vkCreateMetalSurfaceEXT vkCreateMetalSurfaceEXT = nullptr;
#endif

// VK_KHR_get_physical_device_properties2
PFN_vkGetPhysicalDeviceFeatures2KHR vkGetPhysicalDeviceFeatures2KHR = nullptr;
PFN_vkGetPhysicalDeviceProperties2KHR vkGetPhysicalDeviceProperties2KHR = nullptr;
PFN_vkGetPhysicalDeviceFormatProperties2KHR vkGetPhysicalDeviceFormatProperties2KHR = nullptr;
PFN_vkGetPhysicalDeviceImageFormatProperties2KHR vkGetPhysicalDeviceImageFormatProperties2KHR = nullptr;
PFN_vkGetPhysicalDeviceQueueFamilyProperties2KHR vkGetPhysicalDeviceQueueFamilyProperties2KHR = nullptr;
PFN_vkGetPhysicalDeviceMemoryProperties2KHR vkGetPhysicalDeviceMemoryProperties2KHR = nullptr;
PFN_vkGetPhysicalDeviceSparseImageFormatProperties2KHR vkGetPhysicalDeviceSparseImageFormatProperties2KHR = nullptr;

// VK_KHR_maintenance1
PFN_vkTrimCommandPoolKHR vkTrimCommandPoolKHR = nullptr;

// VK_KHR_maintenance3
PFN_vkGetDescriptorSetLayoutSupportKHR vkGetDescriptorSetLayoutSupportKHR = nullptr;

// VK_KHR_bind_memory2
PFN_vkBindBufferMemory2KHR vkBindBufferMemory2KHR = nullptr;
PFN_vkBindImageMemory2KHR vkBindImageMemory2KHR = nullptr;

// VK_KHR_create_renderpass2
PFN_vkCreateRenderPass2KHR vkCreateRenderPass2KHR = nullptr;
PFN_vkCmdBeginRenderPass2KHR vkCmdBeginRenderPass2KHR = nullptr;
PFN_vkCmdNextSubpass2KHR vkCmdNextSubpass2KHR = nullptr;
PFN_vkCmdEndRenderPass2KHR vkCmdEndRenderPass2KHR = nullptr;

// VK_KHR_draw_indirect_count
PFN_vkCmdDrawIndirectCountKHR vkCmdDrawIndirectCountKHR = nullptr;
PFN_vkCmdDrawIndexedIndirectCountKHR vkCmdDrawIndexedIndirectCountKHR = nullptr;

// VK_KHR_timeline_semaphore
PFN_vkGetSemaphoreCounterValueKHR vkGetSemaphoreCounterValueKHR = nullptr;
PFN_vkWaitSemaphoresKHR vkWaitSemaphoresKHR = nullptr;
PFN_vkSignalSemaphoreKHR vkSignalSemaphoreKHR = nullptr;

// VK_EXT_host_query_reset
PFN_vkResetQueryPoolEXT vkResetQueryPoolEXT = nullptr;

// VK_KHR_buffer_device_address
PFN_vkGetBufferDeviceAddressKHR vkGetBufferDeviceAddressKHR = nullptr;
PFN_vkGetBufferOpaqueCaptureAddressKHR vkGetBufferOpaqueCaptureAddressKHR = nullptr;
PFN_vkGetDeviceMemoryOpaqueCaptureAddressKHR vkGetDeviceMemoryOpaqueCaptureAddressKHR = nullptr;

// VK_KHR_deferred_host_operations
PFN_vkCreateDeferredOperationKHR vkCreateDeferredOperationKHR = nullptr;
PFN_vkDestroyDeferredOperationKHR vkDestroyDeferredOperationKHR = nullptr;
PFN_vkGetDeferredOperationMaxConcurrencyKHR vkGetDeferredOperationMaxConcurrencyKHR = nullptr;
PFN_vkGetDeferredOperationResultKHR vkGetDeferredOperationResultKHR = nullptr;
PFN_vkDeferredOperationJoinKHR vkDeferredOperationJoinKHR = nullptr;

// VK_KHR_pipeline_executable_properties
PFN_vkGetPipelineExecutablePropertiesKHR vkGetPipelineExecutablePropertiesKHR = nullptr;
PFN_vkGetPipelineExecutableStatisticsKHR vkGetPipelineExecutableStatisticsKHR = nullptr;
PFN_vkGetPipelineExecutableInternalRepresentationsKHR vkGetPipelineExecutableInternalRepresentationsKHR = nullptr;

// VK_KHR_synchronization2
PFN_vkCmdSetEvent2KHR vkCmdSetEvent2KHR = nullptr;
PFN_vkCmdResetEvent2KHR vkCmdResetEvent2KHR = nullptr;
PFN_vkCmdWaitEvents2KHR vkCmdWaitEvents2KHR = nullptr;
PFN_vkCmdPipelineBarrier2KHR vkCmdPipelineBarrier2KHR = nullptr;
PFN_vkCmdWriteTimestamp2KHR vkCmdWriteTimestamp2KHR = nullptr;
PFN_vkQueueSubmit2KHR vkQueueSubmit2KHR = nullptr;
PFN_vkCmdWriteBufferMarker2AMD vkCmdWriteBufferMarker2AMD = nullptr;
PFN_vkGetQueueCheckpointData2NV vkGetQueueCheckpointData2NV = nullptr;

// VK_KHR_copy_commands2
PFN_vkCmdCopyBuffer2KHR vkCmdCopyBuffer2KHR = nullptr;
PFN_vkCmdCopyImage2KHR vkCmdCopyImage2KHR = nullptr;
PFN_vkCmdCopyBufferToImage2KHR vkCmdCopyBufferToImage2KHR = nullptr;
PFN_vkCmdCopyImageToBuffer2KHR vkCmdCopyImageToBuffer2KHR = nullptr;
PFN_vkCmdBlitImage2KHR vkCmdBlitImage2KHR = nullptr;
PFN_vkCmdResolveImage2KHR vkCmdResolveImage2KHR = nullptr;

// VK_EXT_conditional_rendering
PFN_vkCmdBeginConditionalRenderingEXT vkCmdBeginConditionalRenderingEXT = nullptr;
PFN_vkCmdEndConditionalRenderingEXT vkCmdEndConditionalRenderingEXT = nullptr;

// VK_EXT_transform_feedback
PFN_vkCmdBindTransformFeedbackBuffersEXT vkCmdBindTransformFeedbackBuffersEXT = nullptr;
PFN_vkCmdBeginTransformFeedbackEXT vkCmdBeginTransformFeedbackEXT = nullptr;
PFN_vkCmdEndTransformFeedbackEXT vkCmdEndTransformFeedbackEXT = nullptr;
PFN_vkCmdBeginQueryIndexedEXT vkCmdBeginQueryIndexedEXT = nullptr;
PFN_vkCmdEndQueryIndexedEXT vkCmdEndQueryIndexedEXT = nullptr;
PFN_vkCmdDrawIndirectByteCountEXT vkCmdDrawIndirectByteCountEXT = nullptr;