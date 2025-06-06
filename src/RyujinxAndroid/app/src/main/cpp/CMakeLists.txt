include(FetchContent)

# For more information about using CMake with Android Studio, read the
# documentation: https://d.android.com/studio/projects/add-native-code.html

# Sets the minimum version of CMake required to build the native library.

cmake_minimum_required(VERSION 3.22.1)

# Declares and names the project.

project("ryujinxjni")

# 设置构建类型
set(CMAKE_BUILD_TYPE Release)

替代 check_ipo_supported 的兼容方案
function(check_lto_support)
    # 尝试编译一个简单的 LTO 测试程序
    file(WRITE ${CMAKE_BINARY_DIR}/lto_test.c "int main() { return 0; }")
    try_compile(LTO_SUPPORTED ${CMAKE_BINARY_DIR} ${CMAKE_BINARY_DIR}/lto_test.c
        COMPILE_DEFINITIONS "-flto"
        OUTPUT_VARIABLE LTO_OUTPUT
    )
    
    if(LTO_SUPPORTED)
        message(STATUS "LTO supported by compiler")
        set(LTO_ENABLED TRUE PARENT_SCOPE)
    else()
        message(STATUS "LTO not supported: ${LTO_OUTPUT}")
        set(LTO_ENABLED FALSE PARENT_SCOPE)
    endif()
endfunction()

check_lto_support()

set(CMAKE_C_FLAGS_RELEASE "${CMAKE_C_FLAGS_RELEASE} -O3 -fomit-frame-pointer -fno-stack-protector")
set(CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE} -O3 -fomit-frame-pointer -fno-stack-protector")

set(CMAKE_C_FLAGS_RELEASE "${CMAKE_C_FLAGS_RELEASE} -s")
set(CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE} -s")
   
# 启用 NEON 指令集
set(ANDROID_ARM_NEON TRUE)

# 其他选项
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded)
set(SKIA_USE_SYSTEM_EXPAT OFF)
set(SKIA_ENABLE_PARAGRAPH ON)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://git.ryujinx.app/kenji-nx/libadrenotools.git
   # GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1 # v1.0-14-gdeec5f7
)

FetchContent_MakeAvailable(adrenotools)

# Creates and names a library, sets it as either STATIC
# or SHARED, 和 provides the relative paths to its source code.
# You can define multiple libraries, 和 CMake builds them for you.
# Gradle automatically packages shared libraries with your APK.

add_library( # Sets the name of the library.
            ryujinxjni

            # Sets the library as a shared library.
            SHARED

            # Provides a relative path to your source file(s).
            vulkan_wrapper.cpp
        ryujinx.cpp)

# Searches for a specified prebuilt library and stores the path as a
# variable. Because CMake includes system libraries in the search path by
# default, you only need to specify the name of the public NDK library
# you want to add. CMake verifies that the library exists before
# completing its build.

find_library( # Sets the name of the path variable.
              log-lib

              # Specifies the name of the NDK library that
              # you want CMake to locate.
              log )

# Specifies libraries CMake should link to your target library. You
# can link multiple libraries, such as libraries you define in this
# build script, prebuilt third-party libraries, or system libraries.

target_link_libraries( # Specifies the target library.
                       ryujinxjni
                       # Links the target library to the log library
                       # included in the NDK.
                       ${log-lib}
                        -lvulkan
                       -landroid
                        adrenotools
                        -flto=thin
                        -Wl,-z,now
        )

# Build external libraries if prebuilt files don't exist
set(JNI_PATH ../jniLibs/${CMAKE_ANDROID_ARCH_ABI})
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif ()
