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

# 设置 FFmpeg 配置选项 - 增强解码器支持并添加硬件解码
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
    --enable-avfilter
    --enable-avcodec
    --enable-avformat
    --enable-avutil
    --enable-swresample
    --enable-swscale
    --disable-network
    --disable-protocols
    --disable-filters
    --enable-asm
    --enable-neon
    --enable-inline-asm
    --enable-jni
    --enable-mediacodec
    
    # 硬件解码器支持 - 优先使用
    --enable-decoder=h264_mediacodec
    --enable-decoder=hevc_mediacodec
    --enable-decoder=vp8_mediacodec
    --enable-decoder=vp9_mediacodec
    --enable-decoder=av1_mediacodec
    --enable-decoder=mpeg4_mediacodec
    --enable-decoder=mpeg2video_mediacodec
    
    # 软件解码器支持 - 作为回退
    --enable-decoder=h264
    --enable-decoder=hevc
    --enable-decoder=vp8
    --enable-decoder=vp9
    --enable-decoder=av1
    --enable-decoder=mpeg4
    --enable-decoder=mpeg2video
    --enable-decoder=mpeg1video
    --enable-decoder=msmpeg4v1
    --enable-decoder=msmpeg4v2
    --enable-decoder=msmpeg4v3
    --enable-decoder=wmv1
    --enable-decoder=wmv2
    --enable-decoder=wmv3
    --enable-decoder=vc1
    --enable-decoder=theora
    --enable-decoder=rv10
    --enable-decoder=rv20
    --enable-decoder=rv30
    --enable-decoder=rv40
    --enable-decoder=flv
    --enable-decoder=svq1
    --enable-decoder=svq3
    --enable-decoder=cinepak
    --enable-decoder=indeo2
    --enable-decoder=indeo3
    --enable-decoder=indeo4
    --enable-decoder=indeo5
    
    # 音频解码器支持
    --enable-decoder=aac
    --enable-decoder=mp3
    --enable-decoder=ac3
    --enable-decoder=eac3
    --enable-decoder=dts
    --enable-decoder=flac
    --enable-decoder=vorbis
    --enable-decoder=opus
    --enable-decoder=wmav1
    --enable-decoder=wmav2
    --enable-decoder=wmapro
    --enable-decoder=pcm_s16le
    --enable-decoder=pcm_s16be
    --enable-decoder=pcm_s24le
    --enable-decoder=pcm_s24be
    --enable-decoder=pcm_s32le
    --enable-decoder=pcm_s32be
    --enable-decoder=pcm_f32le
    --enable-decoder=pcm_f32be
    --enable-decoder=pcm_u8
    --enable-decoder=pcm_alaw
    --enable-decoder=pcm_mulaw
    --enable-decoder=adpcm_ms
    --enable-decoder=adpcm_ima_wav
    --enable-decoder=adpcm_ima_qt
    --enable-decoder=adpcm_swf
    --enable-decoder=adpcm_yamaha
    
    # 解复用器支持
    --enable-demuxer=h264
    --enable-demuxer=hevc
    --enable-demuxer=aac
    --enable-demuxer=mp3
    --enable-demuxer=ac3
    --enable-demuxer=eac3
    --enable-demuxer=flac
    --enable-demuxer=ogg
    --enable-demuxer=mov
    --enable-demuxer=matroska
    --enable-demuxer=avi
    --enable-demuxer=flv
    --enable-demuxer=mpegts
    --enable-demuxer=m4v
    --enable-demuxer=mpegvideo
    --enable-demuxer=mpegps
    --enable-demuxer=wav
    --enable-demuxer=aiff
    --enable-demuxer=ape
    --enable-demuxer=mpc
    --enable-demuxer=mpc8
    --enable-demuxer=asf
    --enable-demuxer=wv
    --enable-demuxer=tta
    --enable-demuxer=rm
    --enable-demuxer=voc
    --enable-demuxer=film_cpk
    --enable-demuxer=nsv
    --enable-demuxer=flic
    --enable-demuxer=4xm
    --enable-demuxer=thp
    
    # 解析器支持
    --enable-parser=h264
    --enable-parser=hevc
    --enable-parser=aac
    --enable-parser=ac3
    --enable-parser=mpegaudio
    --enable-parser=mpeg4video
    --enable-parser=mpegvideo
    --enable-parser=vp8
    --enable-parser=vp9
    --enable-parser=flac
    --enable-parser=opus
    --enable-parser=vorbis
    --enable-parser=dca
    --enable-parser=vc1
    
    # 比特流过滤器支持
    --enable-bsf=h264_mp4toannexb
    --enable-bsf=hevc_mp4toannexb
    --enable-bsf=aac_adtstoasc
    --enable-bsf=extract_extradata
    --enable-bsf=noise
    --enable-bsf=remove_extradata
    --enable-bsf=dump_extradata
    --enable-bsf=vp9_superframe
    --enable-bsf=vp9_superframe_split
    
    # 硬件加速相关
    --enable-hwaccels
    --enable-opengl
    --enable-gpl
    --enable-nonfree
    
    --disable-zlib
    --enable-small
    --enable-optimizations
    --disable-debug
    --disable-stripping
    # 添加错误恢复支持
    --enable-error-resilience
    --enable-hardcoded-tables
    --enable-safe-bitstream-reader
    # 移除 pkg-config 依赖
    --pkg-config=false
)

# 添加配置验证步骤
ExternalProject_Add(
    ffmpeg
    # 使用稳定版本而不是 master
    GIT_REPOSITORY              https://github.com/FFmpeg/FFmpeg.git
    GIT_TAG                     n5.1.3  # 使用稳定版本
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
