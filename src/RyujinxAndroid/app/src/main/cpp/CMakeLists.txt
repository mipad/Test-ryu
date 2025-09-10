# CMakeLists.txt (终极优化版：专为消除滋滋声 + 最大化音频性能)
include(FetchContent)

# 设置最低 CMake 版本
cmake_minimum_required(VERSION 3.22.1)

# 项目名称
project("ryujinxjni")

# =============== 基础配置 ===============
set(CMAKE_BUILD_TYPE Release)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# Android 特定设置
set(ANDROID_STL c++_shared)
set(ANDROID_ARM_NEON TRUE)

# =============== 性能编译选项 ===============
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded)

# ✅ 全局优化标志：低延迟 + 高性能
target_compile_options(ryujinxjni PRIVATE
    -O3
    -DNDEBUG
    -ffast-math
    -fno-exceptions          # 减少开销
    -fno-rtti                # 减少开销
    -flto                    # 链接时优化
    -march=armv8-a
    -mfpu=neon
    -mfloat-abi=hard
)

# =============== 获取依赖库 ===============

# --- adrenotools ---
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://git.ryujinx.app/kenji-nx/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
)
FetchContent_MakeAvailable(adrenotools)

# --- Oboe (强制低延迟配置) ---
FetchContent_Declare(
    oboe
    GIT_REPOSITORY https://github.com/google/oboe.git
    GIT_TAG        1.9.3
)
FetchContent_MakeAvailable(oboe)

# --- OpenAL Soft (极致低延迟配置) ---
FetchContent_Declare(
    openal
    GIT_REPOSITORY https://github.com/kcat/openal-soft.git
    GIT_TAG        1.24.3
)

# ✅ OpenAL Soft 低延迟优化配置
set(ALSOFT_REQUIRE_OPENSL OFF CACHE BOOL "Don't require OpenSL")
set(ALSOFT_BACKEND_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_AAUDIO ON CACHE BOOL "Enable AAudio backend for lower latency") # ✅ 优先AAudio
set(ALSOFT_NO_CONFIG_UTIL ON CACHE BOOL "Disable config utility")
set(ALSOFT_UTILS OFF CACHE BOOL "Disable utils")
set(ALSOFT_EXAMPLES OFF CACHE BOOL "Disable examples")
set(ALSOFT_INSTALL OFF CACHE BOOL "Disable installation")
set(ALSOFT_TESTS OFF CACHE BOOL "Disable tests")

# ✅ 性能优化
set(ALSOFT_CPUEXT_NEON ON CACHE BOOL "Enable NEON optimizations")
set(ALSOFT_REQUIRE_NEON OFF CACHE BOOL "Don't require NEON")

# ✅ 延迟优化：禁用所有效果处理器
set(ALSOFT_HRTF_DISABLE ON CACHE BOOL "Disable HRTF" FORCE)
set(ALSOFT_AMBIENT_CHANNELS 0 CACHE STRING "Ambient channels")
set(ALSOFT_DEFAULT_REVERB OFF CACHE BOOL "Disable default reverb")
set(ALSOFT_DEFAULT_ECHO OFF CACHE BOOL "Disable default echo")
set(ALSOFT_DEFAULT_MODULATION OFF CACHE BOOL "Disable default modulation")

# ✅ 缓冲区优化（关键！）
set(ALSOFT_PERIOD_SIZE 192 CACHE STRING "Period size in frames (lower = lower latency)") # 默认960太高
set(ALSOFT_PERIODS 3 CACHE STRING "Number of periods (2-4 recommended)") # 减少缓冲区层级

FetchContent_MakeAvailable(openal)

# =============== 创建主库 ===============
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
    oboe_audio_renderer.cpp
)

# ✅ 添加音频专用编译定义
target_compile_definitions(ryujinxjni PRIVATE
    RYUJINX_AUDIO_OPTIMIZE=1
    OBOE_ENABLE_AAUDIO=1           # 强制启用AAudio
    OBOE_LOW_LATENCY=1             # 启用低延迟模式
    OBOE_PERFORMANCE_MODE_LOW_LATENCY=1
    # 如果需要调试，可临时添加：
    # OBOE_ENABLE_LOGGING=1
)

# =============== 链接库 ===============
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(android-lib android)

# 包含目录
include_directories(${oboe_SOURCE_DIR}/include)

# 链接
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

# =============== OpenSSL 集成 ===============
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif ()

# 设置 OpenAL 输出路径
set_target_properties(OpenAL PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

# =============== 调试符号（可选） ===============
# 在 Release 模式下保留调试符号，便于崩溃分析
set_target_properties(ryujinxjni PROPERTIES
    LINK_FLAGS "-Wl,--build-id"
)

# ✅ 生成 .map 文件（可选，用于分析符号）
set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -Wl,-Map=${CMAKE_PROJECT_NAME}.map")
