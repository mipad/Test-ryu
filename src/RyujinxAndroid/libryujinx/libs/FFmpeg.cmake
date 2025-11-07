include(ExternalProject)

set(PROJECT_ENV "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}")

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

# 设置 FFmpeg 配置选项 - 针对天玑8100优化，修复汇编问题
set(FFMPEG_CONFIGURE_FLAGS
    --target-os=android
    --arch=aarch64
    --enable-cross-compile
    --cross-prefix=${ANDROID_TOOLCHAIN_PREFIX}
    --sysroot=${ANDROID_SYSROOT}
    --cc=${ANDROID_TOOLCHAIN_ROOT}/bin/clang
    --cxx=${ANDROID_TOOLCHAIN_ROOT}/bin/clang++
    --ar=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-ar
    --as=${ANDROID_TOOLCHAIN_ROOT}/bin/clang
    --nm=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-nm
    --strip=${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-strip
    --enable-shared
    --disable-static
    --disable-programs
    --disable-doc
    --disable-avdevice
    --disable-postproc
    --disable-network
    --disable-everything
    --enable-decoder=aac,mp3,ac3,flac,opus,vorbis,h264,hevc,vp8,vp9
    --enable-demuxer=aac,mp3,ac3,flac,ogg,opus,matroska,mov,avi
    --enable-parser=aac,mp3,ac3,flac,opus,vorbis,h264,hevc,vp8,vp9
    --enable-swresample
    --enable-swscale
    --enable-avcodec
    --enable-avformat
    --enable-avutil
    --enable-small
    --enable-neon
    --enable-asm
    --disable-inline-asm  # 禁用内联汇编以避免编译错误
    --extra-cflags="-O3 -fPIC -march=armv8.2-a+fp16+dotprod -mcpu=cortex-a78 -mtune=cortex-a78 -DANDROID -D__ANDROID__ -I${ANDROID_SYSROOT}/usr/include"
    --extra-ldflags="-L${ANDROID_SYSROOT}/usr/lib -Wl,--hash-style=both"
    --prefix=${CMAKE_CURRENT_BINARY_DIR}/ffmpeg-install
    --pkg-config=pkg-config
    --disable-symver
    --disable-stripping
)

# 根据 Android API 级别设置
if(CMAKE_SYSTEM_VERSION)
    list(APPEND FFMPEG_CONFIGURE_FLAGS --enable-jni)
    list(APPEND FFMPEG_CONFIGURE_FLAGS --enable-mediacodec)
endif()

ExternalProject_Add(
    ffmpeg
    GIT_REPOSITORY              https://github.com/FFmpeg/FFmpeg.git
    GIT_TAG                     master  # 使用 master 分支
    GIT_PROGRESS                1
    GIT_SHALLOW                 1
    UPDATE_COMMAND              ""  # 禁用更新步骤
    LIST_SEPARATOR              "|"
    CONFIGURE_COMMAND           ${CMAKE_COMMAND} -E env ${PROJECT_ENV}
                                <SOURCE_DIR>/configure
                                ${FFMPEG_CONFIGURE_FLAGS}
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
    STEP_TARGETS                build
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