#!/bin/bash
# File: build_openssl.sh
# 目标：为 Android arm64-v8a 编译 OpenSSL

# ------------------------------
# 1. 基础配置
# ------------------------------
TARGET_ARCH="android-arm64"
TARGET_API_LEVEL="30"
NDK_VERSION="25.2.9519653"
OPENSSL_VERSION="3.2.1"
OPENSSL_SOURCE_URL="https://www.openssl.org/source/openssl-${OPENSSL_VERSION}.tar.gz"
OUTPUT_DIR="${PWD}/openssl-out/${TARGET_ARCH}"

# ------------------------------
# 2. 探测 NDK 工具链路径（自动适配 Intel/Apple Silicon）
# ------------------------------
ANDROID_HOME="${ANDROID_HOME:-$ANDROID_SDK_ROOT}"
NDK_ROOT="${ANDROID_HOME}/ndk/${NDK_VERSION}"

detect_toolchain() {
  local prebuilt_dir="${NDK_ROOT}/toolchains/llvm/prebuilt"
  if [ -d "${prebuilt_dir}/darwin-x86_64" ]; then
    echo "${prebuilt_dir}/darwin-x86_64"
  elif [ -d "${prebuilt_dir}/darwin-arm64" ]; then
    echo "${prebuilt_dir}/darwin-arm64"
  else
    echo "错误: 未找到 NDK 工具链目录" >&2
    exit 1
  fi
}

TOOLCHAIN_DIR=$(detect_toolchain)

# ------------------------------
# 3. 设置编译器环境变量
# ------------------------------
export CC="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang"
export CXX="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang++"
export AR="${TOOLCHAIN_DIR}/bin/llvm-ar"
export RANLIB="${TOOLCHAIN_DIR}/bin/llvm-ranlib"
export PATH="${TOOLCHAIN_DIR}/bin:${PATH}"

# ------------------------------
# 4. 下载并编译 OpenSSL
# ------------------------------
mkdir -p openssl-src
cd openssl-src

if [ ! -f "openssl-${OPENSSL_VERSION}.tar.gz" ]; then
  echo "正在下载 OpenSSL ${OPENSSL_VERSION}..."
  curl -L -O "${OPENSSL_SOURCE_URL}" || exit 1
fi

if [ ! -d "openssl-${OPENSSL_VERSION}" ]; then
  tar xzf "openssl-${OPENSSL_VERSION}.tar.gz"
fi

cd openssl-${OPENSSL_VERSION}

./Configure ${TARGET_ARCH} \
  --prefix="${OUTPUT_DIR}" \
  -D__ANDROID_API__=${TARGET_API_LEVEL} \
  --sysroot="${TOOLCHAIN_DIR}/sysroot" \
  -static no-shared no-tests

make -j$(sysctl -n hw.logicalcpu)
make install_sw

# ------------------------------
# 5. 生成 CMake 配置文件
# ------------------------------
CMAKE_CONFIG_DIR="../src/RyujinxAndroid/libryujinx/libs"
mkdir -p "${CMAKE_CONFIG_DIR}"

cat > "${CMAKE_CONFIG_DIR}/OpenSSL.cmake" << EOF
set(OPENSSL_ROOT_DIR \${CMAKE_SOURCE_DIR}/openssl-out/android-arm64)
set(OPENSSL_INCLUDE_DIR \${OPENSSL_ROOT_DIR}/include)
set(OPENSSL_CRYPTO_LIBRARY \${OPENSSL_ROOT_DIR}/lib/libcrypto.a)
set(OPENSSL_SSL_LIBRARY \${OPENSSL_ROOT_DIR}/lib/libssl.a)
EOF

echo "OpenSSL 编译完成！输出目录: ${OUTPUT_DIR}"
