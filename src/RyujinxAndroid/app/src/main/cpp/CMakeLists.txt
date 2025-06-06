include(FetchContent)
cmake_minimum_required(VERSION 3.22.1)
project("ryujinxjni")

# ================= 关键性能优化设置 =================
# 强制 Release 构建类型
set(CMAKE_BUILD_TYPE Release)

# 设置编译器优化级别 (-O3)
set(CMAKE_C_FLAGS_RELEASE "${CMAKE_C_FLAGS_RELEASE} -O3 -march=armv8.2-a+lse+fp16+dotprod")
set(CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE} -O3 -march=armv8.2-a+lse+fp16+dotprod")

# 启用 LTO (链接时优化)
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION ON)

# 启用 NEON 指令集
set(ANDROID_ARM_NEON TRUE)

# 使用 Clang 的 Polly 优化 (需要 NDK r21+)
# add_compile_options(-mllvm -polly -mllvm -polly-vectorizer=stripmine)

# 其他优化选项
add_compile_options(-ffunction-sections -fdata-sections)
add_link_options(-Wl,--gc-sections)

# ================= 原有配置 =================
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded)
set(SKIA_USE_SYSTEM_EXPAT OFF)
set(SKIA_ENABLE_PARAGRAPH ON)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://git.ryujinx.app/kenji-nx/libadrenotools.git
)

FetchContent_MakeAvailable(adrenotools)

add_library(ryujinxjni SHARED
            vulkan_wrapper.cpp
            ryujinx.cpp)

find_library(log-lib log)

# ================= 链接优化 =================
target_link_libraries(ryujinxjni
    ${log-lib}
    vulkan
    android
    adrenotools
    # 添加 LTO 链接选项
    -flto=thin
    # 针对大内存模型优化
    -Wl,-z,now
)

# ================= 其他优化 =================
# 设置 Android 特定优化
if(ANDROID)
    # 针对64位ARM优化
    add_compile_definitions(ANDROID_ARM64)
    add_compile_options(-mcpu=cortex-a75)  # 根据目标设备调整
    
    # 减少调试信息 (Release 构建)
    add_compile_options(-g0)
endif()

# ================= 原有 OpenSSL 部分 =================
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif()
