# FFmpeg.cmake
include(ExternalProject)

find_program(MAKE_COMMAND NAMES make)

# 设置基础环境变量
set(PROJECT_ENV
    "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}"
)

# 根据宿主系统设置工具链路径和环境变量
if (CMAKE_HOST_WIN32)
    # Windows环境需要额外配置
    list(APPEND PROJECT_ENV "Path=${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/windows-x86_64/bin;$ENV{Path}")
    set(TOOLCHAIN_PREFIX "${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/windows-x86_64")
elseif (CMAKE_HOST_UNIX)
    # Linux/macOS环境
    list(APPEND PROJECT_ENV "PATH=${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64/bin:$ENV{PATH}")
    set(TOOLCHAIN_PREFIX "${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64")
else ()
    message(FATAL_ERROR "Unsupported host system: ${CMAKE_HOST_SYSTEM_NAME}")
endif ()

# 根据目标架构设置FFmpeg交叉编译参数
if (CMAKE_ANDROID_ARCH_ABI STREQUAL "arm64-v8a")
    set(TARGET_ARCH "aarch64")
    set(TARGET_TRIPLE "aarch64-linux-android")
    set(FFMPEG_CPU "armv8-a")
elseif (CMAKE_ANDROID_ARCH_ABI STREQUAL "armeabi-v7a")
    set(TARGET_ARCH "arm")
    set(TARGET_TRIPLE "armv7a-linux-androideabi")
    set(FFMPEG_CPU "armv7-a")
elseif (CMAKE_ANDROID_ARCH_ABI STREQUAL "x86_64")
    set(TARGET_ARCH "x86_64")
    set(TARGET_TRIPLE "x86_64-linux-android")
    set(FFMPEG_CPU "x86_64")
elseif (CMAKE_ANDROID_ARCH_ABI STREQUAL "x86")
    set(TARGET_ARCH "i686")
    set(TARGET_TRIPLE "i686-linux-android")
    set(FFMPEG_CPU "i686")
else ()
    message(FATAL_ERROR "Unsupported ABI: ${CMAKE_ANDROID_ARCH_ABI}")
endif ()

# 定义FFmpeg编译选项
set(FFMPEG_CONFIGURE_ARGS
    --target-os=android
    --arch=${TARGET_ARCH}
    --cpu=${FFMPEG_CPU}
    --enable-cross-compile
    --cross-prefix=${TOOLCHAIN_PREFIX}/bin/${TARGET_TRIPLE}${CMAKE_ANDROID_API}-
    --sysroot=${TOOLCHAIN_PREFIX}/sysroot
    --prefix=${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
    --enable-shared
    --disable-static
    --disable-programs
    --disable-doc
    --disable-avdevice
    --disable-postproc
    --disable-network
    --enable-gpl
    --enable-jni
    --extra-cflags="-O3 -fPIC -I${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/include"
    --extra-ldflags="-L${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib"
    --pkg-config=pkg-config
)

# 添加ExternalProject定义
ExternalProject_Add(
    ffmpeg
    GIT_REPOSITORY  https://git.ffmpeg.org/ffmpeg.git
    GIT_TAG         n6.1.1  # 指定FFmpeg版本
    CONFIGURE_COMMAND ${CMAKE_COMMAND} -E env ${PROJECT_ENV}
        <SOURCE_DIR>/configure ${FFMPEG_CONFIGURE_ARGS}
    BUILD_COMMAND   ${CMAKE_COMMAND} -E env ${PROJECT_ENV}
        ${MAKE_COMMAND} -j${NPROC}
    INSTALL_COMMAND ${MAKE_COMMAND} install
    BUILD_IN_SOURCE 1
)

# 依赖项处理（假设OpenSSL已通过前面的步骤编译）
add_dependencies(ffmpeg openssl)
