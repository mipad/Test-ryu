cmake_minimum_required(VERSION 3.22.1)
project("ryujinxjni" LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# 设置 FFmpeg 库路径（假设 jniLibs 在 app/src/main 下）
set(FFMPEG_LIB_DIR ${CMAKE_SOURCE_DIR}/../../../../src/main/jniLibs/${CMAKE_ANDROID_ARCH_ABI})
if (NOT EXISTS ${FFMPEG_LIB_DIR})
    message(FATAL_ERROR "FFmpeg 库路径不存在: ${FFMPEG_LIB_DIR}")
endif()

# 引入 adrenotools
include(FetchContent)
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://github.com/bylaws/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
)
FetchContent_MakeAvailable(adrenotools)

# 定义目标库
add_library(ryujinxjni SHARED vulkan_wrapper.cpp ryujinx.cpp)

# 查找系统库
find_library(log-lib log)
find_library(android-lib android)
find_library(vulkan-lib vulkan)

# 查找 FFmpeg 库（按实际文件名调整）
find_library(avcodec-lib avcodec PATHS ${FFMPEG_LIB_DIR} REQUIRED)
find_library(avutil-lib avutil PATHS ${FFMPEG_LIB_DIR} REQUIRED)
find_library(swresample-lib swresample PATHS ${FFMPEG_LIB_DIR} REQUIRED)
find_library(swscale-lib swscale PATHS ${FFMPEG_LIB_DIR} REQUIRED)

# 链接所有库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${android-lib}
    ${vulkan-lib}
    ${avcodec-lib}
    ${avutil-lib}
    ${swresample-lib}
    ${swscale-lib}
    adrenotools
)

# 处理 OpenSSL 依赖
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)
cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif()
