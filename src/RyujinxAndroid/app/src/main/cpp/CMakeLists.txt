cmake_minimum_required(VERSION 3.18...3.31)

# 显式包含 Android NDK 工具链（关键修复）
include($ENV{ANDROID_NDK}/build/cmake/android.toolchain.cmake)
set(CMAKE_SYSTEM_NAME Android)
set(CMAKE_ANDROID_ARCH_ABI arm64-v8a)  # 根据实际架构调整

project("ryujinxjni")

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# 设置库输出路径
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_SOURCE_DIR}/../../../../jniLibs/${CMAKE_ANDROID_ARCH_ABI})

# 引入 adrenotools 依赖
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://github.com/bylaws/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
)
FetchContent_MakeAvailable(adrenotools)

# 添加主库
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
)

# 强制生成 .so 文件（避免意外生成 .dll）
set_target_properties(ryujinxjni PROPERTIES SUFFIX ".so")

# 引入 OpenSSL 编译
include(../../../../libryujinx/libs/OpenSSL.cmake)
add_dependencies(ryujinxjni openssl)

# 查找 Android 原生库
find_library(log-lib log)
find_library(android-lib android)
find_library(vulkan-lib vulkan)

# 链接依赖项
target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${android-lib}
    ${vulkan-lib}
    adrenotools
    # 直接链接 OpenSSL 静态库
    ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/libcrypto.a
    ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/libssl.a
)
