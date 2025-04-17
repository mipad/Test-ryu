include(ExternalProject)
find_package(Perl 5 REQUIRED)

# 确保 NDK 路径正确传递
set(ENV{ANDROID_NDK_ROOT} ${CMAKE_ANDROID_NDK})
set(PROJECT_ENV
    "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}"
    "PATH=${ANDROID_TOOLCHAIN_ROOT}/bin:$ENV{PATH}"
)

# 架构名称转换
if(CMAKE_ANDROID_ARCH MATCHES "arm64.*")
    set(OPENSSL_ARCH "android-arm64")
elseif(CMAKE_ANDROID_ARCH MATCHES "x86_64")
    set(OPENSSL_ARCH "android-x86_64")
else()
    message(FATAL_ERROR "Unsupported architecture: ${CMAKE_ANDROID_ARCH}")
endif()

# Windows 特定配置
if(CMAKE_HOST_WIN32)
    find_program(NMAKE_EXE nmake REQUIRED)
    set(MAKE_COMMAND ${NMAKE_EXE})
    # 确保 Perl 路径无空格
    string(REPLACE " " "^ " PERL_EXECUTABLE_FIXED ${PERL_EXECUTABLE})
    set(PROJECT_CFG_PREFIX ${PERL_EXECUTABLE_FIXED})
    # 使用分号作为路径分隔符
    list(APPEND PROJECT_ENV "Path=${ANDROID_TOOLCHAIN_ROOT}/bin;$ENV{Path}")
else()
    find_program(MAKE_COMMAND NAMES make REQUIRED)
    set(PROJECT_CFG_PREFIX ${PERL_EXECUTABLE})
endif()

ExternalProject_Add(
    openssl
    GIT_REPOSITORY https://github.com/openssl/openssl.git
    GIT_TAG openssl-3.2.1  # 使用标签代替固定 commit
    CONFIGURE_COMMAND
        ${CMAKE_COMMAND} -E env ${PROJECT_ENV}
        ${PROJECT_CFG_PREFIX} <SOURCE_DIR>/Configure
            ${OPENSSL_ARCH}
            -D__ANDROID_API__=${CMAKE_SYSTEM_VERSION}
            --prefix=${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
            --openssldir=${CMAKE_LIBRARY_OUTPUT_DIRECTORY}/ssl
            no-shared  # 强制生成静态库
    BUILD_COMMAND
        ${CMAKE_COMMAND} -E env ${PROJECT_ENV}
        ${MAKE_COMMAND} -j8
    INSTALL_COMMAND
        ${MAKE_COMMAND} install_dev  # 安装头文件和静态库
)
