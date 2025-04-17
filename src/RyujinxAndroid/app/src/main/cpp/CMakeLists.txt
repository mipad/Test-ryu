cmake_minimum_required(VERSION 3.18...3.31)

# ==================== 核心配置 ====================
include($ENV{ANDROID_NDK}/build/cmake/android.toolchain.cmake)
set(CMAKE_SYSTEM_NAME Android)
set(CMAKE_ANDROID_ARCH_ABI arm64-v8a)
set(CMAKE_ANDROID_NDK $ENV{ANDROID_NDK})

project("ryujinxjni")

# ==================== 路径设置 ====================
set(PROJECT_ROOT ${CMAKE_SOURCE_DIR})
set(OPENSSL_ROOT ${PROJECT_ROOT}/libs/openssl)
set(OPENSSL_BUILD_DIR ${PROJECT_ROOT}/build/openssl)
set(MODULES_BASE_DIR ${CMAKE_SOURCE_DIR}/../../src)  # 主源码目录
set(THIRDPARTY_DIR ${PROJECT_ROOT}/../thirdparty)    # 第三方库目录（假设路径）

# ==================== 编译选项 ====================
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${PROJECT_ROOT}/../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_LIBRARY_OUTPUT_DIRECTORY})  # 统一静态库输出路径
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_LIBRARY_OUTPUT_DIRECTORY})  # 统一可执行文件输出路径

# ==================== OpenSSL 配置 ====================
include(${PROJECT_ROOT}/libs/OpenSSL.cmake)  # 自定义 OpenSSL 编译逻辑

# ==================== 自定义宏：强制子模块为共享库 ====================
macro(add_shared_subdirectory dir)
    add_subdirectory(${dir})
    get_filename_component(MODULE_NAME ${dir} NAME)
    if(TARGET ${MODULE_NAME})
        # 强制设为共享库并设置输出路径
        set_target_properties(${MODULE_NAME} PROPERTIES
            LIBRARY_OUTPUT_DIRECTORY ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
            POSITION_INDEPENDENT_CODE ON
            CXX_VISIBILITY_PRESET hidden
        )
        # 安装到输出目录
        install(TARGETS ${MODULE_NAME}
            LIBRARY DESTINATION ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
        )
    endif()
endmacro()

# ==================== 添加所有子模块 ====================
add_shared_subdirectory(${MODULES_BASE_DIR}/ARMeilleure)
add_shared_subdirectory(${MODULES_BASE_DIR}/LibRyujinx.NativeSample)
add_shared_subdirectory(${MODULES_BASE_DIR}/LibRyujinx)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.UI.Common)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Tests)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Tests.Unicorn)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Tests.Memory)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.ShaderTools)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.SDL2.Common)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Memory)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Input)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Input.SDL2)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Horizon)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Horizon.Kernel.Generators)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Horizon.Generators)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Horizon.Common)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Headless.SDL2)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.HLE)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.HLE.Generators)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Nvdec.Vp9)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Vulkan)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Shader)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Video)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Nvdec)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Vic)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Texture)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.OpenGL)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Host1x)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Nvdec.FFmpeg)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Gpu)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Common)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.GAL)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Graphics.Device)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Cpu)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Audio)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Audio.Backends.SoundIo)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Audio.Backends.SDL2)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.Audio.Backends.OpenAL)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx.UI.LocaleGenerator)
add_shared_subdirectory(${MODULES_BASE_DIR}/Spv.Generator)
add_shared_subdirectory(${MODULES_BASE_DIR}/Ryujinx)

# ==================== 第三方库处理 ====================
# 示例：SDL2 的显式安装
add_subdirectory(${THIRDPARTY_DIR}/SDL2)
if(TARGET SDL2)
    install(TARGETS SDL2
        LIBRARY DESTINATION ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
    )
endif()

# 示例：FFmpeg 的显式安装（假设已配置）
add_subdirectory(${THIRDPARTY_DIR}/FFmpeg)
if(TARGET avcodec)
    install(TARGETS avcodec avutil swresample swscale
        LIBRARY DESTINATION ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
    )
endif()

# ==================== 主库定义 ====================
add_library(ryujinxjni SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
)
set_target_properties(ryujinxjni PROPERTIES SUFFIX ".so")

# ==================== 链接依赖 ====================
find_library(log-lib log)
find_library(android-lib android)
find_library(vulkan-lib vulkan)

target_link_libraries(ryujinxjni
    ${log-lib}
    ${android-lib}
    ${vulkan-lib}
    ${OPENSSL_BUILD_DIR}/libcrypto.a
    ${OPENSSL_BUILD_DIR}/libssl.a
    # 动态链接所有子模块
    ARMeilleure
    LibRyujinx.NativeSample
    LibRyujinx
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
    Ryujinx.UI.LocaleGenerator
    Spv.Generator
    Ryujinx
    # 第三方库
    SDL2
    avcodec
    avutil
    swresample
    swscale
)

# ==================== 头文件包含 ====================
target_include_directories(ryujinxjni PRIVATE
    ${OPENSSL_ROOT}/include
    ${MODULES_BASE_DIR}/Ryujinx.Common/include
    ${MODULES_BASE_DIR}/Ryujinx.Cpu/include
    ${MODULES_BASE_DIR}/Ryujinx.Graphics/include
    # 第三方库头文件
    ${THIRDPARTY_DIR}/SDL2/include
    ${THIRDPARTY_DIR}/FFmpeg/include
)

# ==================== 安装指令 ====================
install(TARGETS ryujinxjni
    LIBRARY DESTINATION ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
)
