cmake_minimum_required(VERSION 3.22.1)

# 项目名称和类型
project("ryujinxjni" LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# 设置 FFmpeg 和 Vulkan 库路径
set(FFMPEG_LIB_DIR ${CMAKE_SOURCE_DIR}/../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
set(VULKAN_SDK_DIR $ENV{VULKAN_SDK})

# 引入 adrenotools 依赖
include(FetchContent)
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://github.com/bylaws/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1 # v1.0-14-gdeec5f7
)
FetchContent_MakeAvailable(adrenotools)

# 定义目标库和源文件
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
)

# 查找系统库（Log、Android、Vulkan）
find_library(log-lib log)
find_library(android-lib android)
find_library(vulkan-lib vulkan)

# 查找 FFmpeg 库
find_library(avcodec-lib avcodec PATHS ${FFMPEG_LIB_DIR} REQUIRED)
find_library(avutil-lib avutil PATHS ${FFMPEG_LIB_DIR} REQUIRED)
find_library(swresample-lib swresample PATHS ${FFMPEG_LIB_DIR} REQUIRED)
find_library(swscale-lib swscale PATHS ${FFMPEG_LIB_DIR} REQUIRED)

# 查找 Vulkan 验证层库
find_library(vulkan-validation-layer-lib
    NAMES VkLayer_khronos_validation
    PATHS ${VULKAN_SDK_DIR}/lib
    REQUIRED
)

# 链接所有库到目标
target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${android-lib}
    ${vulkan-lib}
    ${avcodec-lib}
    ${avutil-lib}
    ${swresample-lib}
    ${swscale-lib}
    ${vulkan-validation-layer-lib}
    adrenotools
)

# 包含头文件目录（如果 FFmpeg 头文件在 jniLibs 中）
target_include_directories(ryujinxjni
    PRIVATE
    ${FFMPEG_LIB_DIR}/../include  # 假设头文件在 jniLibs/../include
)

# 处理 OpenSSL 依赖
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    message(STATUS "OpenSSL 未预编译，开始构建...")
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
else()
    message(STATUS "使用预编译的 OpenSSL 库")
endif()

# 设置生成 .so 的输出路径（可选）
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${PROJECT_SOURCE_DIR}/../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
