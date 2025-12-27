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
set(ALSOFT_BACKEND_AAUDIO ON CACHE BOOL "Enable AAudio backend")
set(ALSOFT_TESTS OFF CACHE BOOL "Disable tests")

# 添加优化选项以减少杂音和延迟
set(ALSOFT_CPUEXT_NEON ON CACHE BOOL "Enable NEON optimizations")

FetchContent_MakeAvailable(openal)

# 创建主库
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
    oboe_audio_renderer.cpp
)

# 创建硬件解码器库
add_library(
    hardware_decoder
    SHARED
    simple_hardware_decoder.cpp
)

# 添加编译定义以优化音频处理
target_compile_definitions(ryujinxjni PRIVATE
    RYUJINX_AUDIO_OPTIMIZE=1
)

# 为硬件解码器库添加编译定义
target_compile_definitions(hardware_decoder PRIVATE
    ANDROID=1
    __ANDROID__=1
    HAVE_FFMPEG=1
    __STDC_CONSTANT_MACROS=1
    __STDC_FORMAT_MACROS=1
)

# 为音频渲染器文件单独启用激进优化
set_source_files_properties(oboe_audio_renderer.cpp PROPERTIES
    COMPILE_FLAGS "-Ofast"
)

# 为主目标添加编译选项
target_compile_options(ryujinxjni PRIVATE
    -std=c++20
    -O3
    -DNDEBUG
    -march=armv8-a
)

# 为硬件解码器库添加编译选项
target_compile_options(hardware_decoder PRIVATE
    -std=c++20
    -O3
    -DNDEBUG
    -fPIC
    -march=armv8-a
    -fvisibility=hidden
)

# 查找系统库
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(aaudio-lib aaudio)
find_library(android-lib android)

# 添加 Oboe 包含目录
include_directories(${oboe_SOURCE_DIR}/include)

# 链接主库
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

# 链接硬件解码器库 - 先链接系统库
target_link_libraries(
    hardware_decoder
    ${log-lib}
    ${android-lib}
)

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
    add_dependencies(hardware_decoder openssl)
endif ()

# 检查 FFmpeg 库是否存在 - 检查所有必需的库
set(FFMPEG_LIBS_REQUIRED
    libavcodec.so
    libavutil.so
    libswresample.so
    libswscale.so
)

set(ALL_FFMPEG_LIBS_FOUND TRUE)
foreach(FFMPEG_LIB ${FFMPEG_LIBS_REQUIRED})
    cmake_path(APPEND JNI_PATH ${FFMPEG_LIB} OUTPUT_VARIABLE LIB_PATH)
    if(NOT EXISTS ${LIB_PATH})
        set(ALL_FFMPEG_LIBS_FOUND FALSE)
        message(STATUS "Missing FFmpeg library: ${FFMPEG_LIB}")
    endif()
endforeach()

# 构建 FFmpeg（如果预构建文件不存在）
if (NOT ALL_FFMPEG_LIBS_FOUND)
    message(STATUS "FFmpeg libraries not found, building from source...")
    
    # 包含 FFmpeg 编译脚本
    include(${CMAKE_CURRENT_SOURCE_DIR}/../../../../libryujinx/libs/FFmpeg.cmake)
    
    # 添加对 FFmpeg 的依赖
    add_dependencies(ryujinxjni ffmpeg)
    add_dependencies(hardware_decoder ffmpeg)
    
    # 设置 FFmpeg 包含目录
    target_include_directories(hardware_decoder PRIVATE
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/include
    )
    
    # 链接所有必需的 FFmpeg 库到硬件解码器
    target_link_libraries(hardware_decoder
        avcodec
        avutil
        swresample
        swscale
    )
    
    # 复制 FFmpeg 库到输出目录
    add_custom_command(TARGET ffmpeg POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E echo "Copying FFmpeg libraries to ${JNI_PATH}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavcodec.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavutil.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswresample.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswscale.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavformat.so
            ${JNI_PATH}
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavfilter.so
            ${JNI_PATH}
        COMMENT "Copying FFmpeg libraries to JNI directory"
    )
    
    # 添加 FFmpeg 头文件路径
    set(FFMPEG_INCLUDE_DIR ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/include)
    
else()
    message(STATUS "Using prebuilt FFmpeg libraries")
    
    # 导入所有必需的 FFmpeg 库
    add_library(avcodec SHARED IMPORTED)
    set_target_properties(avcodec PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libavcodec.so
    )

    add_library(avutil SHARED IMPORTED)
    set_target_properties(avutil PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libavutil.so
    )

    add_library(swresample SHARED IMPORTED)
    set_target_properties(swresample PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libswresample.so
    )

    add_library(swscale SHARED IMPORTED)
    set_target_properties(swscale PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libswscale.so
    )

    add_library(avformat SHARED IMPORTED)
    set_target_properties(avformat PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libavformat.so
    )

    add_library(avfilter SHARED IMPORTED)
    set_target_properties(avfilter PROPERTIES
        IMPORTED_LOCATION ${JNI_PATH}/libavfilter.so
    )
    
    # 链接所有必需的 FFmpeg 库到硬件解码器
    target_link_libraries(hardware_decoder
        avcodec
        avutil
        swresample
        swscale
        avformat
        avfilter
    )
    
    # 设置预编译 FFmpeg 的头文件路径
    set(FFMPEG_INCLUDE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../../../../libryujinx/libs/ffmpeg/include)
    
endif()

# 确保包含目录已设置
if(FFMPEG_INCLUDE_DIR)
    target_include_directories(hardware_decoder PRIVATE
        ${FFMPEG_INCLUDE_DIR}
    )
endif()

# 设置 OpenAL 输出目录
set_target_properties(OpenAL PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 设置主库输出目录
set_target_properties(ryujinxjni PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 设置硬件解码器库输出目录和名称
set_target_properties(hardware_decoder PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
    OUTPUT_NAME "hardware_decoder"  # 输出为 libhardware_decoder.so
    # 确保符号可见性
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)

# 安装后处理：确保所有依赖库都被复制到正确位置
add_custom_command(TARGET ryujinxjni POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:OpenAL>
        ${JNI_PATH}
    COMMENT "Copying OpenAL library to JNI directory"
)

# 确保硬件解码器库也被正确复制
add_custom_command(TARGET hardware_decoder POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E echo "Hardware decoder library built: $<TARGET_FILE:hardware_decoder>"
    COMMENT "Hardware decoder build completed"
)

# 验证所有库
add_custom_target(verify_libraries
    COMMAND ${CMAKE_COMMAND} -E echo "Verifying library sizes..."
    COMMAND ${CMAKE_COMMAND} -E make_directory ${JNI_PATH}
    COMMAND bash -c "ls -la ${JNI_PATH}/*.so 2>/dev/null || echo 'No libraries found'"
    COMMAND ${CMAKE_COMMAND} -E echo "Library verification completed"
    DEPENDS ryujinxjni hardware_decoder
    COMMENT "Verifying output libraries"
)

message(STATUS "JNI output path: ${JNI_PATH}")
message(STATUS "Hardware decoder library will be built as: libhardware_decoder.so")
message(STATUS "Required FFmpeg libraries: avcodec, avutil, swresample, swscale")
