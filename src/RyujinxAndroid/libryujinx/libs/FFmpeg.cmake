# FFmpeg.cmake
include(ExternalProject)
include(ProcessorCount)

# 获取逻辑CPU核心数
ProcessorCount(NPROC)
if(NOT NPROC)
    set(NPROC 4)
endif()

# 根据Android ABI设置FFmpeg目标平台
if (CMAKE_ANDROID_ARCH_ABI STREQUAL "arm64-v8a")
    set(FFMPEG_ARCH "aarch64")
    set(FFMPEG_TARGET_TRIPLE "aarch64-linux-android")
    set(FFMPEG_CPU "armv8-a")
elseif (CMAKE_ANDROID_ARCH_ABI STREQUAL "armeabi-v7a")
    set(FFMPEG_ARCH "arm")
    set(FFMPEG_TARGET_TRIPLE "armv7a-linux-androideabi")
    set(FFMPEG_CPU "armv7-a")
elseif (CMAKE_ANDROID_ARCH_ABI STREQUAL "x86_64")
    set(FFMPEG_ARCH "x86_64")
    set(FFMPEG_TARGET_TRIPLE "x86_64-linux-android")
    set(FFMPEG_CPU "x86_64")
elseif (CMAKE_ANDROID_ARCH_ABI STREQUAL "x86")
    set(FFMPEG_ARCH "i686")
    set(FFMPEG_TARGET_TRIPLE "i686-linux-android")
    set(FFMPEG_CPU "i686")
else()
    message(FATAL_ERROR "Unsupported ABI: ${CMAKE_ANDROID_ARCH_ABI}")
endif()

# 设置NDK工具链路径
set(NDK_TOOLCHAIN_BIN "${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64/bin")

# FFmpeg编译参数
set(FFMPEG_CONFIGURE_ARGS
    --target-os=android
    --arch=${FFMPEG_ARCH}
    --cpu=${FFMPEG_CPU}
    --enable-cross-compile
    --cross-prefix=${NDK_TOOLCHAIN_BIN}/${FFMPEG_TARGET_TRIPLE}${CMAKE_ANDROID_API}-
    --sysroot=${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64/sysroot
    --prefix=${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
    --enable-shared
    --disable-static
    --disable-programs
    --disable-doc
    --disable-avdevice
    --disable-postproc
    --disable-network
    --enable-gpl
    --enable-openssl  # 启用OpenSSL支持（需提前编译）
    --extra-cflags="-I${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/include"
    --extra-ldflags="-L${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib"
)

ExternalProject_Add(
    ffmpeg
    GIT_REPOSITORY https://git.ffmpeg.org/ffmpeg.git
    GIT_TAG n6.1.1  # 指定FFmpeg版本
    CONFIGURE_COMMAND <SOURCE_DIR>/configure ${FFMPEG_CONFIGURE_ARGS}
    BUILD_COMMAND make -j${NPROC}
    INSTALL_COMMAND make install
    BUILD_IN_SOURCE 1
    DEPENDS openssl  # 依赖OpenSSL
)
