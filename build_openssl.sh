#!/bin/bash
set -euo pipefail

# ------------------------------
# 基础配置
# ------------------------------
TARGET_ARCH="android-arm64"
TARGET_API_LEVEL="30"
NDK_VERSION="25.2.9519653"
OPENSSL_VERSION="3.2.1"
OPENSSL_SOURCE_URL="https://www.openssl.org/source/openssl-${OPENSSL_VERSION}.tar.gz"
OUTPUT_DIR="${PWD}/openssl-out/${TARGET_ARCH}"

# ------------------------------
# 探测 NDK 工具链路径（严格验证）
# ------------------------------
ANDROID_HOME="${ANDROID_HOME:-$ANDROID_SDK_ROOT}"
NDK_ROOT="${ANDROID_HOME}/ndk/${NDK_VERSION}"

# 工具链路径探测函数
detect_toolchain() {
  local prebuilt_dir="${NDK_ROOT}/toolchains/llvm/prebuilt"
  if [ -d "${prebuilt_dir}/darwin-x86_64" ]; then
    echo "${prebuilt_dir}/darwin-x86_64"
  elif [ -d "${prebuilt_dir}/darwin-arm64" ]; then
    echo "${prebuilt_dir}/darwin-arm64"
  else
    echo "::error::NDK 工具链目录未找到：${prebuilt_dir}" >&2
    exit 1
  fi
}

TOOLCHAIN_DIR=$(detect_toolchain)

# ------------------------------
# 设置编译器环境变量（关键修正）
# ------------------------------
export CC="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang"
export CXX="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang++"
export AR="${TOOLCHAIN_DIR}/bin/llvm-ar"
export RANLIB="${TOOLCHAIN_DIR}/bin/llvm-ranlib"
export LD="${TOOLCHAIN_DIR}/bin/ld"
export PATH="${TOOLCHAIN_DIR}/bin:${PATH}"

# ------------------------------
# 下载并解压源码
# ------------------------------
mkdir -p openssl-src
cd openssl-src

if [ ! -f "openssl-${OPENSSL_VERSION}.tar.gz" ]; then
  echo "正在下载 OpenSSL ${OPENSSL_VERSION}..."
  curl -L -O "${OPENSSL_SOURCE_URL}" || { echo "::error::下载失败"; exit 1; }
fi

if [ ! -d "openssl-${OPENSSL_VERSION}" ]; then
  echo "解压源码..."
  tar xzf "openssl-${OPENSSL_VERSION}.tar.gz"
fi

cd "openssl-${OPENSSL_VERSION}"

# ------------------------------
# 配置 OpenSSL（强制指定所有参数）
# ------------------------------
echo "正在配置 OpenSSL..."
./Configure ${TARGET_ARCH} \
  --prefix="${OUTPUT_DIR}" \
  -D__ANDROID_API__=${TARGET_API_LEVEL} \
  --sysroot="${TOOLCHAIN_DIR}/sysroot" \
  -fPIC \
  -fstack-protector-strong \
  -no-shared \
  -no-tests \
  -no-legacy \
  CC="${CC}" \
  CXX="${CXX}" \
  AR="${AR}" \
  RANLIB="${RANLIB}" \
  LD="${LD}" \
  -static

# 严格检查配置结果
if [ ! -f "Makefile" ]; then
  echo "::error::OpenSSL 配置失败，未生成 Makefile"
  echo "=== 配置日志 ==="
  cat config.log
  exit 1
fi

# ------------------------------
# 编译并安装（启用详细日志）
# ------------------------------
echo "开始编译..."
make -j$(sysctl -n hw.logicalcpu) V=1

echo "安装到 ${OUTPUT_DIR}..."
make install_sw

# ------------------------------
# 验证产物
# ------------------------------
if [ ! -f "${OUTPUT_DIR}/lib/libcrypto.a" ]; then
  echo "::error::libcrypto.a 未生成"
  exit 1
fi

echo "OpenSSL 编译成功！"
