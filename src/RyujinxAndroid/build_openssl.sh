#!/bin/bash
# File: build_openssl.sh
# 彻底修复 "target already defined" 错误
# 适配 NDK 25+ 和 OpenSSL 3.2.1

set -euo pipefail

# ======================== 用户配置 ========================
TARGET_ARCH="android-arm64"          # 目标架构（仅在此处定义）
TARGET_API_LEVEL="30"                # Android API 级别
NDK_VERSION="25.2.9519653"           # 必须与本地安装一致
OPENSSL_VERSION="3.2.1"              # OpenSSL 版本
# =========================================================

# --------------------- 路径配置 ---------------------
ANDROID_HOME="${ANDROID_HOME:-$ANDROID_SDK_ROOT}"
NDK_ROOT="${ANDROID_HOME}/ndk/${NDK_VERSION}"
OPENSSL_SOURCE_URL="https://www.openssl.org/source/openssl-${OPENSSL_VERSION}.tar.gz"
OUTPUT_DIR="${PWD}/openssl-out/${TARGET_ARCH}"

# --------------------- 工具链强制配置 ---------------------
TOOLCHAIN_DIR="${NDK_ROOT}/toolchains/llvm/prebuilt/darwin-x86_64"  # 强制指定路径
export CC="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang"
export AR="${TOOLCHAIN_DIR}/bin/llvm-ar"
export RANLIB="${TOOLCHAIN_DIR}/bin/llvm-ranlib"
export PATH="${TOOLCHAIN_DIR}/bin:${PATH}"

# --------------------- 关键修复点 ---------------------
clean_environment() {
  # 清除所有可能干扰的环境变量
  unset CROSS_COMPILE
  unset ANDROID_NDK
  unset ANDROID_TOOLCHAIN
  echo "=== 环境变量已清理 ==="
}

# --------------------- 源码处理 ---------------------
prepare_source() {
  echo "=== 准备源码 ==="
  rm -rf openssl-src
  mkdir -p openssl-src
  cd openssl-src

  # 下载源码（带校验）
  if [ ! -f "openssl-${OPENSSL_VERSION}.tar.gz" ]; then
    echo "下载 OpenSSL..."
    curl -L -O "${OPENSSL_SOURCE_URL}"
    echo "验证文件完整性..."
    shasum -a 256 "openssl-${OPENSSL_VERSION}.tar.gz" | grep -q "c5ac01e760ee6ff0dab61d6b2bbd30146724d063eb322180c6f18a6f74e4b6aa" || {
      echo "::error::文件校验失败"
      exit 1
    }
  fi

  tar xzf "openssl-${OPENSSL_VERSION}.tar.gz"
  cd "openssl-${OPENSSL_VERSION}"
}

# --------------------- 配置阶段（关键修复）---------------------
configure_openssl() {
  echo "=== 配置参数验证 ==="
  echo "目标架构: ${TARGET_ARCH}"
  echo "API 级别: ${TARGET_API_LEVEL}"
  
  # 严格参数格式控制
  ./Configure \
    "${TARGET_ARCH}" \  # 作为第一个独立参数
    --prefix="${OUTPUT_DIR}" \
    -D__ANDROID_API__=${TARGET_API_LEVEL} \
    --sysroot="${TOOLCHAIN_DIR}/sysroot" \
    -fPIC \
    -fstack-protector-strong \
    no-shared \
    no-tests \
    no-legacy \
    -static

  # 结果验证
  if [ ! -f "Makefile" ]; then
    echo "::error::配置失败，请检查："
    echo "1. 确保未在参数中重复定义目标架构"
    echo "2. 检查 config.log 获取详细信息"
    tail -n 50 config.log
    exit 1
  fi
}

# --------------------- 编译安装 ---------------------
build_openssl() {
  echo "=== 开始编译 ==="
  make -j$(sysctl -n hw.logicalcpu) V=1
  
  echo "=== 安装到 ${OUTPUT_DIR} ==="
  make install_sw

  # 最终验证
  if [ ! -f "${OUTPUT_DIR}/lib/libcrypto.a" ]; then
    echo "::error::关键文件缺失"
    exit 1
  fi
}

# --------------------- 主流程 ---------------------
clean_environment
prepare_source
configure_openssl
build_openssl

echo "========================================"
echo "编译成功！产物路径: ${OUTPUT_DIR}"
echo "架构验证结果:"
file "${OUTPUT_DIR}/lib/libcrypto.a"
echo "========================================"
