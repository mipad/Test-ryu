# FFmpeg.cmake - Android交叉编译专用
include(ExternalProject)
include(ProcessorCount)

# --------------- 基础配置 ---------------
# 获取逻辑CPU核心数加速编译
ProcessorCount(NPROC)
if(NOT NPROC)
    set(NPROC 4)  # 默认使用4线程
    message(STATUS "Using default parallel jobs: ${NPROC}")
endif()

# --------------- 工具链配置 ---------------
# 定义NDK根路径（需通过顶层CMake传递）
if(NOT DEFINED CMAKE_ANDROID_NDK)
    message(FATAL_ERROR "ANDROID_NDK路径未定义！请通过-DCMAKE_ANDROID_NDK=设置")
endif()

# 根据宿主系统设置工具链路径
if(CMAKE_HOST_WIN32)
    set(NDK_HOST_TAG "windows-x86_64")
elseif(CMAKE_HOST_UNIX)
    set(NDK_HOST_TAG "linux-x86_64")
else()
    message(FATAL_ERROR "不支持的操作系统: ${CMAKE_HOST_SYSTEM_NAME}")
endif()

set(NDK_TOOLCHAIN_ROOT "${CMAKE_ANDROID_NDK}/toolchains/llvm/prebuilt/${NDK_HOST_TAG}")
set(NDK_BIN_DIR "${NDK_TOOLCHAIN_ROOT}/bin")

# --------------- 架构映射 ---------------
# 将CMake的ABI转换为FFmpeg参数
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

# --------------- 环境变量配置 ---------------
# 设置关键环境变量
set(ENV_VARS
    "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}"
    "PATH=${NDK_BIN_DIR}:$ENV{PATH}"  # 确保工具链在PATH中
)

# --------------- FFmpeg编译参数 ---------------
# 动态生成交叉编译器前缀（必须包含API版本）
set(CROSS_PREFIX "${NDK_BIN_DIR}/${TARGET_TRIPLE}${CMAKE_ANDROID_API}-")

# OpenSSL路径（假设已通过OpenSSL.cmake编译）
set(OPENSSL_INSTALL_DIR "${CMAKE_LIBRARY_OUTPUT_DIRECTORY}")

# 配置参数
set(FFMPEG_CONFIGURE_ARGS
    --target-os=android       # 目标平台
    --arch=${FFMPEG_ARCH}     # 架构
    --cpu=${FFMPEG_CPU}       # CPU指令集
    --enable-cross-compile    # 启用交叉编译
    --cross-prefix=${CROSS_PREFIX}              # 交叉编译器前缀
    --sysroot=${NDK_TOOLCHAIN_ROOT}/sysroot     # 系统根目录
    --prefix=${CMAKE_LIBRARY_OUTPUT_DIRECTORY}  # 安装路径
    --enable-shared           # 生成动态库
    --disable-static          # 禁用静态库
    --disable-programs        # 不生成可执行文件
    --disable-doc             # 禁用文档
    --disable-avdevice        # 禁用avdevice模块
    --disable-postproc        # 禁用后期处理
    --disable-network         # 禁用网络功能
    --enable-gpl              # 启用GPL许可
    --enable-openssl          # 启用OpenSSL支持
    --extra-cflags="-I${OPENSSL_INSTALL_DIR}/include -fPIC -O3"  # 优化和头文件路径
    --extra-ldflags="-L${OPENSSL_INSTALL_DIR}/lib"               # 库文件路径
    ${FFMPEG_EXTRA}           # 架构特定参数
)

# --------------- ExternalProject配置 ---------------
ExternalProject_Add(
    ffmpeg
    GIT_REPOSITORY  https://git.ffmpeg.org/ffmpeg.git
    GIT_TAG         n6.1.1  # 指定稳定版本
    
    # 配置阶段
    CONFIGURE_COMMAND ${CMAKE_COMMAND} -E env ${ENV_VARS}
        <SOURCE_DIR>/configure ${FFMPEG_CONFIGURE_ARGS}
    
    # 构建阶段
    BUILD_COMMAND    ${CMAKE_COMMAND} -E env ${ENV_VARS}
        make -j${NPROC}
    
    # 安装阶段
    INSTALL_COMMAND  make install
    
    # 日志输出
    LOG_CONFIGURE 1
    LOG_BUILD 1
    LOG_INSTALL 1
    
    # 依赖项
    DEPENDS openssl  # 确保OpenSSL先编译
    
    # 源码内构建
    BUILD_IN_SOURCE 1
)

# --------------- 后续处理 ---------------
# 添加FFmpeg库路径到主项目
link_directories(${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib)

# 验证安装
add_custom_command(TARGET ffmpeg POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E echo "FFmpeg库已安装到：${CMAKE_LIBRARY_OUTPUT_DIRECTORY}"
    COMMAND ls -l ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/lib/libavcodec.so
)
