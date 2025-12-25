include(FetchContent)

cmake_minimum_required(VERSION 3.22.1)

project("ryujinxjni")

# 设置构建类型
set(CMAKE_BUILD_TYPE Release)

# 其他选项
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded)
set(SKIA_USE_SYSTEM_EXPAT OFF)
set(SKIA_ENABLE_PARAGRAPH ON)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# 设置 Android 特定选项
set(ANDROID_STL c++_shared)
set(ANDROID_ARM_NEON TRUE)

# 使用 FetchContent 获取 adrenotools
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://git.ryujinx.app/kenji-nx/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
)

FetchContent_MakeAvailable(adrenotools)

# 为 adrenotools 相关目标添加 PAGE_SIZE 定义
if(TARGET adrenotools)
    target_compile_definitions(adrenotools PRIVATE PAGE_SIZE=4096)
endif()

if(TARGET linkernsbypass)
    target_compile_definitions(linkernsbypass PRIVATE PAGE_SIZE=4096)
endif()

# 为 adrenotools 的子目录目标也添加定义
get_target_property(adrenotools_sources adrenotools SOURCES)
if(adrenotools_sources)
    target_compile_definitions(adrenotools PRIVATE PAGE_SIZE=4096)
endif()

# 使用 FetchContent 获取 Oboe 源码
FetchContent_Declare(
    oboe
    GIT_REPOSITORY https://github.com/google/oboe.git
    GIT_TAG        1.10.0  # 使用与 Gradle 依赖相同的版本
)

# 设置 Oboe 构建选项，禁用格式转换以减少代码大小
set(OBOE_DISABLE_CONVERSION ON CACHE BOOL "Disable format conversion in Oboe")

FetchContent_MakeAvailable(oboe)

# 使用 FetchContent 获取标准 OpenAL Soft
FetchContent_Declare(
    openal
    GIT_REPOSITORY https://github.com/kcat/openal-soft.git
    GIT_TAG        1.24.3  # 使用稳定版本
)

# 设置 OpenAL 构建选项 - 优化以减少杂音和延迟
set(ALSOFT_REQUIRE_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_OPENSL ON CACHE BOOL "Enable OpenSL backend")
set(ALSOFT_BACKEND_AAUDIO ON CACHE BOOL "Enable AAudio backend")
set(ALSOFT_TESTS OFF CACHE BOOL "Disable tests")

# 添加优化选项以减少杂音和延迟
set(ALSOFT_CPUEXT_NEON ON CACHE BOOL "Enable NEON optimizations")

FetchContent_MakeAvailable(openal)

# 创建主库
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
    oboe_audio_renderer.cpp
    # MediaCodec 解码器
    mediacodec/mediacodec_decoder.cpp
    mediacodec/mediacodec_h264.cpp
    mediacodec/mediacodec_vp8.cpp
    mediacodec/mediacodec_vp9.cpp
    mediacodec/mediacodec_common.cpp
    mediacodec/yuv_converter.cpp
    mediacodec/mediacodec_jni.cpp
)

# 添加编译定义以优化音频处理
target_compile_definitions(ryujinxjni PRIVATE
    RYUJINX_AUDIO_OPTIMIZE=1
    # 如果需要，可以添加更多音频优化标志
)
# 为音频渲染器文件单独启用激进优化
# 注意：如果某些游戏出现杂音，可以回退到 "-O3" 或 "-O3 -ffast-math"
set_source_files_properties(oboe_audio_renderer.cpp PROPERTIES
    COMPILE_FLAGS "-Ofast"
)

# 为主目标添加编译选项（其他文件保持 -O3，不包含 -ffast-math）
target_compile_options(ryujinxjni PRIVATE
    -std=c++20
    -O3
    -DNDEBUG
    -march=armv8-a
    #-Ofast
)

# 查找系统库
find_library(log-lib log)
find_library(opensles-lib OpenSLES)
find_library(aaudio-lib aaudio)
find_library(android-lib android)
find_library(mediandk-lib mediandk)

# 添加 Oboe 包含目录
include_directories(${oboe_SOURCE_DIR}/include)

# 添加 MediaCodec 包含目录
include_directories(${CMAKE_CURRENT_SOURCE_DIR}/mediacodec)

# 链接库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    ${opensles-lib}
    ${aaudio-lib}
    ${android-lib}
    ${mediandk-lib}
    -lvulkan
    adrenotools
    oboe
    OpenAL
)

# 设置 JNI 库输出路径
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

# 构建 OpenSSL（如果预构建文件不存在）
if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif ()

# 设置 OpenAL 输出目录
set_target_properties(OpenAL PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 设置输出目录
set_target_properties(ryujinxjni PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${JNI_PATH}
    ARCHIVE_OUTPUT_DIRECTORY ${JNI_PATH}
    RUNTIME_OUTPUT_DIRECTORY ${JNI_PATH}
)

# 添加 MediaCodec 特定的编译定义
target_compile_definitions(ryujinxjni PRIVATE
    MEDIACODEC_ENABLED=1
    ANDROID_NDK_MEDIACODEC=1
)

# 确保所有源文件都正确包含头文件
include_directories(${CMAKE_CURRENT_SOURCE_DIR})

# 为 MediaCodec 文件添加特定的编译选项
set_source_files_properties(
    mediacodec/mediacodec_decoder.cpp
    mediacodec/mediacodec_h264.cpp
    mediacodec/mediacodec_vp8.cpp
    mediacodec/mediacodec_vp9.cpp
    mediacodec/mediacodec_common.cpp
    mediacodec/mediacodec_jni.cpp
    PROPERTIES
    COMPILE_FLAGS "-Wno-deprecated-declarations -Wno-sign-conversion"
)

# 确保编译器能找到 Android NDK 头文件
if(ANDROID_NDK)
    include_directories(${ANDROID_NDK}/sources/android/native_app_glue)
    include_directories(${ANDROID_NDK}/sources/third_party/vulkan/src/include)
    include_directories(${ANDROID_NDK}/sources/android/cpufeatures)
    include_directories(${ANDROID_NDK}/sources/cxx-stl/llvm-libc++/include)
    
    # MediaCodec NDK 头文件路径
    if(EXISTS "${ANDROID_NDK}/sources/third_party/android_ndk_media/include")
        include_directories(${ANDROID_NDK}/sources/third_party/android_ndk_media/include)
    endif()
    
    # 如果使用较新的 NDK，MediaCodec 头文件可能在以下路径
    if(EXISTS "${ANDROID_NDK}/sysroot/usr/include")
        include_directories(${ANDROID_NDK}/sysroot/usr/include)
    endif()
endif()

# 如果找不到 mediandk 库，尝试替代方案
if(NOT mediandk-lib)
    message(WARNING "libmediandk not found, trying alternative")
    find_library(media-lib media)
    if(media-lib)
        target_link_libraries(ryujinxjni ${media-lib})
    else()
        message(WARNING "Neither libmediandk nor libmedia found, MediaCodec may not work")
    endif()
endif()

# 添加构建后步骤，确保库文件被正确复制
add_custom_command(TARGET ryujinxjni POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
    $<TARGET_FILE:ryujinxjni>
    ${JNI_PATH}/libryujinxjni.so
    COMMENT "Copying ryujinxjni library to JNI directory"
)

# 确保所有依赖库也被复制
if(TARGET OpenAL)
    add_custom_command(TARGET OpenAL POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:OpenAL>
        ${JNI_PATH}/libopenal.so
        COMMENT "Copying OpenAL library to JNI directory"
    )
endif()

# 添加测试目标（可选）
if(BUILD_TESTS)
    enable_testing()
    
    # 创建测试可执行文件
    add_executable(mediacodec_test
        mediacodec/test/test_mediacodec.cpp
    )
    
    # 链接测试库
    target_link_libraries(mediacodec_test
        ${log-lib}
        ${android-lib}
        ${mediandk-lib}
        ryujinxjni
    )
    
    # 添加测试
    add_test(NAME MediaCodecTest COMMAND mediacodec_test)
endif()
