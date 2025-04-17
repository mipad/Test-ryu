cmake_minimum_required(VERSION 3.18...3.31)

# ==================== 核心配置 ====================
# 显式指定 NDK 路径并加载工具链
set(ANDROID_NDK $ENV{ANDROID_NDK})
if(NOT ANDROID_NDK)
    message(FATAL_ERROR "ANDROID_NDK 环境变量未设置！")
endif()
include(${ANDROID_NDK}/build/cmake/android.toolchain.cmake)

set(CMAKE_SYSTEM_NAME Android)
set(CMAKE_ANDROID_ARCH_ABI arm64-v8a)
set(CMAKE_ANDROID_NDK ${ANDROID_NDK})  # 显式设置 NDK 路径

# ==================== 全局路径配置 ====================
project("ryujinxjni")
set(PROJECT_ROOT ${CMAKE_SOURCE_DIR})
set(OPENSSL_ROOT ${PROJECT_ROOT}/libs/openssl)
set(OPENSSL_BUILD_DIR ${PROJECT_ROOT}/build/openssl)
set(MODULES_BASE_DIR ${CMAKE_SOURCE_DIR}/../../src)
set(THIRDPARTY_DIR ${PROJECT_ROOT}/../thirdparty)

# ==================== 输出目录统一配置 ====================
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${PROJECT_ROOT}/../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_LIBRARY_OUTPUT_DIRECTORY})
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_LIBRARY_OUTPUT_DIRECTORY})

# ==================== 编译器与链接器配置 ====================
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -fPIC -Wno-deprecated-declarations")

# 设置系统根目录和工具链路径
set(ANDROID_TOOLCHAIN_ROOT ${ANDROID_NDK}/toolchains/llvm/prebuilt/${CMAKE_HOST_SYSTEM_NAME})
set(ANDROID_SYSROOT ${ANDROID_TOOLCHAIN_ROOT}/sysroot)
set(CMAKE_SYSROOT ${ANDROID_SYSROOT})
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} --sysroot=${ANDROID_SYSROOT}")

# 添加关键链接器标志
set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} \
    -Wl,--build-id=sha1 \
    -Wl,--no-undefined \
    -Wl,-z,relro -Wl,-z,now \
    -Wl,--gc-sections \
    -B${ANDROID_TOOLCHAIN_ROOT}/aarch64-linux-android/bin \
    -L${ANDROID_SYSROOT}/usr/lib/aarch64-linux-android/21 \
")

# ==================== OpenSSL 配置 ====================
include(${PROJECT_ROOT}/libs/OpenSSL.cmake)

# ==================== 自定义宏：强制子模块为共享库 ====================
macro(add_shared_subdirectory dir)
    add_subdirectory(${dir})
    get_filename_component(MODULE_NAME ${dir} NAME)
    if(TARGET ${MODULE_NAME})
        set_target_properties(${MODULE_NAME} PROPERTIES
            LIBRARY_OUTPUT_DIRECTORY ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
            POSITION_INDEPENDENT_CODE ON
            CXX_VISIBILITY_PRESET hidden
        )
        install(TARGETS ${MODULE_NAME} LIBRARY DESTINATION ${CMAKE_LIBRARY_OUTPUT_DIRECTORY})
    endif()
endmacro()

# ==================== 添加所有子模块 ====================
add_shared_subdirectory(${MODULES_BASE_DIR}/ARMeilleure)
add_shared_subdirectory(${MODULES_BASE_DIR}/LibRyujinx.NativeSample)
# ...（其他子模块添加，保持原有顺序）

# ==================== 第三方库处理 ====================
# SDL2
add_subdirectory(${THIRDPARTY_DIR}/SDL2)
if(TARGET SDL2)
    target_include_directories(SDL2 PUBLIC ${THIRDPARTY_DIR}/SDL2/include)
    install(TARGETS SDL2 LIBRARY DESTINATION ${CMAKE_LIBRARY_OUTPUT_DIRECTORY})
endif()

# FFmpeg
add_subdirectory(${THIRDPARTY_DIR}/FFmpeg)
if(TARGET avcodec)
    target_link_libraries(avcodec INTERFACE log dl z)
    install(TARGETS avcodec avutil swresample swscale
        LIBRARY DESTINATION ${CMAKE_LIBRARY_OUTPUT_DIRECTORY}
    )
endif()

# ==================== 主库定义与链接 ====================
add_library(ryujinxjni SHARED vulkan_wrapper.cpp ryujinx.cpp)
set_target_properties(ryujinxjni PROPERTIES SUFFIX ".so")

# 显式链接系统库和第三方库
target_link_libraries(ryujinxjni
    log dl z m  # Android 系统库
    android vulkan  # NDK 提供的库
    ${OPENSSL_BUILD_DIR}/libcrypto.a
    ${OPENSSL_BUILD_DIR}/libssl.a
    # 子模块和第三方库
    ARMeilleure LibRyujinx SDL2 avcodec avutil swresample swscale
    # ...（其他子模块，保持原有顺序）
)

# ==================== 头文件包含 ====================
target_include_directories(ryujinxjni PRIVATE
    ${OPENSSL_ROOT}/include
    ${MODULES_BASE_DIR}/Ryujinx.Common/include
    ${MODULES_BASE_DIR}/Ryujinx.Cpu/include
    ${MODULES_BASE_DIR}/Ryujinx.Graphics/include
    ${THIRDPARTY_DIR}/SDL2/include
    ${THIRDPARTY_DIR}/FFmpeg/include
)

# ==================== 安装指令 ====================
install(TARGETS ryujinxjni LIBRARY DESTINATION ${CMAKE_LIBRARY_OUTPUT_DIRECTORY})
