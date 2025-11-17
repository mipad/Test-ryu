include(FetchContent)

# For more information about using CMake with Android Studio, read the
# documentation: https://d.android.com/studio/projects/add-native-code.html

# Sets the minimum version of CMake required to build the native library.
cmake_minimum_required(VERSION 3.22.1)

# Declares and names the project.
project("ryujinxjni")

# 设置构建类型
set(CMAKE_BUILD_TYPE Release)

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

# 设置 OpenAL 构建选项 - 优化以减少杂音和延迟
set(ALSOFT_REQUIRE_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_AAUDIO OFF CACHE BOOL "Disable AAudio backend")
set(ALSOFT_NO_CONFIG_UTIL ON CACHE BOOL "Disable config utility")
set(ALSOFT_UTILS OFF CACHE BOOL "Disable utils")
set(ALSOFT_EXAMPLES OFF CACHE BOOL "Disable examples")
set(ALSOFT_INSTALL OFF CACHE BOOL "Disable installation")
set(ALSOFT_INSTALL_PKG_CONFIG_FILES OFF CACHE BOOL "Disable pkg-config files")
set(ALSOFT_TESTS OFF CACHE BOOL "Disable tests")

# 添加优化选项以减少杂音和延迟
set(ALSOFT_CPUEXT_NEON ON CACHE BOOL "Enable NEON optimizations")
set(ALSOFT_REQUIRE_NEON OFF CACHE BOOL "Don't require NEON")
set(ALSOFT_HRTF_DISABLE ON CACHE BOOL "Disable HRTF" FORCE) # 禁用HRTF可能减少延迟
set(ALSOFT_AMBIENT_CHANNELS 0 CACHE STRING "Ambient channels") # 减少环境声道

# 针对低延迟优化
set(ALSOFT_DEFAULT_CFG "" CACHE STRING "Default config file")
set(ALSOFT_CONFIG "" CACHE STRING "Config file")
set(ALSOFT_DEFAULT_REVERB OFF CACHE BOOL "Disable default reverb")
set(ALSOFT_DEFAULT_ECHO OFF CACHE BOOL "Disable default echo")
set(ALSOFT_DEFAULT_MODULATION OFF CACHE BOOL "Disable default modulation")

FetchContent_MakeAvailable(openal)

# 创建主库
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
    oboe_audio_renderer.cpp
    stabilized_audio_callback.cpp
)

# 添加编译定义以优化音频处理
target_compile_definitions(ryujinxjni PRIVATE
    RYUJINX_AUDIO_OPTIMIZE=1
    # 如果需要，可以添加更多音频优化标志
)


# 为主目标添加编译选项（不包含 -ffast-math）
target_compile_options(ryujinxjni PRIVATE
    -std=c++20
    -O3
    -DNDEBUG
    -march=armv8-a
    # 注意：移除了全局的 -ffast-math
)

# 查找系统库
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(android-lib android)

# 添加 Oboe 包含目录
include_directories(${oboe_SOURCE_DIR}/include)

# 链接库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${opensles-lib}
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
    add_dependencies(ryujinxjni openssl)
endif ()

# 设置 OpenAL 输出目录
set_target_properties(OpenAL PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)
