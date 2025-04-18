include(FetchContent)

cmake_minimum_required(VERSION 3.22.1)
project("ryujinxjni")

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED TRUE)

# 定义 OpenSSL 安装路径
set(OPENSSL_INSTALL_DIR "${CMAKE_BINARY_DIR}/openssl_install")
file(MAKE_DIRECTORY ${OPENSSL_INSTALL_DIR})

# 修复 adrenotools 的 PAGE_SIZE 错误（通过补丁）
# 创建补丁目录并生成补丁文件
set(ADRENOTOOLS_PATCH_DIR ${CMAKE_CURRENT_SOURCE_DIR}/patches)
file(MAKE_DIRECTORY ${ADRENOTOOLS_PATCH_DIR})

file(WRITE ${ADRENOTOOLS_PATCH_DIR}/adrenotools_fix_page_size.patch
"diff --git a/lib/linkernsbypass/android_linker_ns.cpp b/lib/linkernsbypass/android_linker_ns.cpp
--- a/lib/linkernsbypass/android_linker_ns.cpp
+++ b/lib/linkernsbypass/android_linker_ns.cpp
@@ -1,5 +1,6 @@
+#include <unistd.h>
 static void* align_ptr(void* ptr) {
-    return reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(ptr) & ~(PAGE_SIZE - 1));
+    size_t page_size = sysconf(_SC_PAGESIZE);
+    return reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(ptr) & ~(page_size - 1));
 }
 
 // 其他使用 PAGE_SIZE 的地方同理修改
")

# 配置 FetchContent 并应用补丁
FetchContent_Declare(
    adrenotools
    GIT_REPOSITORY https://github.com/bylaws/libadrenotools.git
    GIT_TAG        deec5f75ee1a8ccbe32c8780b1d17284fc87b0f1
    PATCH_COMMAND git apply ${ADRENOTOOLS_PATCH_DIR}/adrenotools_fix_page_size.patch
)

# 允许使用 PATCH_COMMAND
set(CMAKE_POLICY_DEFAULT_CMP0135 NEW)
FetchContent_MakeAvailable(adrenotools)

# 定义主库并设置输出路径
add_library(
    ryujinxjni
    SHARED
    vulkan_wrapper.cpp
    ryujinx.cpp
)

# 强制主库输出到 jniLibs 目录
set_target_properties(ryujinxjni
    PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_SOURCE_DIR}/../jniLibs/${CMAKE_ANDROID_ARCH_ABI}
)

find_library(log-lib log)

# 定义 OpenSSL 库路径
set(JNI_PATH ${OPENSSL_INSTALL_DIR}/lib)
cmake_path(ABSOLUTE_PATH JNI_PATH NORMALIZE)

cmake_path(APPEND JNI_PATH libcrypto.so OUTPUT_VARIABLE LIBCRYPTO_JNI_PATH)
cmake_path(APPEND JNI_PATH libssl.so OUTPUT_VARIABLE LIBSSL_JNI_PATH)

# 检查 OpenSSL 库是否存在，若不存在则构建
if (NOT (EXISTS ${LIBCRYPTO_JNI_PATH} AND EXISTS ${LIBSSL_JNI_PATH}))
    include(../../../../libryujinx/libs/OpenSSL.cmake)
    add_dependencies(ryujinxjni openssl)
endif()

# 后构建步骤：复制 OpenSSL 库到 jniLibs
add_custom_command(
    TARGET ryujinxjni POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy ${LIBCRYPTO_JNI_PATH} ${CMAKE_SOURCE_DIR}/../jniLibs/${CMAKE_ANDROID_ARCH_ABI}/
    COMMAND ${CMAKE_COMMAND} -E copy ${LIBSSL_JNI_PATH} ${CMAKE_SOURCE_DIR}/../jniLibs/${CMAKE_ANDROID_ARCH_ABI}/
    COMMENT "Copying OpenSSL libraries to jniLibs"
)

# 链接依赖库
target_link_libraries(
    ryujinxjni
    ${log-lib}
    -lvulkan
    -landroid
    adrenotools
    ${LIBCRYPTO_JNI_PATH}
    ${LIBSSL_JNI_PATH}
)
