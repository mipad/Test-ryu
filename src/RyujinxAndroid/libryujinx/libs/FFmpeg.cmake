include(ExternalProject)

set(PROJECT_ENV "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}")

# 设置 Android 工具链路径
set(ANDROID_TOOLCHAIN_ROOT ${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64)
set(ANDROID_SYSROOT ${ANDROID_TOOLCHAIN_ROOT}/sysroot)
set(ANDROID_PLATFORM aarch64-linux-android)

# 提高 Android API 级别到 30（Android 11），获得更好的硬件支持
set(ANDROID_API_LEVEL 30)

if (CMAKE_HOST_WIN32)
    set(ProgramFiles_x86 "$ENV{ProgramFiles\(x86\)}")
    cmake_path(APPEND VSWHERE_BIN "${ProgramFiles_x86}" "Microsoft Visual Studio" "Installer" "vswhere.exe")
    execute_process(
            COMMAND ${VSWHERE_BIN} "-latest" "-find" "VC\\Tools\\MSVC\\*\\bin\\Hostx64\\x64\\nmake.exe"
            OUTPUT_VARIABLE NMAKE_PATHS_OUTPUT
            OUTPUT_STRIP_TRAILING_WHITESPACE
            COMMAND_ERROR_IS_FATAL ANY
    )
    string(REPLACE "\n" ";" NMAKE_PATH_LIST "${NMAKE_PATHS_OUTPUT}")
    list(GET NMAKE_PATH_LIST 0 NMAKE_PATH)
    cmake_path(NATIVE_PATH NMAKE_PATH NORMALIZE MAKE_COMMAND)

    set(PROJECT_PATH_LIST $ENV{Path})
    cmake_path(CONVERT "${ANDROID_TOOLCHAIN_ROOT}\\bin" TO_NATIVE_PATH_LIST ANDROID_TOOLCHAIN_BIN NORMALIZE)
    list(PREPEND PROJECT_PATH_LIST "${ANDROID_TOOLCHAIN_BIN}")
    list(JOIN PROJECT_PATH_LIST "|" PROJECT_PATH_STRING)
    list(APPEND PROJECT_ENV "Path=${PROJECT_PATH_STRING}")
elseif (CMAKE_HOST_UNIX)
    find_program(MAKE_COMMAND NAMES make REQUIRED)
    list(APPEND PROJECT_ENV "PATH=${ANDROID_TOOLCHAIN_ROOT}/bin:$ENV{PATH}")
else ()
    message(WARNING "Host system (${CMAKE_HOST_SYSTEM_NAME}) not supported. Treating as unix.")
    find_program(MAKE_COMMAND NAMES make REQUIRED)
    list(APPEND PROJECT_ENV "PATH=${ANDROID_TOOLCHAIN_ROOT}/bin:$ENV{PATH}")
endif ()

# 设置 FFmpeg 配置选项 - 针对 Ryujinx 模拟器优化，启用多线程解码
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
    
    # 高级编译器优化
    --extra-cflags=-O3
    --extra-cflags=-fPIC
    --extra-cflags=-march=armv8.2-a+fp16+dotprod  # 使用兼容性更好的架构
    --extra-cflags=-mtune=cortex-a78
    --extra-cflags=-ftree-vectorize
    --extra-cflags=-funroll-loops
    --extra-cflags=-fomit-frame-pointer
    --extra-cflags=-DANDROID
    --extra-cflags=-D__ANDROID_API__=${ANDROID_API_LEVEL}
    --extra-cflags=-DPTHREAD  # 启用pthread支持
    --extra-ldflags=-Wl,--hash-style=both
    --extra-ldexeflags=-pie
    
    # 多线程支持 - 关键！
    --enable-pthreads
    --enable-w32threads=no  # Android不需要Windows线程
    
    # 运行时优化
    --enable-runtime-cpudetect
    
    # 基础配置
    --disable-static
    --enable-shared
    --disable-programs
    --disable-doc
    --disable-htmlpages
    --disable-manpages
    --disable-podpages
    --disable-txtpages
    
    # 核心库
    --enable-avfilter
    --enable-avcodec
    --enable-avformat
    --enable-avutil
    --enable-swresample
    --enable-swscale
    --enable-network
    --enable-protocols
    --enable-filters
    
    # ARM架构优化
    --enable-asm
    --enable-neon
    --enable-inline-asm
    --enable-armv5te
    --enable-armv6
    --enable-armv6t2
    --enable-armv8
    --enable-vfp
    --enable-vfpv3
    --enable-thumb
    
    # Android特有
    --enable-jni
    --enable-mediacodec
    --enable-hwaccels
    
    # 解码器配置 - 针对模拟器优化
    --enable-decoder=*
    --disable-encoder=*  # 模拟器不需要编码器
    
    # 硬件加速解码
    --enable-hwaccel=h264_mediacodec
    --enable-hwaccel=hevc_mediacodec
    --enable-hwaccel=mpeg2_mediacodec
    --enable-hwaccel=vp8_mediacodec
    --enable-hwaccel=vp9_mediacodec
    --enable-hwaccel=av1_mediacodec
    
    --enable-demuxer=*
    --enable-muxer=*
    --enable-parser=*
    --enable-bsf=*
    
    # 修改压缩库配置部分
    --enable-zlib
    --disable-bzlib      # 改为禁用
    --disable-lzma       # 改为禁用
    
    # 性能优化
    --disable-small  # 移除大小优化，确保完整性能
    --enable-optimizations
    --enable-fast-unaligned
    
    # 调试信息
    --disable-debug
    --disable-stripping  # 保持符号表，便于调试
    
    --pkg-config=pkg-config
)

# 添加配置验证步骤
ExternalProject_Add(
    ffmpeg
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
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavcodec.so
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavutil.so
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavformat.so
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswresample.so
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswscale.so
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavfilter.so
)

# 创建导入目标
add_library(avcodec SHARED IMPORTED GLOBAL)
set_target_properties(avcodec PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavcodec.so
)
add_dependencies(avcodec ffmpeg)

add_library(avutil SHARED IMPORTED GLOBAL)
set_target_properties(avutil PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavutil.so
)
add_dependencies(avutil ffmpeg)

add_library(avformat SHARED IMPORTED GLOBAL)
set_target_properties(avformat PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavformat.so
)
add_dependencies(avformat ffmpeg)

add_library(swresample SHARED IMPORTED GLOBAL)
set_target_properties(swresample PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswresample.so
)
add_dependencies(swresample ffmpeg)

add_library(swscale SHARED IMPORTED GLOBAL)
set_target_properties(swscale PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswscale.so
)
add_dependencies(swscale ffmpeg)

add_library(avfilter SHARED IMPORTED GLOBAL)
set_target_properties(avfilter PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavfilter.so
)
add_dependencies(avfilter ffmpeg)

# 添加头文件目录
set(FFMPEG_INCLUDE_DIR ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/include)
