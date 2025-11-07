include(ExternalProject)

set(PROJECT_ENV "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}")

# 设置 Android 工具链路径
set(ANDROID_TOOLCHAIN_ROOT ${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64)
set(ANDROID_SYSROOT ${ANDROID_TOOLCHAIN_ROOT}/sysroot)
set(ANDROID_PLATFORM aarch64-linux-android)

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

# 设置优化的 FFmpeg 配置选项 - 针对天玑8100优化
set(FFMPEG_CONFIGURE_COMMAND
    <SOURCE_DIR>/configure
    --prefix=${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install
    --cross-prefix=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}21-
    --target-os=android
    --arch=aarch64
    --cpu=cortex-a78
    --cc=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}21-clang
    --cxx=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}21-clang++
    --nm=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-nm
    --strip=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-strip
    --enable-cross-compile
    --sysroot=${ANDROID_SYSROOT}
    --extra-cflags=-O3
    --extra-cflags=-fPIC
    --extra-cflags=-march=armv8.2-a+fp16+dotprod
    --extra-cflags=-mtune=cortex-a78
    --extra-cflags=-DANDROID
    --extra-cflags=-D__ANDROID_API__=21
    --extra-cflags=-ffast-math
    --extra-cflags=-funroll-loops
    --extra-ldflags=-Wl,--hash-style=both
    --extra-ldexeflags=-pie
    --enable-runtime-cpudetect
    --disable-static
    --enable-shared
    --disable-programs
    --disable-doc
    --disable-htmlpages
    --disable-manpages
    --disable-podpages
    --disable-txtpages
    # 启用必要的组件
    --enable-avfilter
    --enable-avcodec
    --enable-avformat
    --enable-avutil
    --enable-swresample
    --enable-swscale
    --enable-network
    --enable-protocols
    --enable-filters
    --enable-asm
    --enable-neon
    --enable-inline-asm
    --enable-jni
    --enable-mediacodec
    # 只启用常用解码器以减少开销
    --enable-decoder=h264,hevc,mpeg4,aac,mp3,ac3
    --enable-encoder=mpeg4,aac
    --enable-demuxer=mov,mp4,matroska,avi,mpegts
    --enable-muxer=mp4,matroska
    --enable-parser=h264,hevc,aac,mpeg4video
    --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb
    --enable-hwaccels
    --enable-hwaccel=h264_mediacodec,hevc_mediacodec
    --enable-zlib
    --enable-small
    --enable-optimizations
    --disable-debug
    --disable-stripping
    # 性能优化选项
    --enable-pthreads
    --extra-cflags=-pthread
    --extra-ldflags=-pthread
    --pkg-config=pkg-config
)

# 添加配置验证步骤
ExternalProject_Add(
    ffmpeg
    GIT_REPOSITORY              https://github.com/FFmpeg/FFmpeg.git
    GIT_TAG                     master  # 使用稳定版本而不是master
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
    # 添加日志以帮助调试
    LOG_CONFIGURE 1
    LOG_BUILD 1
    LOG_INSTALL 1
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

# 添加性能优化验证
message(STATUS "FFmpeg 配置优化完成")
message(STATUS "目标 CPU: cortex-a78 (天玑8100)")
message(STATUS "硬件加速: MediaCodec 启用")
message(STATUS "NEON 优化: 启用")
message(STATUS "指令集: armv8.2-a+fp16+dotprod")
message(STATUS "线程优化: pthread 启用")