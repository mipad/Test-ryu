cmake_minimum_required(VERSION 3.18...3.31)

# ==================== 核心配置 ====================
# 加载 Android NDK 工具链
include($ENV{ANDROID_NDK}/build/cmake/android.toolchain.cmake)
set(CMAKE_SYSTEM_NAME Android)
set(CMAKE_ANDROID_ARCH_ABI arm64-v8a)
set(CMAKE_ANDROID_NDK $ENV{ANDROID_NDK})

project("ryujinxjni")

# ==================== 路径设置 ====================
# 主项目路径：假设 CMakeLists.txt 位于 libryujinxAndroid 目录内
set(PROJECT_ROOT ${CMAKE_SOURCE_DIR})
set(OPENSSL_ROOT ${PROJECT_ROOT}/libs/openssl)  # OpenSSL 源码路径
set(OPENSSL_BUILD_DIR ${PROJECT_ROOT}/build/openssl)  # OpenSSL 编译输出路径

# ==================== 编译选项 ====================
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${PROJECT_ROOT}/../jniLibs/${CMAKE_ANDROID_ARCH_ABI})

# ==================== 引入 OpenSSL 编译 ====================
# 定义 OpenSSL 编译配置
include(${PROJECT_ROOT}/libs/OpenSSL.cmake)  # OpenSSL 的自定义 CMake 配置

# ==================== 包含外部模块 ====================
# 模块路径：主项目外的其他模块位于根目录/src/
set(MODULES_BASE_DIR ${CMAKE_SOURCE_DIR}/../../src)  # 假设主项目在 libryujinxAndroid 目录内

# 添加模块
add_subdirectory(${MODULES_BASE_DIR}/ARMeilleure)
add_subdirectory(${MODULES_BASE_DIR}/LibRyujinx.NativeSample)
add_subdirectory(${MODULES_BASE_DIR}/LibRyujinx)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Audio.Backends.OpenAL)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Audio.Backends.SDL2)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Audio.Backends.SoundIo)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Audio)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Common)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Cpu)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Device)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.GAL)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Gpu)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Host1x)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Nvdec.FFmpeg)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Nvdec.Vp9)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Nvdec)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.OpenGL)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Shader)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Texture)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Vic)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Video)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Vulkan)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.HLE.Generators)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.HLE)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Headless.SDL2)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Horizon.Common)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Horizon.Generators)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Horizon.Kernel.Generators)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Horizon)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Input.SDL2)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Input)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Memory)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.SDL2.Common)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.ShaderTools)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Tests.Memory)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Tests.Unicorn)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Tests)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.UI.Common)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx.UI.LocaleGenerator)
add_subdirectory(${MODULES_BASE_DIR}/Ryujinx)
add_subdirectory(${MODULES_BASE_DIR}/Spv.Generator)




# ==================== 主库定义 ====================
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
    
    
)

# 强制生成 .so 文件
set_target_properties(ryujinxjni PROPERTIES SUFFIX ".so")

# ==================== 链接依赖 ====================
find_library(log-lib log)
find_library(android-lib android)
find_library(vulkan-lib vulkan)

target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${android-lib}
    ${vulkan-lib}
    # 链接 OpenSSL 静态库
    ${OPENSSL_BUILD_DIR}/libcrypto.a
    ${OPENSSL_BUILD_DIR}/libssl.a
    # 链接外部模块
    Ryujinx.UI.Common
    Ryujinx.Tests
    Ryujinx.Tests.Unicorn
    Ryujinx.Tests.Memory
    Ryujinx.ShaderTools
    Ryujinx.SDL2.Common
    Ryujinx.Memory
    Ryujinx.Input
    Ryujinx.Input.SDL2
    Ryujinx.Horizon
    Ryujinx.Horizon.Kernel.Generators
    Ryujinx.Horizon.Generators
    Ryujinx.Horizon.Common
    Ryujinx.Headless.SDL2
    Ryujinx.HLE
    Ryujinx.HLE.Generators
    Ryujinx.Graphics.Nvdec.Vp9
    Ryujinx.Graphics.Vulkan
    Ryujinx.Graphics.Shader
    Ryujinx.Graphics.Video
    Ryujinx.Graphics.Nvdec
    Ryujinx.Graphics.Vic
    Ryujinx.Graphics.Texture
    Ryujinx.Graphics.OpenGL
    Ryujinx.Graphics.Host1x
    Ryujinx.Graphics.Nvdec.FFmpeg
    Ryujinx.Graphics.Gpu
    Ryujinx.Common
    Ryujinx.Graphics.GAL
    Ryujinx.Graphics.Device
    Ryujinx.Cpu
    Ryujinx.Audio
    Ryujinx.Audio.Backends.SoundIo
    Ryujinx.Audio.Backends.SDL2
    Ryujinx.Audio.Backends.OpenAL
    LibRyujinx
    LibRyujinx.NativeSample
    ARMeilleure
    Ryujinx.UI.LocaleGenerator
    Spv.Generator
    Ryujinx
    
)

# ==================== 头文件包含 ====================
target_include_directories(ryujinxjni PRIVATE
    ${OPENSSL_ROOT}/include  # OpenSSL 头文件路径
    ${MODULES_BASE_DIR}/Ryujinx.Common/include
    ${MODULES_BASE_DIR}/Ryujinx.Cpu/include
    ${MODULES_BASE_DIR}/Ryujinx.Graphics/include
)
