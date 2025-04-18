include(FetchContent)

cmake_minimum_required(VERSION 3.22.1)
project("ryujinxjni")

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# 定义 OpenSSL 安装路径
set(OPENSSL_INSTALL_DIR "${CMAKE_BINARY_DIR}/openssl_install")
file(MAKE_DIRECTORY ${OPENSSL_INSTALL_DIR})

FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://github.com/bylaws/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
)

FetchContent_MakeAvailable(adrenotools)

add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
)

find_library(log-lib log)

# 定义 JNI 库路径为 OpenSSL 安装路径
set(JNI_PATH ${OPENSSL_INSTALL_DIR}/lib)
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif()

target_link_libraries(
    ryujinxjni
    ${log-lib}
    -lvulkan
    -landroid
    adrenotools
    ${LIBCRYPTO_JNI_PATH}
    ${LIBSSL_JNI_PATH}
)
