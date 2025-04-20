#!/bin/bash

# ---------------------------------------------------------------
# 1. 环境变量配置（与你的 GitHub Actions 完全一致）
# ---------------------------------------------------------------
export NDK_VERSION="25.2.9519653"
export ANDROID_HOME="$HOME/Library/Android/sdk"  # macOS 默认路径
export NDK_DIR="$ANDROID_HOME/ndk/$NDK_VERSION"

# 动态探测工具链路径（优先 x86_64）
if [ -d "$NDK_DIR/toolchains/llvm/prebuilt/darwin-x86_64" ]; then
    NDK_TOOLCHAIN="$NDK_DIR/toolchains/llvm/prebuilt/darwin-x86_64"
elif [ -d "$NDK_DIR/toolchains/llvm/prebuilt/darwin-arm64" ]; then
    NDK_TOOLCHAIN="$NDK_DIR/toolchains/llvm/prebuilt/darwin-arm64"
else
    echo "::error::未找到有效的工具链路径"
    exit 1
fi

export PATH="$NDK_TOOLCHAIN/bin:$PATH"
export TARGET_API_LEVEL=30  # 与你的 CI 配置一致

# ---------------------------------------------------------------
# 2. 进入 OpenSSL 源码目录（假设代码已检出）
# ---------------------------------------------------------------
cd $WORKSPACE_PATH/Test-ryu/src/RyujinxAndroid/app/.cxx/RelWithDebInfo/*/arm64-v8a/openssl

# ---------------------------------------------------------------
# 3. 清理旧配置（可选）
# ---------------------------------------------------------------
make clean
./Configure clean

# ---------------------------------------------------------------
# 4. 配置 Android 交叉编译参数（关键步骤）
# ---------------------------------------------------------------
./Configure android-arm64 \
    -D__ANDROID_API__=$TARGET_API_LEVEL \
    --prefix=$PWD/install \
    --openssldir=$PWD/openssl \
    -static \
    no-shared \
    no-tests \
    no-legacy

# ---------------------------------------------------------------
# 5. 设置编译工具链（与你的 CI 的 LDFLAGS 一致）
# ---------------------------------------------------------------
export CC="$NDK_TOOLCHAIN/bin/aarch64-linux-android$TARGET_API_LEVEL-clang"
export CXX="$NDK_TOOLCHAIN/bin/aarch64-linux-android$TARGET_API_LEVEL-clang++"
export AR="$NDK_TOOLCHAIN/bin/llvm-ar"
export RANLIB="$NDK_TOOLCHAIN/bin/llvm-ranlib"
export LD="$NDK_TOOLCHAIN/bin/ld"
export LDFLAGS="-L$NDK_TOOLCHAIN/sysroot/usr/lib/aarch64-linux-android/$TARGET_API_LEVEL"

# ---------------------------------------------------------------
# 6. 执行编译并验证
# ---------------------------------------------------------------
make -j4
make install

# ---------------------------------------------------------------
# 7. 验证产物
# ---------------------------------------------------------------
ls -l ./install/lib/libcrypto.a ./install/lib/libssl.a
file ./install/lib/libcrypto.a | grep "ARM aarch64"
