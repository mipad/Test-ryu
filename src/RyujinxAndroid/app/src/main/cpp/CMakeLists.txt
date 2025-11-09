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

# 使用 FetchContent 获取 Oboe 源码
FetchContent_Declare(
    oboe
    GIT_REPOSITORY https://github.com/google/oboe.git
    GIT_TAG        1.10.0  # 使用与 Gradle 依赖相同的版本
)

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
)

# 添加编译定义以优化音频处理
target_compile_definitions(ryujinxjni PRIVATE
    RYUJINX_AUDIO_OPTIMIZE=1
    # 针对天玑8100的优化
    RYUJINX_MTK_OPTIMIZE=1
    # 如果需要，可以添加更多音频优化标志
)

# 添加编译选项
target_compile_options(ryujinxjni PRIVATE
    -std=c++20
    -O3
    -DNDEBUG
    -ffast-math
    -march=armv8.2-a+fp16+dotprod+crypto  # 天玑8100支持的指令集
    -mtune=cortex-a78  # 天玑8100的大核架构
    -funsafe-math-optimizations
    -ftree-vectorize
)

# 查找系统库
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(android-lib android)

# 添加 Oboe 包含目录
include_directories(${oboe_SOURCE_DIR}/include)

# 设置 JNI 库输出路径
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

# 检查 OpenSSL 库是否存在
cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

# 构建 OpenSSL（如果预构建文件不存在）
if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif ()

# 检查 FFmpeg 库是否存在
cmake_path(APPEND JNI_PATH libavcodec.so OUTPUT_VARIABLE LIBAVCODEC_JNI_PATH)
cmake_path(APPEND JNI_PATH libavutil.so OUTPUT_VARIABLE LIBAVUTIL_JNI_PATH)
cmake_path(APPEND JNI_PATH libavformat.so OUTPUT_VARIABLE LIBAVFORMAT_JNI_PATH)
cmake_path(APPEND JNI_PATH libswresample.so OUTPUT_VARIABLE LIBSWRESAMPLE_JNI_PATH)
cmake_path(APPEND JNI_PATH libswscale.so OUTPUT_VARIABLE LIBSWSCALE_JNI_PATH)
cmake_path(APPEND JNI_PATH libavfilter.so OUTPUT_VARIABLE LIBAVFILTER_JNI_PATH)

# 构建 FFmpeg（如果预构建文件不存在）- 使用完整配置
if (NOT (EXISTS ${LIBAVCODEC_JNI_PATH} AND EXISTS ${LIBAVUTIL_JNI_PATH} AND 
         EXISTS ${LIBAVFORMAT_JNI_PATH} AND EXISTS ${LIBSWRESAMPLE_JNI_PATH} AND
         EXISTS ${LIBSWSCALE_JNI_PATH} AND EXISTS ${LIBAVFILTER_JNI_PATH}))
    message(STATUS "FFmpeg libraries not found, building from source with full configuration...")
    
    # 包含 FFmpeg 编译脚本
    include(../../../../libryujinx/libs/FFmpeg.cmake)
    add_dependencies(ryujinxjni ffmpeg)
    
    # 设置 FFmpeg 包含目录
    target_include_directories(ryujinxjni PRIVATE
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/include
    )
    
    # 链接 FFmpeg 库
    target_link_libraries(ryujinxjni
        avcodec
        avutil
        avformat
        swresample
        swscale
        avfilter
    )
    
    # 添加 FFmpeg 编译定义
    target_compile_definitions(ryujinxjni PRIVATE
        HAVE_FFMPEG=1
        __STDC_CONSTANT_MACROS=1
    )
    
    # 设置 FFmpeg 输出目录
    set_target_properties(avcodec avutil avformat swresample swscale avfilter PROPERTIES
        LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
        ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
        RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
    )
    
    # 复制 FFmpeg 库到输出目录
    add_custom_command(TARGET ffmpeg POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavcodec.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavutil.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavformat.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswresample.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswscale.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavfilter.so
            ${JNI_PATH}
        COMMENT "Copying FFmpeg libraries to JNI directory"
    )
    
    # 添加自定义目标来显示构建状态
    add_custom_target(ffmpeg_status
        DEPENDS ffmpeg
        COMMENT "FFmpeg build completed successfully"
    )
else()
    message(STATUS "Using prebuilt FFmpeg libraries")
    
    # 导入预编译的 FFmpeg 库
    add_library(avcodec SHARED IMPORTED)
    set_target_properties(avcodec PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libavcodec.so
    )

    add_library(avutil SHARED IMPORTED)
    set_target_properties(avutil PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libavutil.so
    )

    add_library(avformat SHARED IMPORTED)
    set_target_properties(avformat PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libavformat.so
    )

    add_library(swresample SHARED IMPORTED)
    set_target_properties(swresample PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libswresample.so
    )

    add_library(swscale SHARED IMPORTED)
    set_target_properties(swscale PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libswscale.so
    )

    add_library(avfilter SHARED IMPORTED)
    set_target_properties(avfilter PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libavfilter.so
    )
    
    # 链接预编译的 FFmpeg 库
    target_link_libraries(ryujinxjni
        avcodec
        avutil
        avformat
        swresample
        swscale
        avfilter
    )
    
    # 添加 FFmpeg 编译定义
    target_compile_definitions(ryujinxjni PRIVATE
        HAVE_FFMPEG=1
        __STDC_CONSTANT_MACROS=1
    )
    
    # 设置预编译 FFmpeg 的头文件路径
    target_include_directories(ryujinxjni PRIVATE
        ../../../../libryujinx/libs/ffmpeg/include
    )
endif()

# 链接其他库
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

# 设置 OpenAL 输出目录
set_target_properties(OpenAL PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 针对天玑8100的 Mali GPU 优化
target_compile_definitions(ryujinxjni PRIVATE
    MALI_GPU=1
    # Mali GPU 特定优化
    USE_MALI_OPTIMIZATIONS=1
    # 天玑8100特定优化
    DIMENSITY_8100=1
    # Mali GPU 硬件加速
    HAVE_MALI_GPU=1
    USE_MEDIACODEC=1
)

# 添加针对 ARM64 和 Mali 的特定编译选项
if(CMAKE_ANDROID_ARCH_ABI STREQUAL "arm64-v8a")
    target_compile_options(ryujinxjni PRIVATE
        -mcpu=cortex-a78  # 天玑8100大核
        -mtune=cortex-a55  # 天玑8100小核
        -march=armv8.2-a+fp16+dotprod+crypto  # 天玑8100支持的指令集
    )
endif()

# 设置目标属性
set_target_properties(ryujinxjni PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 安装后处理：确保所有依赖库都被复制到正确位置
add_custom_command(TARGET ryujinxjni POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:OpenAL>
        ${JNI_PATH}
    COMMENT "Copying OpenAL library to JNI directory"
)

# 添加验证步骤确保所有库都存在
add_custom_target(verify_libraries
    COMMAND ${CMAKE_COMMAND} -E echo "Verifying library sizes..."
    COMMAND ${CMAKE_COMMAND} -E make_directory ${JNI_PATH}
    COMMAND bash -c "ls -la ${JNI_PATH}/*.so && echo 'Library verification completed'"
    DEPENDS ryujinxjni
    COMMENT "Verifying output libraries"
)

message(STATUS "Build configuration for Dimensity 8100 (ARM64, Mali GPU) completed")
message(STATUS "JNI output path: ${JNI_PATH}")
message(STATUS "FFmpeg support: Enabled with full features")