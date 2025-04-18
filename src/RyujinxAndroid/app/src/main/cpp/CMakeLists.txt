cmake_minimum_required(VERSION 3.18...3.31)

# ==================== 核心配置 ====================
# 加载 Android NDK 工具链
include($ENV{ANDROID_NDK}/build/cmake/android.toolchain.cmake)
set(CMAKE_SYSTEM_NAME Android)
set(CMAKE_ANDROID_ARCH_ABI arm64-v8a)
set(CMAKE_ANDROID_NDK $ENV{ANDROID_NDK})

project("ryujinxjni")

# ==================== 路径设置 ====================
set(PROJECT_ROOT ${CMAKE_SOURCE_DIR})
set(OPENSSL_ROOT ${PROJECT_ROOT}/libs/openssl)
set(OPENSSL_BUILD_DIR ${PROJECT_ROOT}/build/openssl)

# ==================== 编译选项 ====================
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${PROJECT_ROOT}/../jniLibs/${CMAKE_ANDROID_ARCH_ABI})  # 强制输出到jniLibs

# ==================== 引入 OpenSSL 编译 ====================
include(${PROJECT_ROOT}/libs/OpenSSL.cmake)

# ==================== 包含外部模块 ====================
set(MODULES_BASE_DIR ${CMAKE_SOURCE_DIR}/../../src)

# 强制所有子模块生成动态库（SHARED）
foreach(module
    ARMeilleure
    LibRyujinx.NativeSample
    LibRyujinx
    Ryujinx.Audio.Backends.OpenAL
    Ryujinx.Audio.Backends.SDL2
    Ryujinx.Audio.Backends.SoundIo
    Ryujinx.Audio
    Ryujinx.Common
    Ryujinx.Cpu
    Ryujinx.Graphics.Device
    Ryujinx.Graphics.GAL
    Ryujinx.Graphics.Gpu
    Ryujinx.Graphics.Host1x
    Ryujinx.Graphics.Nvdec.FFmpeg
    Ryujinx.Graphics.Nvdec.Vp9
    Ryujinx.Graphics.Nvdec
    Ryujinx.Graphics.OpenGL
    Ryujinx.Graphics.Shader
    Ryujinx.Graphics.Texture
    Ryujinx.Graphics.Vic
    Ryujinx.Graphics.Video
    Ryujinx.Graphics.Vulkan
    Ryujinx.HLE.Generators
    Ryujinx.HLE
    Ryujinx.Headless.SDL2
    Ryujinx.Horizon.Common
    Ryujinx.Horizon.Generators
    Ryujinx.Horizon.Kernel.Generators
    Ryujinx.Horizon
    Ryujinx.Input.SDL2
    Ryujinx.Input
    Ryujinx.Memory
    Ryujinx.SDL2.Common
    Ryujinx.ShaderTools
    Ryujinx.Tests.Memory
    Ryujinx.Tests.Unicorn
    Ryujinx.Tests
    Ryujinx.UI.Common
    Ryujinx.UI.LocaleGenerator
    Ryujinx
    Spv.Generator
)
    add_subdirectory(${MODULES_BASE_DIR}/${module})
    # 强制模块生成动态库
    set_target_properties(${module} PROPERTIES
        LIBRARY_OUTPUT_DIRECTORY ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
        POSITION_INDEPENDENT_CODE ON
    )
endforeach()

# ==================== 主库定义 ====================
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
)

set_target_properties(ryujinxjni PROPERTIES SUFFIX ".so")

# ==================== 链接依赖 ====================
find_library(log-lib log)
find_library(android-lib android)
find_library(vulkan-lib vulkan)

# 显式链接第三方动态库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${android-lib}
    ${vulkan-lib}
    ${OPENSSL_BUILD_DIR}/libcrypto.a
    ${OPENSSL_BUILD_DIR}/libssl.a
    # 链接外部模块（动态库）
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
    # 第三方库
    avcodec avutil swresample swscale  # FFmpeg
    OpenAL  # OpenAL
    SDL2    # SDL2
)

# ==================== 头文件包含 ====================
target_include_directories(ryujinxjni PRIVATE
    ${OPENSSL_ROOT}/include
    ${MODULES_BASE_DIR}/Ryujinx.Common/include
    ${MODULES_BASE_DIR}/Ryujinx.Cpu/include
    ${MODULES_BASE_DIR}/Ryujinx.Graphics/include
)
