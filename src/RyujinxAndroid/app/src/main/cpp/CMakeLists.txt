# CMakeLists.txt
cmake_minimum_required(VERSION 3.22.1)
project("ryujinxjni")

# 设置C++标准
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# 启用详细构建日志（调试时建议开启）
set(CMAKE_VERBOSE_MAKEFILE ON)

# 引入FetchContent模块
include(FetchContent)

# 获取adrenotools依赖
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://github.com/bylaws/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1 # v1.0-14-gdeec5f7
)
FetchContent_MakeAvailable(adrenotools)

# 创建主库
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
)

# 查找NDK中的log库
find_library(log-lib log)

# 规范化JNI库路径
file(TO_CMAKE_PATH "../jniLibs/${CMAKE_ANDROID_ARCH_ABI}" JNI_PATH)
get_filename_component(JNI_PATH "${JNI_PATH}" ABSOLUTE)

# 检查OpenSSL预编译库是否存在
set(LIBCRYPTO_JNI_PATH "${JNI_PATH}/libcrypto.so")
set(LIBSSL_JNI_PATH "${JNI_PATH}/libssl.so")
if (NOT (EXISTS "${LIBCRYPTO_JNI_PATH}" AND EXISTS "${LIBSSL_JNI_PATH}"))
    message(STATUS "[OpenSSL] Prebuilt libraries not found. Building from source...")
    # 包含OpenSSL编译脚本
    file(RELATIVE_PATH REL_OPENSSL_PATH "${CMAKE_CURRENT_SOURCE_DIR}" "${CMAKE_SOURCE_DIR}/libryujinx/libs")
    include(${REL_OPENSSL_PATH}/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
    # 添加OpenSSL库路径
    link_directories(${CMAKE_LIBRARY_OUTPUT_DIRECTORY})
endif ()

# 检查FFmpeg预编译库是否存在
set(FFMPEG_LIBS
    "${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib/libavcodec.so"
    "${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib/libavformat.so"
    "${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib/libavutil.so"
)
set(FFMPEG_FOUND TRUE)
foreach(LIB ${FFMPEG_LIBS})
    if(NOT EXISTS "${LIB}")
        set(FFMPEG_FOUND FALSE)
    endif()
endforeach()

if(NOT FFMPEG_FOUND)
    message(STATUS "[FFmpeg] Libraries not found. Building from source...")
    # 包含FFmpeg编译脚本
    file(RELATIVE_PATH REL_FFMPEG_PATH "${CMAKE_CURRENT_SOURCE_DIR}" "${CMAKE_SOURCE_DIR}/libryujinx/libs")
    include(${REL_FFMPEG_PATH}/FFmpeg.cmake)
    add_dependencies(ryujinxjni ffmpeg)
    # 添加FFmpeg库路径
    link_directories(${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib)
endif ()

# 链接所有依赖库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    vulkan
    android
    adrenotools
    # 条件链接OpenSSL
    $<$<BOOL:${FFMPEG_FOUND}>:${LIBCRYPTO_JNI_PATH}>
    $<$<BOOL:${FFMPEG_FOUND}>:${LIBSSL_JNI_PATH}>
    # FFmpeg核心库
    avcodec
    avformat
    avutil
    swresample
    swscale
)

# 编译优化选项
target_compile_options(ryujinxjni PRIVATE
    -O3
    -fexceptions
    -frtti
)

# 包含Vulkan头文件（假设已安装Vulkan SDK）
find_package(Vulkan REQUIRED)
target_include_directories(ryujinxjni PRIVATE
    ${Vulkan_INCLUDE_DIR}
)

# 包含OpenSSL和FFmpeg头文件
target_include_directories(ryujinxjni PRIVATE
    ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/include
)
