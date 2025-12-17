include(FetchContent)

# For more information about using CMake with Android Studio, read the
# documentation: https://d.android.com/studio/projects/add-native-code.html

# Sets the minimum version of CMake required to build the native library.
cmake_minimum_required(VERSION 3.22.1)

# Declares and names the project.
project("ryujinxjni")

# 根据构建类型设置不同的配置
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Debug)
endif()

message(STATUS "Build type: ${CMAKE_BUILD_TYPE}")

# 其他选项
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded)
set(SKIA_USE_SYSTEM_EXPAT OFF)
set(SKIA_ENABLE_PARAGRAPH ON)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# 设置 Android 特定选项
set(ANDROID_STL c++_shared)
set(ANDROID_ARM_NEON TRUE)

# 使用 FetchContent 获取 adrenotools
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://git.ryujinx.app/kenji-nx/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
)

FetchContent_MakeAvailable(adrenotools)

# 为 adrenotools 相关目标添加 PAGE_SIZE 定义
if(TARGET adrenotools)
    target_compile_definitions(adrenotools PRIVATE PAGE_SIZE=4096)
endif()

if(TARGET linkernsbypass)
    target_compile_definitions(linkernsbypass PRIVATE PAGE_SIZE=4096)
endif()

# 为 adrenotools 的子目录目标也添加定义
get_target_property(adrenotools_sources adrenotools SOURCES)
if(adrenotools_sources)
    target_compile_definitions(adrenotools PRIVATE PAGE_SIZE=4096)
endif()

# 使用 FetchContent 获取 Oboe 源码
FetchContent_Declare(
    oboe
    GIT_REPOSITORY https://github.com/google/oboe.git
    GIT_TAG        1.10.0  # 使用与 Gradle 依赖相同的版本
)

# 设置 Oboe 构建选项，禁用格式转换以减少代码大小
set(OBOE_DISABLE_CONVERSION ON CACHE BOOL "Disable format conversion in Oboe")

FetchContent_MakeAvailable(oboe)

# 使用 FetchContent 获取标准 OpenAL Soft
FetchContent_Declare(
    openal
    GIT_REPOSITORY https://github.com/kcat/openal-soft.git
    GIT_TAG        1.24.3  # 使用稳定版本
)

# 设置 OpenAL 构建选项 - 禁用可选依赖以减少构建复杂度
set(ALSOFT_REQUIRE_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_AAUDIO ON CACHE BOOL "Enable AAudio backend")
set(ALSOFT_TESTS OFF CACHE BOOL "Disable tests")

# 禁用 OpenAL 的可选依赖，它们不是必需的
set(ALSOFT_BACKEND_PULSEAUDIO OFF CACHE BOOL "Disable PulseAudio backend")
set(ALSOFT_BACKEND_ALSA OFF CACHE BOOL "Disable ALSA backend")
set(ALSOFT_BACKEND_JACK OFF CACHE BOOL "Disable JACK backend")
set(ALSOFT_BACKEND_PORTAUDIO OFF CACHE BOOL "Disable PortAudio backend")
set(ALSOFT_BACKEND_COREAUDIO OFF CACHE BOOL "Disable CoreAudio backend")
set(ALSOFT_BACKEND_WASAPI OFF CACHE BOOL "Disable WASAPI backend")
set(ALSOFT_BACKEND_WINMM OFF CACHE BOOL "Disable WinMM backend")
set(ALSOFT_BACKEND_DSOUND OFF CACHE BOOL "Disable DirectSound backend")
set(ALSOFT_BACKEND_OSS OFF CACHE BOOL "Disable OSS backend")
set(ALSOFT_BACKEND_SNDIO OFF CACHE BOOL "Disable SndIO backend")
set(ALSOFT_BACKEND_SUN OFF CACHE BOOL "Disable Sun backend")
set(ALSOFT_BACKEND_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_WAVE OFF CACHE BOOL "Disable Wave backend")
set(ALSOFT_BACKEND_NULL OFF CACHE BOOL "Disable Null backend")

# 禁用 MySOFA 支持（减少依赖）
set(ALSOFT_EMBED_HRTF_DATA ON CACHE BOOL "Embed HRTF data")
set(ALSOFT_INSTALL OFF CACHE BOOL "Disable installation")
set(ALSOFT_UTILS OFF CACHE BOOL "Disable utilities")
set(ALSOFT_EXAMPLES OFF CACHE BOOL "Disable examples")
set(ALSOFT_NO_CONFIG_UTIL OFF CACHE BOOL "Disable config util")

# 添加优化选项以减少杂音和延迟
set(ALSOFT_CPUEXT_NEON ON CACHE BOOL "Enable NEON optimizations")

# 设置 FetchContent 属性以在配置阶段禁用查找
set(ALSOFT_REQUIRE_MYSOFA OFF)

FetchContent_MakeAvailable(openal)

# 创建主库
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
    oboe_audio_renderer.cpp
)

# 根据构建类型设置不同的编译定义
if(CMAKE_BUILD_TYPE STREQUAL "Debug")
    target_compile_definitions(ryujinxjni PRIVATE
        DEBUG=1
        _DEBUG=1
        RYUJINX_AUDIO_OPTIMIZE=0
        # 调试相关定义
    )
    message(STATUS "Using debug build configuration")
else()
    target_compile_definitions(ryujinxjni PRIVATE
        NDEBUG=1
        RYUJINX_AUDIO_OPTIMIZE=1
        # 发布版优化定义
    )
    message(STATUS "Using release build configuration")
endif()

# 根据构建类型设置编译选项
if(CMAKE_BUILD_TYPE STREQUAL "Debug")
    # 调试版本：保留符号和调试信息
    target_compile_options(ryujinxjni PRIVATE
        -std=c++20
        -O0        # 无优化
        -g         # 生成调试信息
        -ggdb      # 生成GDB调试信息
        -DDEBUG
        -D_DEBUG
        -march=armv8-a
        # 添加调试支持
        -fno-omit-frame-pointer
        -fno-inline-functions
    )
    
    # 为音频渲染器文件设置调试编译选项
    set_source_files_properties(oboe_audio_renderer.cpp PROPERTIES
        COMPILE_FLAGS "-O0 -g -ggdb"
    )
    
    # 设置调试版本的后缀
    set_target_properties(ryujinxjni PROPERTIES
        DEBUG_POSTFIX "_debug"
    )
else()
    # 发布版本：优化设置
    target_compile_options(ryujinxjni PRIVATE
        -std=c++20
        -O3
        -DNDEBUG
        -march=armv8-a
        # 发布版优化选项
        -ffast-math
        -funroll-loops
    )
    
    # 为音频渲染器文件设置发布版优化
    set_source_files_properties(oboe_audio_renderer.cpp PROPERTIES
        COMPILE_FLAGS "-Ofast"
    )
endif()

# 查找系统库
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(aaudio-lib aaudio)
find_library(android-lib android)

# 添加 Oboe 包含目录
include_directories(${oboe_SOURCE_DIR}/include)

# 链接库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${opensles-lib}
    ${aaudio-lib}
    ${android-lib}
    -lvulkan
    adrenotools
    oboe
    OpenAL
)

# 设置 JNI 库输出路径
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

# 构建 OpenSSL（如果预构建文件不存在）
if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    # 修复：正确的目标名称是 ryujinxjni，不是 ryujinxjnx
    add_dependencies(ryujinxjni openssl)
endif ()

# 设置 OpenAL 输出目录
set_target_properties(OpenAL PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 设置目标属性以保留调试符号
set_target_properties(ryujinxjni PROPERTIES
    COMPILE_FLAGS_DEBUG "-O0 -g -ggdb"
    COMPILE_FLAGS_RELEASE "-O3 -DNDEBUG"
    # 确保不剥离符号
    STRIP ""
)
