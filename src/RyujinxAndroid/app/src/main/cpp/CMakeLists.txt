include(FetchContent)

# Sets the minimum version of CMake required to build the native library.
cmake_minimum_required(VERSION 3.22.1)

# Declares and names the project.
project("ryujinxjni")

# 在项目开始时强制设置链接器
set(CMAKE_SHARED_LINKER_FLAGS "-fuse-ld=lld")
set(CMAKE_EXE_LINKER_FLAGS "-fuse-ld=lld")
set(CMAKE_MODULE_LINKER_FLAGS "-fuse-ld=lld")

# 设置构建类型
set(CMAKE_BUILD_TYPE Release)

# 启用 LTO
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION TRUE)
set(CMAKE_POLICY_DEFAULT_CMP0069 NEW)

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

# 在配置 adrenotools 之前设置全局变量
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -flto=thin")
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -flto=thin")

FetchContent_MakeAvailable(adrenotools)

# ========== 关键修复：为 adrenotools 目标强制设置正确的链接器 ==========
# 这些目标需要明确的链接器设置来覆盖内部硬编码的 gold 链接器
if(TARGET hook_impl)
    target_link_options(hook_impl PRIVATE -fuse-ld=lld)
    get_target_property(hook_impl_flags hook_impl LINK_FLAGS)
    if(hook_impl_flags)
        string(REPLACE "-fuse-ld=gold" "-fuse-ld=lld" hook_impl_flags "${hook_impl_flags}")
        set_target_properties(hook_impl PROPERTIES LINK_FLAGS "${hook_impl_flags}")
    endif()
endif()

if(TARGET main_hook)
    target_link_options(main_hook PRIVATE -fuse-ld=lld)
endif()

if(TARGET file_redirect_hook)
    target_link_options(file_redirect_hook PRIVATE -fuse-ld=lld)
endif()

if(TARGET gsl_alloc_hook)
    target_link_options(gsl_alloc_hook PRIVATE -fuse-ld=lld)
endif()

if(TARGET linkernsbypass)
    target_link_options(linkernsbypass PRIVATE -fuse-ld=lld)
endif()

if(TARGET adrenotools)
    target_link_options(adrenotools PRIVATE -fuse-ld=lld)
endif()
# ========== 修复结束 ==========

# 为 adrenotools 相关目标添加 PAGE_SIZE 定义
if(TARGET adrenotools)
    target_compile_definitions(adrenotools PRIVATE PAGE_SIZE=4096)
endif()

if(TARGET linkernsbypass)
    target_compile_definitions(linkernsbypass PRIVATE PAGE_SIZE=4096)
endif()

# 使用 FetchContent 获取 Oboe 源码
FetchContent_Declare(
    oboe
    GIT_REPOSITORY https://github.com/google/oboe.git
    GIT_TAG        1.10.0
)

FetchContent_MakeAvailable(oboe)

# 使用 FetchContent 获取标准 OpenAL Soft
FetchContent_Declare(
    openal
    GIT_REPOSITORY https://github.com/kcat/openal-soft.git
    GIT_TAG        1.24.3
)

set(ALSOFT_REQUIRE_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_TESTS OFF CACHE BOOL "Disable tests")
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

# 添加编译定义以优化音频处理
target_compile_definitions(ryujinxjni PRIVATE
    RYUJINX_AUDIO_OPTIMIZE=1
)

# 为主目标添加编译选项（启用 LTO）
target_compile_options(ryujinxjni PRIVATE
    -std=c++20
    -flto=thin
    -O3
    -DNDEBUG
    -march=armv8-a
    -funroll-loops
)

# 为音频渲染器文件单独启用优化
set_source_files_properties(oboe_audio_renderer.cpp PROPERTIES
    COMPILE_FLAGS "-O3 -ffast-math"
)

# 查找系统库
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(android-lib android)

# 添加 Oboe 包含目录
include_directories(${oboe_SOURCE_DIR}/include)

# 链接库
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

# 为主目标设置链接器选项（启用 LTO）
target_link_options(ryujinxjni PRIVATE
    -flto=thin
    -fuse-ld=lld
)

# 设置 JNI 库输出路径
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

# 构建 OpenSSL（如果预构建文件不存在）
if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif ()

# 设置 OpenAL 输出目录
set_target_properties(OpenAL PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

message(STATUS "LTO enabled with lld linker - Configuration completed")
