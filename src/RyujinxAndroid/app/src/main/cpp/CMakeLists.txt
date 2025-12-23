# CMakeLists.txt - 简化版
include(FetchContent)

cmake_minimum_required(VERSION 3.22.1)
project("ryujinxjni")

set(CMAKE_BUILD_TYPE Release)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)
set(ANDROID_STL c++_shared)
set(ANDROID_ARM_NEON TRUE)

# 获取依赖库
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://git.ryujinx.app/kenji-nx/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
)
FetchContent_MakeAvailable(adrenotools)

FetchContent_Declare(
    oboe
    GIT_REPOSITORY https://github.com/google/oboe.git
    GIT_TAG        1.10.0
)
FetchContent_MakeAvailable(oboe)

FetchContent_Declare(
    openal
    GIT_REPOSITORY https://github.com/kcat/openal-soft.git
    GIT_TAG        1.24.3
)
set(ALSOFT_REQUIRE_OPENSL ON)
set(ALSOFT_BACKEND_OPENSL ON)
set(ALSOFT_BACKEND_AAUDIO ON)
set(ALSOFT_TESTS OFF)
set(ALSOFT_CPUEXT_NEON ON)
FetchContent_MakeAvailable(openal)

# 主库源文件
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
    oboe_audio_renderer.cpp
    ffmpeg_adapter.cpp  # 添加FFmpeg适配器
)

# 编译选项
target_compile_options(ryujinxjni PRIVATE
    -std=c++20
    -O3
    -DNDEBUG
    -march=armv8.2-a+fp16+dotprod
    -mtune=cortex-a78
    -flto
    -ffat-lto-objects
    -fno-exceptions
    -fno-rtti
    -fvisibility=default  # 重要：导出所有符号
)

# 链接选项
target_link_options(ryujinxjni PRIVATE
    -flto
    -Wl,--gc-sections
    -Wl,--export-dynamic  # 导出动态符号
)

# 系统库
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(aaudio-lib aaudio)
find_library(android-lib android)

# 包含目录
include_directories(${oboe_SOURCE_DIR}/include)

# 链接库
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

# 设置输出路径
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

# FFmpeg 静态库配置（只构建需要的库）
include(ExternalProject)

set(PROJECT_ENV "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}")
set(ANDROID_TOOLCHAIN_ROOT ${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64)
set(ANDROID_SYSROOT ${ANDROID_TOOLCHAIN_ROOT}/sysroot)
set(ANDROID_PLATFORM aarch64-linux-android)
set(ANDROID_API_LEVEL 30)

# 简化 FFmpeg 配置，只构建需要的库
set(FFMPEG_CONFIGURE_COMMAND
    <SOURCE_DIR>/configure
    --prefix=${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install
    --cross-prefix=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}${ANDROID_API_LEVEL}-
    --target-os=android
    --arch=aarch64
    --cc=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}${ANDROID_API_LEVEL}-clang
    --cxx=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}${ANDROID_API_LEVEL}-clang++
    --nm=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-nm
    --strip=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-strip
    --enable-cross-compile
    --sysroot=${ANDROID_SYSROOT}
    --extra-cflags=-O3
    --extra-cflags=-fPIC
    --extra-cflags=-march=armv8.2-a+fp16+dotprod
    --extra-cflags=-mtune=cortex-a78
    --extra-cflags=-DANDROID
    --extra-cflags=-D__ANDROID_API__=${ANDROID_API_LEVEL}
    --extra-ldflags=-Wl,--hash-style=both
    --extra-ldexeflags=-pie
    --enable-static
    --disable-shared
    --disable-programs
    --disable-doc
    --disable-avformat
    --disable-avfilter  # 不需要的库
    --enable-avcodec
    --enable-avutil
    --enable-swresample
    --enable-swscale
    --enable-asm
    --enable-neon
    --enable-inline-asm
    --enable-jni
    --enable-mediacodec
    --enable-hwaccels
    --enable-decoder=*
    --disable-encoder=*
    --enable-zlib
    --disable-small
    --enable-optimizations
    --disable-debug
    --pkg-config=pkg-config
)

ExternalProject_Add(
    ffmpeg-minimal
    GIT_REPOSITORY              https://github.com/FFmpeg/FFmpeg.git
    GIT_TAG                     n6.1.4
    GIT_PROGRESS                1
    GIT_SHALLOW                 1
    UPDATE_COMMAND              ""
    CONFIGURE_COMMAND           ${CMAKE_COMMAND} -E env ${PROJECT_ENV}
                                ${FFMPEG_CONFIGURE_COMMAND}
    BUILD_COMMAND               ${CMAKE_COMMAND} -E env ${PROJECT_ENV}
                                ${MAKE_COMMAND} -j${CMAKE_BUILD_PARALLEL_LEVEL}
    INSTALL_COMMAND             ${MAKE_COMMAND} install
    BUILD_IN_SOURCE            1
    BUILD_BYPRODUCTS
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavcodec.a
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavutil.a
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswresample.a
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswscale.a
)

# 导入静态库
add_library(avcodec-static STATIC IMPORTED)
set_target_properties(avcodec-static PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavcodec.a
)
add_dependencies(avcodec-static ffmpeg-minimal)

add_library(avutil-static STATIC IMPORTED)
set_target_properties(avutil-static PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavutil.a
)
add_dependencies(avutil-static ffmpeg-minimal)

add_library(swresample-static STATIC IMPORTED)
set_target_properties(swresample-static PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswresample.a
)
add_dependencies(swresample-static ffmpeg-minimal)

add_library(swscale-static STATIC IMPORTED)
set_target_properties(swscale-static PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswscale.a
)
add_dependencies(swscale-static ffmpeg-minimal)

# 链接 FFmpeg 静态库到主库
target_link_libraries(ryujinxjni
    avcodec-static
    avutil-static
    swresample-static
    swscale-static
)

# 添加 FFmpeg 头文件
target_include_directories(ryujinxjni PRIVATE
    ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/include
)

# 设置目标属性
set_target_properties(ryujinxjni PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 设置 OpenAL 输出目录
set_target_properties(OpenAL PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

message(STATUS "Build configuration completed")
message(STATUS "JNI output path: ${JNI_PATH}")
