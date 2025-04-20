#!/bin/bash
# File: build_openssl.sh
# 目标：为 Android arm64-v8a 稳定编译 OpenSSL
# 适配 NDK 25+ 的纯 Clang 工具链

set -euo pipefail

# ------------------------------
# 基础配置（根据项目需求修改）
# ------------------------------
TARGET_ARCH="android-arm64"
TARGET_API_LEVEL="30"
NDK_VERSION="25.2.9519653"
OPENSSL_VERSION="3.2.1"
OPENSSL_SOURCE_URL="https://www.openssl.org/source/openssl-${OPENSSL_VERSION}.tar.gz"
OUTPUT_DIR="${PWD}/openssl-out/${TARGET_ARCH}"

# ------------------------------
# NDK 工具链配置（自动探测）
# ------------------------------
ANDROID_HOME="${ANDROID_HOME:-$ANDROID_SDK_ROOT}"
NDK_ROOT="${ANDROID_HOME}/ndk/${NDK_VERSION}"

# 工具链路径探测函数
detect_toolchain() {
  local prebuilt_dir="${NDK_ROOT}/toolchains/llvm/prebuilt"
  
  # 严格检查路径存在性
  if [ ! -d "$prebuilt_dir" ]; then
    echo "::error::NDK 工具链根目录不存在: $prebuilt_dir"
    exit 1
  fi

  # 优先级：darwin-x86_64 > darwin-arm64
  if [ -d "${prebuilt_dir}/darwin-x86_64" ]; then
    echo "${prebuilt_dir}/darwin-x86_64"
  elif [ -d "${prebuilt_dir}/darwin-arm64" ]; then
    echo "${prebuilt_dir}/darwin-arm64"
  else
    echo "::error::未找到有效的 NDK 工具链目录"
    echo "请检查 NDK 版本 $NDK_VERSION 的安装"
    exit 1
  fi
}

TOOLCHAIN_DIR=$(detect_toolchain)

# ------------------------------
# 编译器环境变量（强制指定所有工具）
# ------------------------------
export CC="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang"
export CXX="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang++"
export AR="${TOOLCHAIN_DIR}/bin/llvm-ar"
export RANLIB="${TOOLCHAIN_DIR}/bin/llvm-ranlib"
export LD="${TOOLCHAIN_DIR}/bin/ld"
export STRIP="${TOOLCHAIN_DIR}/bin/llvm-strip"
export PATH="${TOOLCHAIN_DIR}/bin:${PATH}"

# ------------------------------
# 下载源码（带重试机制）
# ------------------------------
mkdir -p openssl-src
cd openssl-src

if [ ! -f "openssl-${OPENSSL_VERSION}.tar.gz" ]; then
  echo "正在下载 OpenSSL ${OPENSSL_VERSION}..."
  for i in {1..3}; do
    if curl -L -O "${OPENSSL_SOURCE_URL}"; then
      break
    elif [ $i -eq 3 ]; then
      echo "::error::下载 OpenSSL 失败"
      exit 1
    fi
    sleep 5
  done
fi

if [ ! -d "openssl-${OPENSSL_VERSION}" ]; then
  echo "解压源码..."
  tar xzf "openssl-${OPENSSL_VERSION}.tar.gz" || { echo "::error::解压失败"; exit 1; }
fi

cd "openssl-${OPENSSL_VERSION}"

# ------------------------------
# 配置阶段（关键修复点）
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
  -static \
  -Wno-macro-redefined \
  -Wno-unused-command-line-argument \
  CC="${CC}" \
  CXX="${CXX}" \
  AR="${AR}" \
  RANLIB="${RANLIB}" \
  LD="${LD}"

# 严格检查 Makefile
if [ ! -f "Makefile" ]; then
  echo "::error::配置失败，未生成 Makefile"
  echo "=== 最后 50 行配置日志 ==="
  tail -n 50 config.log
  exit 1
fi

# ------------------------------
# 编译阶段（带详细输出）
# ------------------------------
echo "开始编译..."
make -j$(sysctl -n hw.logicalcpu) V=1

echo "安装到 ${OUTPUT_DIR}..."
make install_sw

# ------------------------------
# 产物验证（严格检查）
# ------------------------------
echo "=== 产物验证 ==="
if [ ! -f "${OUTPUT_DIR}/lib/libcrypto.a" ]; then
  echo "::error::libcrypto.a 未生成"
  exit 1
fi

if ! file "${OUTPUT_DIR}/lib/libcrypto.a" | grep -q "ARM aarch64"; then
  echo "::error::libcrypto.a 架构不正确"
  exit 1
fi

echo "-----------------------------------------"
echo "OpenSSL 编译成功！"
echo "输出目录: ${OUTPUT_DIR}"
