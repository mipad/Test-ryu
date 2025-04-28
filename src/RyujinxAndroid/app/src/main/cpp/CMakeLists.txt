include(FetchContent)

# For more information about using CMake with Android Studio, read the
# documentation: https://d.android.com/studio/projects/add-native-code.html

# Sets the minimum version of CMake required to build the native library.
cmake_minimum_required(VERSION 3.22.1)

# Declares and names the project.
project("ryujinxjni")

# 设置编译优化选项
add_compile_options(
    -O3                   # 最高优化等级
    -ffast-math           # 浮点快速运算
    -flto=thin            # 启用 ThinLTO
    -march=armv8.2-a+dotprod  # ARMv8.2 指令集扩展
    -mtune=cortex-a75     # 针对 Cortex-A75 优化
)

# 启用链接时优化（LTO）
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION TRUE)

# 强制启用 NEON 指令集（仅限 arm64-v8a）
if(ANDROID_ABI STREQUAL "arm64-v8a")
    add_compile_options(-mfpu=neon -mfloat-abi=softfp)
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# 声明并获取 adrenotools 依赖
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://github.com/bylaws/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1 # v1.0-14-gdeec5f7
)

FetchContent_MakeAvailable(adrenotools)

# 创建共享库
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
)

# 查找系统库
find_library(
    log-lib
    log
)

# 链接库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    -lvulkan
    -landroid
    adrenotools
    -latomic         # 添加原子操作支持
    -lc++_shared     # 显式链接 C++ 共享库
)

# 检查预编译的 OpenSSL 库是否存在
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    message(STATUS "Building OpenSSL from source...")
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
else()
    message(STATUS "Using prebuilt OpenSSL libraries")
endif()
