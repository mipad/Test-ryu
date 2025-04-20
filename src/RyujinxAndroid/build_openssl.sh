#!/bin/bash
# File: build_openssl.sh
# 完全修复 NDK 25+ 的 OpenSSL 编译问题
# 解决 "no NDK aarch64-linux-android-gcc" 错误

set -euo pipefail

# ======================== 用户配置 ========================
TARGET_ARCH="android-arm64"          # 目标架构
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
export CXX="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang++"
export AR="${TOOLCHAIN_DIR}/bin/llvm-ar"
export RANLIB="${TOOLCHAIN_DIR}/bin/llvm-ranlib"
export LD="${TOOLCHAIN_DIR}/bin/ld"
export STRIP="${TOOLCHAIN_DIR}/bin/llvm-strip"
export NM="${TOOLCHAIN_DIR}/bin/llvm-nm"
export PATH="${TOOLCHAIN_DIR}/bin:${PATH}"

# --------------------- 系统根目录验证 ---------------------
SYSROOT="${TOOLCHAIN_DIR}/sysroot"
echo "=== Sysroot 验证 ==="
echo "头文件路径: ${SYSROOT}/usr/include"
echo "库文件路径: ${SYSROOT}/usr/lib/aarch64-linux-android/${TARGET_API_LEVEL}"
ls -l "${SYSROOT}/usr/include" || { echo "::error::sysroot 头文件缺失"; exit 1; }
ls -l "${SYSROOT}/usr/lib/aarch64-linux-android/${TARGET_API_LEVEL}" || { echo "::error::sysroot 库文件缺失"; exit 1; }

# --------------------- 源码处理 ---------------------
prepare_source() {
  mkdir -p openssl-src
  cd openssl-src

  if [ ! -f "openssl-${OPENSSL_VERSION}.tar.gz" ]; then
    echo "正在下载 OpenSSL ${OPENSSL_VERSION}..."
    curl -L -O "${OPENSSL_SOURCE_URL}" || { echo "::error::下载失败"; exit 1; }
  fi

  if [ ! -d "openssl-${OPENSSL_VERSION}" ]; then
    echo "解压源码..."
    tar xzf "openssl-${OPENSSL_VERSION}.tar.gz" || { echo "::error::解压失败"; exit 1; }
  fi

  cd "openssl-${OPENSSL_VERSION}"
}

# --------------------- 配置阶段（核心修复）---------------------
configure_openssl() {
  echo "=== 环境变量验证 ==="
  echo "CC: $(which ${CC##*/})"
  echo "AR: $(which ${AR##*/})"
  
  echo "=== 开始配置 ==="
  ./Configure ${TARGET_ARCH} \
    --prefix="${OUTPUT_DIR}" \
    -D__ANDROID_API__=${TARGET_API_LEVEL} \
    --cross-compile-prefix="" \  # 关键修复：禁用自动前缀检测
    --sysroot="${SYSROOT}" \
    -fPIC \
    -fstack-protector-strong \
    -Wl,-Bsymbolic \
    -Wl,-z,noexecstack \
    -Wl,-z,relro \
    -Wl,-z,now \
    -static \
    no-shared \
    no-tests \
    no-legacy \
    CC="${CC}" \
    CXX="${CXX}" \
    AR="${AR}" \
    RANLIB="${RANLIB}" \
    LD="${LD}" \
    NM="${NM}" \
    STRIP="${STRIP}"

  # 严格检查配置结果
  if [ ! -f "Makefile" ]; then
    echo "::error::配置失败，请检查以下内容："
    echo "1. NDK 版本是否为 ${NDK_VERSION}"
    echo "2. 目标 API 级别是否为 ${TARGET_API_LEVEL}"
    echo "3. 查看 config.log 获取详细信息"
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
    echo "::error::关键文件 libcrypto.a 未生成"
    exit 1
  fi
}

# --------------------- 主流程 ---------------------
prepare_source
configure_openssl
build_openssl

echo "========================================"
echo "编译成功！输出文件："
file "${OUTPUT_DIR}/lib/libcrypto.a" 
echo "========================================"
