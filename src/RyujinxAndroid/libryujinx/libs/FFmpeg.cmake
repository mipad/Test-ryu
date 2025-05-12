include(ExternalProject)

find_package(Perl 5 REQUIRED)

set(PROJECT_ENV "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}")

if (CMAKE_HOST_WIN32)
    set(ProgramFiles_x86 "$ENV{ProgramFiles\(x86\)}")
    # https://github.com/microsoft/vswhere/wiki/Find-MSBuild
    cmake_path(APPEND VSWHERE_BIN "${ProgramFiles_x86}" "Microsoft Visual Studio" "Installer" "vswhere.exe")
    # FIXME: Hardcoded architecture, no way to specify the MSVC version
    execute_process(
            COMMAND ${VSWHERE_BIN} "-latest" "-find" "VC\\Tools\\MSVC\\*\\bin\\Hostx64\\x64\\nmake.exe"
            OUTPUT_VARIABLE NMAKE_PATHS_OUTPUT
            OUTPUT_STRIP_TRAILING_WHITESPACE
            COMMAND_ERROR_IS_FATAL ANY
    )
    string(REPLACE "\n" ";" NMAKE_PATH_LIST "${NMAKE_PATHS_OUTPUT}")
    list(GET NMAKE_PATH_LIST 0 NMAKE_PATH)
    cmake_path(NATIVE_PATH NMAKE_PATH NORMALIZE MAKE_COMMAND)

    set(PROJECT_CFG_PREFIX ${PERL_EXECUTABLE})
    # Deal with semicolon-separated lists
    set(PROJECT_PATH_LIST $ENV{Path})
    cmake_path(CONVERT "${ANDROID_TOOLCHAIN_ROOT}\\bin" TO_NATIVE_PATH_LIST ANDROID_TOOLCHAIN_BIN NORMALIZE)
    list(PREPEND PROJECT_PATH_LIST "${ANDROID_TOOLCHAIN_BIN}")
    # Replace semicolons with "|"
    list(JOIN PROJECT_PATH_LIST "|" PROJECT_PATH_STRING)
    # Add the modified PATH string to PROJECT_ENV
    list(APPEND PROJECT_ENV "Path=${PROJECT_PATH_STRING}")
elseif (CMAKE_HOST_UNIX)
    find_program(MAKE_COMMAND NAMES make REQUIRED)
    list(APPEND PROJECT_ENV "PATH=${ANDROID_TOOLCHAIN_ROOT}/bin:$ENV{PATH}")
else ()
    message(WARNING "Host system (${CMAKE_HOST_SYSTEM_NAME}) not supported. Treating as unix.")
    find_program(MAKE_COMMAND NAMES make REQUIRED)
    list(APPEND PROJECT_ENV "PATH=${ANDROID_TOOLCHAIN_ROOT}/bin:$ENV{PATH}")
endif ()

# ------------------ 架构映射 ------------------
# 精确匹配Android ABI
if(CMAKE_ANDROID_ARCH_ABI STREQUAL "arm64-v8a")
    set(FFMPEG_ARCH "aarch64")
    set(TARGET_TRIPLE "aarch64-linux-android")
    set(FFMPEG_CPU "armv8-a")
    set(FFMPEG_EXTRA "--enable-neon --enable-asm")
elseif(CMAKE_ANDROID_ARCH_ABI STREQUAL "armeabi-v7a")
    set(FFMPEG_ARCH "arm")
    set(TARGET_TRIPLE "armv7a-linux-androideabi")
    set(FFMPEG_CPU "armv7-a")
    set(FFMPEG_EXTRA "--enable-neon --enable-thumb")
elseif(CMAKE_ANDROID_ARCH_ABI STREQUAL "x86_64")
    set(FFMPEG_ARCH "x86_64")
    set(TARGET_TRIPLE "x86_64-linux-android")
    set(FFMPEG_CPU "x86_64")
elseif(CMAKE_ANDROID_ARCH_ABI STREQUAL "x86")
    set(FFMPEG_ARCH "i686")
    set(TARGET_TRIPLE "i686-linux-android")
    set(FFMPEG_CPU "i686")
else()
    message(FATAL_ERROR "不支持的ABI: ${CMAKE_ANDROID_ARCH_ABI}")
endif()

# ------------------ OpenSSL路径 ------------------
set(OPENSSL_INSTALL_DIR "${CMAKE_LIBRARY_OUTPUT_DIRECTORY}")

# ------------------ FFmpeg编译参数 ------------------
# 关键修复：移除所有多余引号，避免参数解析错误
set(FFMPEG_CONFIGURE_ARGS
    --target-os=android
    --arch=${FFMPEG_ARCH}
    --cpu=${FFMPEG_CPU}
    --enable-cross-compile
    --cross-prefix=${CROSS_PREFIX}
    --sysroot=${NDK_TOOLCHAIN_ROOT}/sysroot
    --prefix=${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
    --enable-shared
    --disable-static
    --disable-programs
    --disable-doc
    --disable-avdevice
    --disable-postproc
    --disable-network
    --enable-gpl
    --enable-openssl
    --extra-cflags=-I${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/include\;-fPIC\;-O3  # 分号分隔
    --extra-ldflags=-L${OPENSSL_INSTALL_DIR}/lib              # 无引号
    ${FFMPEG_EXTRA}
)

# ------------------ 环境变量配置 ------------------
# 确保PATH包含NDK工具链
set(PROJECT_ENV "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}")


# ------------------ ExternalProject定义 ------------------
ExternalProject_Add(
    ffmpeg
    GIT_REPOSITORY  https://git.ffmpeg.org/ffmpeg.git
    GIT_TAG         n7.1.1  # 指定稳定版本
    
    # 配置阶段（传递环境变量）
    CONFIGURE_COMMAND ${CMAKE_COMMAND} -E env ${ENV_VARS}
        <SOURCE_DIR>/configure ${FFMPEG_CONFIGURE_ARGS}
    
    # 构建阶段
    BUILD_COMMAND    ${CMAKE_COMMAND} -E env ${ENV_VARS}
        make -j${NPROC}
    
    # 安装阶段
    INSTALL_COMMAND  make install
    
    # 日志配置
    LOG_CONFIGURE 1
    LOG_BUILD     1
    LOG_INSTALL   1
    
    # 依赖项
    DEPENDS openssl
    
    # 源码内构建
    BUILD_IN_SOURCE 1
)

# ------------------ 后期处理 ------------------
# 添加库路径
link_directories(${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib)

# 验证输出
add_custom_command(TARGET ffmpeg POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E echo "FFmpeg动态库已生成："
    COMMAND ls -l ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib/libavcodec.so
    COMMENT "验证FFmpeg输出文件"
)
