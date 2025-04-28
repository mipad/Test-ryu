include(FetchContent)

# Minimum CMake version
cmake_minimum_required(VERSION 3.22.1)

# Project declaration
project("ryujinxjni" LANGUAGES CXX)

# ------------------------------
# 编译器优化配置
# ------------------------------
add_compile_options(
    # 核心优化
    -O3
    -ffast-math          # 快速浮点运算（注意精度风险）
    -flto                # 启用 LTO
    -fuse-ld=lld         # 强制使用 LLD 链接器
    
    # ARM64 指令集优化
    -march=armv8.2-a+dotprod+crypto  # 启用 ARMv8.2 扩展（包含加密指令）
    -mtune=cortex-a75    # 针对 Cortex-A75/A76 优化流水线
)

# 启用跨模块优化（LTO）
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION TRUE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)  # 确保 PIC 兼容性

# C++ 标准配置
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ------------------------------
# 依赖管理
# ------------------------------
# Adrenotools 依赖
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://github.com/bylaws/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
)
FetchContent_MakeAvailable(adrenotools)

# ------------------------------
# 目标库定义
# ------------------------------
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
)

# ------------------------------
# 链接配置
# ------------------------------
find_library(log-lib log)

target_link_libraries(
    ryujinxjni
    ${log-lib}
    -lvulkan
    -landroid
    adrenotools
    -latomic       # 原子操作支持
    -lc++_shared   # 显式链接 NDK 的 libc++_shared
)

# ------------------------------
# OpenSSL 依赖处理
# ------------------------------
set(JNI_PATH "${CMAKE_SOURCE_DIR}/../jniLibs/${ANDROID_ABI}")
set(LIBCRYPTO_JNI_PATH "${JNI_PATH}/libcrypto.so")
set(LIBSSL_JNI_PATH "${JNI_PATH}/libssl.so")

if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    message(STATUS "Building OpenSSL from source...")
    include(${CMAKE_SOURCE_DIR}/../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
else()
    message(STATUS "Using prebuilt OpenSSL libraries")
    target_link_libraries(ryujinxjni
        "${JNI_PATH}/libcrypto.so"
        "${JNI_PATH}/libssl.so"
    )
endif()
