# FFmpeg.cmake - 针对NDK 27.2+的完整修复版
include(ExternalProject)
include(ProcessorCount)

# ------------------ 基础配置 ------------------
# 设置NDK路径检查
if(NOT DEFINED CMAKE_ANDROID_NDK)
    message(FATAL_ERROR "必须通过-DCMAKE_ANDROID_NDK=指定NDK路径")
endif()

# 获取逻辑CPU核心数
ProcessorCount(NPROC)
if(NOT NPROC)
    set(NPROC 4)
    message(STATUS "未检测到CPU核心数，默认使用线程数: ${NPROC}")
endif()

# ------------------ NDK工具链路径 ------------------
# 强制指定NDK 27的目录结构
set(NDK_TOOLCHAIN_ROOT "${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/linux-x86_64")
set(NDK_BIN_DIR "${NDK_TOOLCHAIN_ROOT}/bin")

# 验证工具链是否存在
if(NOT EXISTS "${NDK_BIN_DIR}")
    message(FATAL_ERROR "NDK工具链路径不存在: ${NDK_BIN_DIR}")
endif()

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

# ------------------ 动态生成交叉编译器前缀 ------------------
# 重要修复：必须包含API版本号
if(NOT DEFINED CMAKE_ANDROID_API)
    set(CMAKE_ANDROID_API 35) # 默认API级别
endif()
set(CROSS_PREFIX "${NDK_BIN_DIR}/${TARGET_TRIPLE}${CMAKE_ANDROID_API}-")

# 验证交叉编译器是否存在
if(NOT EXISTS "${CROSS_PREFIX}clang")
    message(FATAL_ERROR "交叉编译器不存在: ${CROSS_PREFIX}clang")
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
set(ENV_VARS
    "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}"
    "PATH=${NDK_BIN_DIR}:$ENV{PATH}"
)

# ------------------ ExternalProject定义 ------------------
ExternalProject_Add(
    ffmpeg
    GIT_REPOSITORY  https://git.ffmpeg.org/ffmpeg.git
    GIT_TAG         n6.1.1  # 指定稳定版本
    
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
