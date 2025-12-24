cmake_minimum_required(VERSION 3.22.1)
project("ryujinxjni")

# 设置构建类型
set(CMAKE_BUILD_TYPE Release)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# Android 特定选项
set(ANDROID_STL c++_shared)
set(ANDROID_ARM_NEON TRUE)
set(ANDROID_PLATFORM 30)

# 编译器优化标志
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -O3 -fPIC -march=armv8.2-a+fp16+dotprod -mtune=cortex-a78")
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -O3 -fPIC -march=armv8.2-a+fp16+dotprod -mtune=cortex-a78")

# 包含 FFmpeg 静态库构建
include(FFmpegStaticFixed.cmake)

# 使用 FetchContent 获取依赖
include(FetchContent)

# adrenotools
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://git.ryujinx.app/kenji-nx/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
)
FetchContent_MakeAvailable(adrenotools)

# Oboe 音频库
FetchContent_Declare(
    oboe
    GIT_REPOSITORY https://github.com/google/oboe.git
    GIT_TAG        1.10.0
)
set(OBOE_DISABLE_CONVERSION ON CACHE BOOL "Disable format conversion in Oboe")
FetchContent_MakeAvailable(oboe)

# OpenAL Soft
FetchContent_Declare(
    openal
    GIT_REPOSITORY https://github.com/kcat/openal-soft.git
    GIT_TAG        1.24.3
)
set(ALSOFT_REQUIRE_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_AAUDIO ON CACHE BOOL "Enable AAudio backend")
set(ALSOFT_TESTS OFF CACHE BOOL "Disable tests")
set(ALSOFT_CPUEXT_NEON ON CACHE BOOL "Enable NEON optimizations")
FetchContent_MakeAvailable(openal)

# 为主目标添加 PAGE_SIZE 定义
if(TARGET adrenotools)
    target_compile_definitions(adrenotools PRIVATE PAGE_SIZE=4096)
endif()
if(TARGET linkernsbypass)
    target_compile_definitions(linkernsbypass PRIVATE PAGE_SIZE=4096)
endif()

# 创建主库，包含 FFmpeg 包装器
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
    oboe_audio_renderer.cpp
    ffmpeg_static_wrapper.cpp  # 新增的 FFmpeg 静态库包装器
)

# 为音频渲染器文件启用激进优化
set_source_files_properties(oboe_audio_renderer.cpp PROPERTIES
    COMPILE_FLAGS "-Ofast"
)

# 为 FFmpeg 包装器启用优化
set_source_files_properties(ffmpeg_static_wrapper.cpp PROPERTIES
    COMPILE_FLAGS "-O3 -ffast-math"
)

# 主目标编译选项
target_compile_options(ryujinxjni PRIVATE
    -std=c++20
    -O3
    -DNDEBUG
    -march=armv8-a
    -DANDROID
    -D__ANDROID_API__=30
)

# 添加 FFmpeg 包含目录
target_include_directories(ryujinxjni PRIVATE
    ${FFMPEG_INCLUDE_DIR}
)

# 添加 Oboe 包含目录
target_include_directories(ryujinxjni PRIVATE
    ${oboe_SOURCE_DIR}/include
)

# 查找系统库
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(aaudio-lib aaudio)
find_library(android-lib android)
find_library(vulkan-lib vulkan)

# 链接所有库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${opensles-lib}
    ${aaudio-lib}
    ${android-lib}
    ${vulkan-lib}
    adrenotools
    oboe
    OpenAL
    ffmpeg-static-combined  # 链接 FFmpeg 静态库
)

# 设置 JNI 库输出路径
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)
set_target_properties(ryujinxjni PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 设置 OpenAL 输出目录
set_target_properties(OpenAL PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 构建 OpenSSL（如果需要）
cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif ()

# 输出构建信息
message(STATUS "Building for Android ABI: ${CMAKE_ANDROID_ARCH_ABI}")
message(STATUS "FFmpeg include directory: ${FFMPEG_INCLUDE_DIR}")
message(STATUS "Oboe source directory: ${oboe_SOURCE_DIR}")
message(STATUS "Output directory: ${JNI_PATH}")
