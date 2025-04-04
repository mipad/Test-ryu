// 

#include "ryuijnx.h"
#include "pthread.h"
#include <chrono>
#include <csignal>
#include <android/native_window.h>
#include <vulkan/vulkan.h>
#include <adrenotools.h>
#include <glm/glm.hpp>
#include <glm/gtc/matrix_transform.hpp>
#include <atomic>
#include <mutex>
#include <android/log.h>

//新增比例功能相关定义 
enum AspectRatio {
    AspectRatioStretch,
    AspectRatio16_9,
    AspectRatioCount
};

static AspectRatio currentAspectRatio = AspectRatioStretch;
static std::atomic<bool> aspectRatioDirty(false);
static std::mutex swapchainMutex;
static bool needRecreateSwapchain = false;

// Vulkan 全局对象声明
extern VkDevice vulkanDevice;
extern VkSwapchainKHR swapchain;
extern ANativeWindow* nativeWindow;
extern VkExtent2D swapchainExtent;
extern VkSurfaceKHR vulkanSurface;
extern VkCommandPool commandPool; 
extern uint32_t graphicsQueueFamilyIndex; 
extern VkPipelineLayout pipelineLayout; 

std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;

extern "C" {

// 新增比例设置接口
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setAspectRatio(
        JNIEnv *env,
        jobject thiz,
        jlong native_window,
        jint ratio_mode) {
    if (native_window == 0 || native_window == -1)
        return;

    auto window = (ANativeWindow *) native_window;
    const int32_t originalWidth = ANativeWindow_getWidth(window);
    const int32_t originalHeight = ANativeWindow_getHeight(window);
    
    int32_t targetWidth = originalWidth;
    int32_t targetHeight = originalHeight;

    switch (ratio_mode) {
        case 1: { // 16:9 模式
            targetHeight = (originalWidth * 9) / 16;
            if (targetHeight > originalHeight) {
                targetHeight = originalHeight;
                targetWidth = (originalHeight * 16) / 9;
            }
            break;
        }
        default: // 拉伸模式
            break;
    }

    // 设置新的缓冲区几何尺寸
    ANativeWindow_setBuffersGeometry(
        window, 
        targetWidth, 
        targetHeight, 
        AHARDWAREBUFFER_FORMAT_R8G8B8A8_UNORM
    );

    {
        std::lock_guard<std::mutex> lock(swapchainMutex);
        currentAspectRatio = static_cast<AspectRatio>(ratio_mode);
        needRecreateSwapchain = true;
    }
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getCurrentAspectRatio(
        JNIEnv *env,
        jobject thiz) {
    return static_cast<jint>(currentAspectRatio);
}

void recreateSwapchain() {
    std::lock_guard<std::mutex> lock(swapchainMutex);
    
    vkDeviceWaitIdle(vulkanDevice);
    
    if (swapchain != VK_NULL_HANDLE) {
        vkDestroySwapchainKHR(vulkanDevice, swapchain, nullptr);
        swapchain = VK_NULL_HANDLE;
    }

    int32_t width = ANativeWindow_getWidth(nativeWindow);
    int32_t height = ANativeWindow_getHeight(nativeWindow);
    swapchainExtent = {static_cast<uint32_t>(width), static_cast<uint32_t>(height)};

    VkSwapchainCreateInfoKHR createInfo{};
    createInfo.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    createInfo.surface = vulkanSurface;
    createInfo.minImageCount = 2;
    createInfo.imageFormat = VK_FORMAT_R8G8B8A8_UNORM;
    createInfo.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    createInfo.imageExtent = swapchainExtent;
    createInfo.imageArrayLayers = 1;
    createInfo.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    createInfo.preTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    createInfo.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    createInfo.presentMode = VK_PRESENT_MODE_FIFO_KHR;

    VkResult result = vkCreateSwapchainKHR(vulkanDevice, &createInfo, nullptr, &swapchain);
    if (result != VK_SUCCESS) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", "Swapchain recreation failed: %d", result);
    }
    
    needRecreateSwapchain = false;
}

void renderFrame() {
    if (needRecreateSwapchain) {
        recreateSwapchain();
        return;
    }

    // 分配命令缓冲区
    VkCommandBuffer cmdBuffer;
    VkCommandBufferAllocateInfo allocInfo{};
    allocInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    allocInfo.commandPool = commandPool;
    allocInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    allocInfo.commandBufferCount = 1;
    if (vkAllocateCommandBuffers(vulkanDevice, &allocInfo, &cmdBuffer) != VK_SUCCESS) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", "Failed to allocate command buffer");
        return;
    }

    // 开始记录命令
    VkCommandBufferBeginInfo beginInfo{};
    beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    if (vkBeginCommandBuffer(cmdBuffer, &beginInfo) != VK_SUCCESS) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", "Failed to begin command buffer");
        return;
    }

    // 动态视口设置
    VkViewport viewport{};
    viewport.x = 0.0f;
    viewport.y = 0.0f;
    viewport.width = static_cast<float>(swapchainExtent.width);
    viewport.height = static_cast<float>(swapchainExtent.height);
    viewport.minDepth = 0.0f;
    viewport.maxDepth = 1.0f;
    
    VkRect2D scissor{};
    scissor.offset = {0, 0};
    scissor.extent = swapchainExtent;

    vkCmdSetViewport(cmdBuffer, 0, 1, &viewport);
    vkCmdSetScissor(cmdBuffer, 0, 1, &scissor);

    // 投影矩阵适配
    glm::mat4 projection = glm::mat4(1.0f);
    float windowAspect = static_cast<float>(swapchainExtent.width) / swapchainExtent.height;
    
    switch (currentAspectRatio) {
        case AspectRatio16_9: {
            const float targetAspect = 16.0f / 9.0f;
            if (windowAspect > targetAspect) {
                projection = glm::scale(projection, 
                    glm::vec3(targetAspect / windowAspect, 1.0f, 1.0f));
            } else {
                projection = glm::scale(projection, 
                    glm::vec3(1.0f, windowAspect / targetAspect, 1.0f));
            }
            break;
        }
        default:
            projection = glm::mat4(1.0f);
    }
    
    // 上传投影矩阵到着色器
    vkCmdPushConstants(
        cmdBuffer, 
        pipelineLayout,
        VK_SHADER_STAGE_VERTEX_BIT,
        0, 
        sizeof(glm::mat4), 
        &projection
    );

    // 结束记录并提交命令缓冲区
    vkEndCommandBuffer(cmdBuffer);
    // ... 提交到队列的逻辑 ...
}
//▲▲▲▲▲▲▲▲▲▲ 渲染循环修改 ▲▲▲▲▲▲▲▲▲▲

//▼▼▼▼▼▼▼▼▼▼ Vulkan 初始化补充 ▼▼▼▼▼▼▼▼▼▼
void initVulkan() {
    // 创建命令池
    VkCommandPoolCreateInfo poolInfo{};
    poolInfo.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    poolInfo.queueFamilyIndex = graphicsQueueFamilyIndex;
    poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    if (vkCreateCommandPool(vulkanDevice, &poolInfo, nullptr, &commandPool) != VK_SUCCESS) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", "Failed to create command pool!");
    }

    // 创建管线布局
    VkPushConstantRange pushConstantRange{};
    pushConstantRange.stageFlags = VK_SHADER_STAGE_VERTEX_BIT;
    pushConstantRange.offset = 0;
    pushConstantRange.size = sizeof(glm::mat4);

    VkPipelineLayoutCreateInfo pipelineLayoutInfo{};
    pipelineLayoutInfo.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    pipelineLayoutInfo.pushConstantRangeCount = 1;
    pipelineLayoutInfo.pPushConstantRanges = &pushConstantRange;
    if (vkCreatePipelineLayout(vulkanDevice, &pipelineLayoutInfo, nullptr, &pipelineLayout) != VK_SUCCESS) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", "Failed to create pipeline layout!");
    }
}

long createSurface(long native_surface, long instance) {
    auto nativeWindow = (ANativeWindow *) native_surface;
    VkSurfaceKHR surface;
    auto vkInstance = (VkInstance) instance;
    auto fpCreateAndroidSurfaceKHR =
            reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(vkGetInstanceProcAddr(vkInstance, "vkCreateAndroidSurfaceKHR"));
    if (!fpCreateAndroidSurfaceKHR)
        return -1;
    VkAndroidSurfaceCreateInfoKHR info = {VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR};
    info.window = nativeWindow;
    VK_CHECK(fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface));
    vulkanSurface = surface; // 修正：在return前赋值
    return (long) surface;
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(
        JNIEnv *env,
        jobject instance) {
    return (jlong) createSurface;
}

char *getStringPointer(
        JNIEnv *env,
        jstring jS) {
    const char *cparam = env->GetStringUTFChars(jS, 0);
    auto len = env->GetStringUTFLength(jS);
    char *s = new char[len];
    strcpy(s, cparam);
    env->ReleaseStringUTFChars(jS, cparam);

    return s;
}

jstring createString(
        JNIEnv *env,
        char *ch) {
    auto str = env->NewStringUTF(ch);

    return str;
}

jstring createStringFromStdString(
        JNIEnv *env,
        std::string s) {
    auto str = env->NewStringUTF(s.c_str());

    return str;
}


}
extern "C"
void setRenderingThread() {
    auto currentId = pthread_self();

    _renderingThreadId = currentId;

    _currentTimePoint = std::chrono::high_resolution_clock::now();
}
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    auto success = env->GetJavaVM(&vm);
    _vm = vm;
    _mainActivity = thiz;
    _mainActivityClass = env->GetObjectClass(thiz);
}

bool isInitialOrientationFlipped = true;

extern "C"
void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1)
        return;
    auto nativeWindow = (ANativeWindow *) native_window;

    auto nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;

    transform = transform >> 1;

    // transform is a valid VkSurfaceTransformFlagBitsKHR
    switch (transform) {
        case 0x1:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
        case 0x2:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90;
            break;
        case 0x4:
            nativeTransform = isInitialOrientationFlipped
                              ? ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY
                              : ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_180;
            break;
        case 0x8:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_270;
            break;
        case 0x10:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL;
            break;
        case 0x20:
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL |
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x40:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL;
            break;
        case 0x80:
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL |
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x100:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
    }

    nativeWindow->perform(nativeWindow, NATIVE_WINDOW_SET_BUFFERS_TRANSFORM,
                          static_cast<int32_t>(nativeTransform));
}

extern "C"
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_loadDriver(JNIEnv *env, jobject thiz,
                                                  jstring native_lib_path,
                                                  jstring private_apps_path,
                                                  jstring driver_name) {
    auto libPath = getStringPointer(env, native_lib_path);
    auto privateAppsPath = getStringPointer(env, private_apps_path);
    auto driverName = getStringPointer(env, driver_name);

    auto handle = adrenotools_open_libvulkan(
            RTLD_NOW,
            ADRENOTOOLS_DRIVER_CUSTOM,
            nullptr,
            libPath,
            privateAppsPath,
            driverName,
            nullptr,
            nullptr
    );

    delete libPath;
    delete privateAppsPath;
    delete driverName;

    return (jlong) handle;
}

extern "C"
void debug_break(int code) {
    if (code >= 3)
        int r = 0;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv *env, jobject thiz, jboolean enable) {
    adrenotools_set_turbo(enable);
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->maxSwapInterval;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->minSwapInterval;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz,
                                                       jlong native_window, jint swap_interval) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->setSwapInterval(nativeWindow, swap_interval);
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv *env, jobject thiz, jlong ptr) {
    return createString(env, (char*)ptr);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz,
                                                                      jboolean is_flipped) {
    isInitialOrientationFlipped = is_flipped;
}
