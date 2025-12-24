# FFmpegStatic.cmake
include(ExternalProject)

set(PROJECT_ENV "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}")

# 设置 Android 工具链路径
set(ANDROID_TOOLCHAIN_ROOT ${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64)
set(ANDROID_SYSROOT ${ANDROID_TOOLCHAIN_ROOT}/sysroot)
set(ANDROID_PLATFORM aarch64-linux-android)

# 使用 Android API 级别 30
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

# 设置 FFmpeg 配置选项 - 简化的静态配置
set(FFMPEG_CONFIGURE_COMMAND
    <SOURCE_DIR>/configure
    --prefix=${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install
    --cross-prefix=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}-
    --target-os=android
    --arch=aarch64
    --cc=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}${ANDROID_API_LEVEL}-clang
    --cxx=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}${ANDROID_API_LEVEL}-clang++
    --nm=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-nm
    --ar=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-ar  # 改为 llvm-ar
    --strip=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-strip
    --enable-cross-compile
    --sysroot=${ANDROID_SYSROOT}
    
    # 编译器优化
    --extra-cflags=-O3
    --extra-cflags=-fPIC
    --extra-cflags=-march=armv8.2-a+fp16+dotprod
    --extra-cflags=-mtune=cortex-a78
    --extra-cflags=-DANDROID
    # 注意：移除了 -D__ANDROID_API__=${ANDROID_API_LEVEL}，避免与工具链冲突
    --extra-cflags=-Wno-unused-function
    --extra-cflags=-Wno-unused-variable
    --extra-cflags=-Wno-unused-but-set-variable
    --extra-cflags=-Wno-macro-redefined
    --extra-cflags=-Wno-incompatible-pointer-types-discards-qualifiers
    --extra-cflags=-Wno-implicit-const-int-float-conversion
    --extra-ldflags=-Wl,--hash-style=both
    --extra-ldexeflags=-pie
    
    # 静态库配置
    --enable-static
    --disable-shared
    
    # 基础配置
    --disable-programs
    --disable-doc
    
    # 核心库（根据 C# 需要）
    --enable-avcodec
    --enable-avformat
    --enable-avutil
    --enable-swresample
    --enable-swscale
    
    # 禁用不需要的库以减少体积和警告
    --disable-avdevice
    --disable-postproc
    --disable-avfilter
    
    # ARM 优化
    --enable-asm
    --enable-neon
    --enable-inline-asm
    
    # Android 特有
    --enable-jni
    --enable-mediacodec
    --enable-hwaccels
    
    # 解码器配置
    --enable-decoder=*
    --disable-encoder=*
    
    # 硬件加速
    --enable-hwaccel=h264_mediacodec
    --enable-hwaccel=hevc_mediacodec
    --enable-hwaccel=vp8_mediacodec
    --enable-hwaccel=vp9_mediacodec
    
    --enable-demuxer=*
    --enable-muxer=*
    --enable-parser=*
    --enable-bsf=*
    
    # 压缩库
    --enable-zlib
    
    # 性能优化
    --disable-small
    --enable-optimizations
    
    # 调试信息
    --disable-debug
    --disable-stripping
    
    --pkg-config=pkg-config
)

# 添加配置验证步骤
ExternalProject_Add(
    ffmpeg-static
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
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavformat.a
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswresample.a
        ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswscale.a
)

# 创建静态库导入目标
add_library(avcodec-static STATIC IMPORTED GLOBAL)
set_target_properties(avcodec-static PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavcodec.a
)
add_dependencies(avcodec-static ffmpeg-static)

add_library(avutil-static STATIC IMPORTED GLOBAL)
set_target_properties(avutil-static PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavutil.a
)
add_dependencies(avutil-static ffmpeg-static)

add_library(avformat-static STATIC IMPORTED GLOBAL)
set_target_properties(avformat-static PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libavformat.a
)
add_dependencies(avformat-static ffmpeg-static)

add_library(swresample-static STATIC IMPORTED GLOBAL)
set_target_properties(swresample-static PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswresample.a
)
add_dependencies(swresample-static ffmpeg-static)

add_library(swscale-static STATIC IMPORTED GLOBAL)
set_target_properties(swscale-static PROPERTIES
    IMPORTED_LOCATION ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/lib/libswscale.a
)
add_dependencies(swscale-static ffmpeg-static)

# 添加头文件目录
set(FFMPEG_INCLUDE_DIR ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/include)
    
