#!/bin/bash
# File: build_openssl.sh
# 完全适配 NDK 25+ 的 OpenSSL 3.2.1 编译脚本
# 修复 "no NDK aarch64-linux-android-gcc" 错误

set -euo pipefail

# ======================== 用户配置区域 ========================
TARGET_ARCH="android-arm64"          # 目标架构
TARGET_API_LEVEL="30"                # Android API 级别
NDK_VERSION="25.2.9519653"           # NDK 版本（必须准确）
OPENSSL_VERSION="3.2.1"              # OpenSSL 版本
# =============================================================

# --------------------- 路径计算 ---------------------
ANDROID_HOME="${ANDROID_HOME:-$ANDROID_SDK_ROOT}"
NDK_ROOT="${ANDROID_HOME}/ndk/${NDK_VERSION}"
OPENSSL_SOURCE_URL="https://www.openssl.org/source/openssl-${OPENSSL_VERSION}.tar.gz"
OUTPUT_DIR="${PWD}/openssl-out/${TARGET_ARCH}"

# --------------------- 工具链探测 ---------------------
detect_toolchain() {
  local prebuilt_dir="${NDK_ROOT}/toolchains/llvm/prebuilt"
  
  # 严格路径验证
  if [ ! -d "${prebuilt_dir}" ]; then
    echo "::error::NDK 工具链目录未找到: ${prebuilt_dir}"
    exit 1
  fi

  # 动态适配 macOS 架构
  local toolchain_subdir
  if [ -d "${prebuilt_dir}/darwin-x86_64" ]; then
    toolchain_subdir="darwin-x86_64"
  elif [ -d "${prebuilt_dir}/darwin-arm64" ]; then
    toolchain_subdir="darwin-arm64"
  else
    echo "::error::未找到有效的工具链目录"
    echo "当前存在的目录:"
    ls -l "${prebuilt_dir}"
    exit 1
  fi

  echo "${prebuilt_dir}/${toolchain_subdir}"
}

TOOLCHAIN_DIR=$(detect_toolchain)

# --------------------- 环境变量设置 ---------------------
export CC="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang"
export CXX="${TOOLCHAIN_DIR}/bin/aarch64-linux-android${TARGET_API_LEVEL}-clang++"
export AR="${TOOLCHAIN_DIR}/bin/llvm-ar"
export RANLIB="${TOOLCHAIN_DIR}/bin/llvm-ranlib"
export LD="${TOOLCHAIN_DIR}/bin/ld"
export STRIP="${TOOLCHAIN_DIR}/bin/llvm-strip"
export NM="${TOOLCHAIN_DIR}/bin/llvm-nm"
export PATH="${TOOLCHAIN_DIR}/bin:${PATH}"

# --------------------- 源码准备 ---------------------
prepare_source() {
  mkdir -p openssl-src
  cd openssl-src

  # 带重试机制的下载
  if [ ! -f "openssl-${OPENSSL_VERSION}.tar.gz" ]; then
    echo "正在下载 OpenSSL ${OPENSSL_VERSION}..."
    for i in {1..3}; do
      if curl -L -O "${OPENSSL_SOURCE_URL}"; then
        break
      elif [ $i -eq 3 ]; then
        echo "::error::下载失败，请检查网络或版本号"
        exit 1
      fi
      sleep 5
    done
  fi

  # 解压验证
  if [ ! -d "openssl-${OPENSSL_VERSION}" ]; then
    echo "解压源码..."
    if ! tar xzf "openssl-${OPENSSL_VERSION}.tar.gz"; then
      echo "::error::解压失败，文件可能损坏"
      exit 1
    fi
  fi

  cd "openssl-${OPENSSL_VERSION}"
}

# --------------------- 配置阶段 ---------------------
configure_openssl() {
  echo "=== 关键路径验证 ==="
  echo "Sysroot 路径: ${TOOLCHAIN_DIR}/sysroot"
  ls -l "${TOOLCHAIN_DIR}/sysroot/usr/include"  # 验证头文件存在性
  
  echo "=== 开始配置 OpenSSL ==="
  
  # 强制覆盖所有工具链参数
  ./Configure ${TARGET_ARCH} \
    --prefix="${OUTPUT_DIR}" \
    -D__ANDROID_API__=${TARGET_API_LEVEL} \
    --sysroot="${TOOLCHAIN_DIR}/sysroot" \
    -fPIC \
    -fstack-protector-strong \
    -Wno-unused-command-line-argument \
    -Wno-macro-redefined \
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

  # 严格配置结果检查
  if [ ! -f "Makefile" ]; then
    echo "::error::配置失败，未生成 Makefile"
    echo "=== config.log 错误片段 ==="
    grep -i error config.log | tail -n 20
    exit 1
  fi
}

# --------------------- 编译阶段 ---------------------
build_openssl() {
  echo "=== 开始编译 ==="
  make -j$(sysctl -n hw.logicalcpu) V=1
  
  echo "=== 安装到 ${OUTPUT_DIR} ==="
  make install_sw

  # 产物存在性验证
  if [ ! -f "${OUTPUT_DIR}/lib/libcrypto.a" ]; then
    echo "::error::libcrypto.a 未生成"
    exit 1
  fi

  # 架构验证
  if ! file "${OUTPUT_DIR}/lib/libcrypto.a" | grep -q "ARM aarch64"; then
    echo "::error::产物架构不匹配"
    exit 1
  fi
}

# --------------------- 主流程 ---------------------
prepare_source
configure_openssl
build_openssl

echo "========================================"
echo "OpenSSL 编译成功！"
echo "输出目录: ${OUTPUT_DIR}"
echo "========================================"
