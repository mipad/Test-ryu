include(ExternalProject)
find_package(Perl 5 REQUIRED)

set(PROJECT_ENV "ANDROID_NDK_ROOT=${CMAKE_ANDROID_NDK}")

# 定义 OpenSSL 安装路径（与 CMakeLists.txt 一致）
set(OPENSSL_INSTALL_DIR "${CMAKE_BINARY_DIR}/openssl_install")

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

    set(PROJECT_CFG_PREFIX ${PERL_EXECUTABLE})
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

ExternalProject_Add(
    openssl
    GIT_REPOSITORY              https://github.com/openssl/openssl.git
    GIT_TAG                     a7e992847de83aa36be0c399c89db3fb827b0be2
    LIST_SEPARATOR              "|"
    CONFIGURE_COMMAND           ${CMAKE_COMMAND} -E env ${PROJECT_ENV}
                                ${PROJECT_CFG_PREFIX} <SOURCE_DIR>/Configure
                                android-${CMAKE_ANDROID_ARCH}
                                -D__ANDROID_API_=${CMAKE_SYSTEM_VERSION}
                                --prefix=${OPENSSL_INSTALL_DIR}  # 关键修改
                                --libdir=""
    BUILD_COMMAND               ${CMAKE_COMMAND} -E env ${PROJECT_ENV}
                                ${MAKE_COMMAND}
    INSTALL_COMMAND             ${MAKE_COMMAND} install_sw  # 使用 install_sw 目标
)
