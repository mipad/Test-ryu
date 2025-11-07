include(ExternalProject)

set(PROJECT_ENV "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}")

# 设置 Android 工具链路径
set(ANDROID_TOOLCHAIN_ROOT ${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64)
set(ANDROID_SYSROOT ${ANDROID_TOOLCHAIN_ROOT}/sysroot)
set(ANDROID_PLATFORM aarch64-linux-android21)

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

# 设置 FFmpeg 配置选项 - 修复参数传递问题
set(FFMPEG_CONFIGURE_COMMAND
    <SOURCE_DIR>/configure
    --prefix=${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install
    --cross-prefix=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}-
    --target-os=android  # 改回 android
    --arch=aarch64
    --cpu=armv8-a  # 使用通用 ARMv8 而不是特定 CPU
    --cc=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}-clang
    --cxx=${ANDROID_TOOLCHAIN_ROOT}/bin/${ANDROID_PLATFORM}-clang++
    --nm=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-nm
    --strip=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-strip
    --enable-cross-compile
    --sysroot=${ANDROID_SYSROOT}
    --extra-cflags=-O3
    --extra-cflags=-fPIC
    --extra-cflags=-march=armv8.2-a+fp16+dotprod
    --extra-cflags=-mtune=cortex-a78
    --extra-cflags=-DANDROID
    --extra-cflags=-D__ANDROID__
    --extra-ldflags=-Wl,--hash-style=both
    --extra-ldexeflags=-pie
    --enable-runtime-cpudetect
    --disable-static
    --enable-shared
    --disable-ffprobe
    --disable-ffplay
    --disable-ffmpeg
    --disable-debug
    --disable-doc
    --enable-avfilter
    --enable-decoders
    --disable-programs
    --disable-avdevice
    --disable-network
    --disable-everything
    --enable-decoder=aac
    --enable-decoder=mp3
    --enable-decoder=ac3
    --enable-decoder=flac
    --enable-decoder=opus
    --enable-decoder=vorbis
    --enable-decoder=h264
    --enable-decoder=hevc
    --enable-decoder=vp8
    --enable-decoder=vp9
    --enable-demuxer=aac
    --enable-demuxer=mp3
    --enable-demuxer=ac3
    --enable-demuxer=flac
    --enable-demuxer=ogg
    --enable-demuxer=opus
    --enable-demuxer=matroska
    --enable-demuxer=mov
    --enable-demuxer=avi
    --enable-parser=aac
    --enable-parser=mp3
    --enable-parser=ac3
    --enable-parser=flac
    --enable-parser=opus
    --enable-parser=vorbis
    --enable-parser=h264
    --enable-parser=hevc
    --enable-parser=vp8
    --enable-parser=vp9
    --enable-swresample
    --enable-swscale
    --enable-avcodec
    --enable-avformat
    --enable-avutil
    --enable-small
    --enable-neon
    --enable-asm
    --disable-inline-asm
    --pkg-config=pkg-config
    --disable-symver
    --disable-jni  # 暂时禁用 JNI
    --disable-mediacodec  # 暂时禁用 MediaCodec
)

ExternalProject_Add(
    ffmpeg
    GIT_REPOSITORY              https://github.com/FFmpeg/FFmpeg.git
    GIT_TAG                     master
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

# 添加头文件目录
set(FFMPEG_INCLUDE_DIR ${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install/include)