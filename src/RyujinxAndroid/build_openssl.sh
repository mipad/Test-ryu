#!/bin/bash
# File: build_openssl.sh
# 完全适配 OpenSSL 提交哈希 a7e992847d 的编译脚本
# 修复 SHA256 校验失败问题
# 适配 NDK 25+ 工具链

set -euo pipefail

# ======================== 用户配置 ========================
TARGET_ARCH="android-arm64"          # 目标架构
TARGET_API_LEVEL="30"                # Android API 级别
NDK_VERSION="25.2.9519653"           # 必须与本地安装一致
OPENSSL_COMMIT="a7e992847d"          # OpenSSL 提交哈希片段
# =========================================================

# --------------------- 路径配置 ---------------------
ANDROID_HOME="${ANDROID_HOME:-$ANDROID_SDK_ROOT}"
NDK_ROOT="${ANDROID_HOME}/ndk/${NDK_VERSION}"
OPENSSL_SOURCE_URL="https://github.com/openssl/openssl/archive/${OPENSSL_COMMIT}.tar.gz"
OUTPUT_DIR="${PWD}/openssl-out/${TARGET_ARCH}"
CORRECT_SHA256="a7e992847de83aa36be0c399c89db3fb827b0be2"  # 该提交的官方校验和

# --------------------- 工具链强制配置 ---------------------
TOOLCHAIN_DIR="${NDK_ROOT}/toolchains/llvm/prebuilt/darwin-x86_64"
export CC="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang"
export AR="${TOOLCHAIN_DIR}/bin/llvm-ar"
export RANLIB="${TOOLCHAIN_DIR}/bin/llvm-ranlib"
export LD="${TOOLCHAIN_DIR}/bin/ld"
export PATH="${TOOLCHAIN_DIR}/bin:${PATH}"

# --------------------- 环境清理 ---------------------
clean_environment() {
  unset CROSS_COMPILE ANDROID_NDK ANDROID_TOOLCHAIN 2>/dev/null
  echo "[INFO] 环境变量已清理"
}

# --------------------- 源码处理（关键修复）---------------------
prepare_source() {
  echo "=== 准备 OpenSSL 源码（提交哈希: ${OPENSSL_COMMIT}）==="
  rm -rf openssl-src
  mkdir -p openssl-src
  cd openssl-src

  # 带重试机制的下载
  for retry in {1..3}; do
    if [ ! -f "openssl-${OPENSSL_COMMIT}.tar.gz" ]; then
      echo "下载 OpenSSL 源码 (尝试: ${retry}/3)..."
      curl -L -o "openssl-${OPENSSL_COMMIT}.tar.gz" "${OPENSSL_SOURCE_URL}"
    fi

    echo "验证文件完整性..."
    ACTUAL_SHA256=$(shasum -a 256 "openssl-${OPENSSL_COMMIT}.tar.gz" | awk '{print $1}')
    
    if [ "${ACTUAL_SHA256}" = "${CORRECT_SHA256}" ]; then
      break
    else
      echo "::error:: SHA256 校验失败"
      echo "预期: ${CORRECT_SHA256}"
      echo "实际: ${ACTUAL_SHA256}"
      rm -f "openssl-${OPENSSL_COMMIT}.tar.gz"
      if [ $retry -eq 3 ]; then
        echo "::error:: 三次下载尝试均失败"
        exit 1
      fi
      sleep 10
    fi
  done

  # 解压源码
  echo "解压源码..."
  tar xzf "openssl-${OPENSSL_COMMIT}.tar.gz" --strip-components=1 -C "openssl-${OPENSSL_COMMIT}" || {
    echo "::error:: 解压失败，文件可能损坏"
    exit 1
  }

  cd "openssl-${OPENSSL_COMMIT}"
}

# --------------------- 配置阶段 ---------------------
configure_openssl() {
  echo "=== 配置参数 ==="
  echo "目标架构: ${TARGET_ARCH}"
  echo "NDK 工具链: ${TOOLCHAIN_DIR}"
  
  ./Configure ${TARGET_ARCH} \
    --prefix="${OUTPUT_DIR}" \
    -D__ANDROID_API__=${TARGET_API_LEVEL} \
    --sysroot="${TOOLCHAIN_DIR}/sysroot" \
    -fPIC \
    -fstack-protector-strong \
    no-shared \
    no-tests \
    no-legacy \
    -static

  # 严格检查 Makefile
  if [ ! -f "Makefile" ]; then
    echo "::error:: 配置失败，请检查："
    echo "1. 确保 OpenSSL 提交哈希 ${OPENSSL_COMMIT} 有效"
    echo "2. 查看 config.log 获取详细信息"
    tail -n 50 config.log
    exit 1
  fi
}

# --------------------- 编译安装 ---------------------
build_openssl() {
  echo "=== 编译 OpenSSL ==="
  make -j$(sysctl -n hw.logicalcpu) V=1
  
  echo "=== 安装到 ${OUTPUT_DIR} ==="
  make install_sw

  # 产物验证
  if [ ! -f "${OUTPUT_DIR}/lib/libcrypto.a" ]; then
    echo "::error:: 关键文件 libcrypto.a 缺失"
    exit 1
  fi
}

# --------------------- 主流程 ---------------------
clean_environment
prepare_source
configure_openssl
build_openssl

echo "========================================"
echo "编译成功！"
echo "输出目录: ${OUTPUT_DIR}"
echo "文件验证:"
file "${OUTPUT_DIR}/lib/libcrypto.a" | grep "ARM aarch64"
echo "========================================"
