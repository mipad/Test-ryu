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

# 包含 FFmpeg 静态库构建 - 使用相对路径
include(../../../../libryujinx/libs/FFmpegStatic.cmake)

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

# 创建主库，包含 FFmpeg 包装器
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
    oboe_audio_renderer.cpp
    ffmpeg_static_wrapper.cpp  # 新增的 FFmpeg 静态库包装器
)

# 添加编译定义以优化音频处理
target_compile_definitions(ryujinxjni PRIVATE
    RYUJINX_AUDIO_OPTIMIZE=1
)

# 为音频渲染器文件单独启用激进优化
set_source_files_properties(oboe_audio_renderer.cpp PROPERTIES
    COMPILE_FLAGS "-Ofast"
)

# 为 FFmpeg 包装器启用优化
set_source_files_properties(ffmpeg_static_wrapper.cpp PROPERTIES
    COMPILE_FLAGS "-O3 -ffast-math"
)

# 为主目标添加编译选项
target_compile_options(ryujinxjni PRIVATE
    -std=c++20
    -O3
    -DNDEBUG
    -march=armv8-a
)

# 查找系统库
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(aaudio-lib aaudio)
find_library(android-lib android)
find_library(vulkan-lib vulkan)

# 添加 Oboe 包含目录
include_directories(${oboe_SOURCE_DIR}/include)

# 添加 FFmpeg 包含目录
target_include_directories(ryujinxjni PRIVATE
    ${FFMPEG_INCLUDE_DIR}
)

# 链接库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${opensles-lib}
    ${aaudio-lib}
    ${android-lib}
    ${vulkan-lib}
    adrenotools
    oboe
    ffmpeg-static-combined  # 链接 FFmpeg 静态库
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

# 输出构建信息
message(STATUS "Building for Android ABI: ${CMAKE_ANDROID_ARCH_ABI}")
message(STATUS "FFmpeg include directory: ${FFMPEG_INCLUDE_DIR}")
message(STATUS "Oboe source directory: ${oboe_SOURCE_DIR}")
message(STATUS "Output directory: ${JNI_PATH}")
