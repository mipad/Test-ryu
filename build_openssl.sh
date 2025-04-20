name: Android CI (NDK 25 + OpenSSL Fix)

on:
  push:
    branches: [ "1" ]
  workflow_dispatch:

jobs:
  build:
    runs-on: macos-latest
    env:
      NDK_VERSION: "25.2.9519653"
      TARGET_API_LEVEL: "30"
      WORKSPACE_PATH: ${{ github.workspace }}/Test-ryu

    steps:
      # ----------------------------------------
      # 1. 检出代码（包含子模块）
      # ----------------------------------------
      - name: Checkout code
        uses: actions/checkout@v4
        with:
          repository: mipad/Test-ryu
          ref: "1"
          path: Test-ryu
          submodules: 'recursive'

      # ----------------------------------------
      # 2. 安装基础工具链
      # ----------------------------------------
      - name: Install build tools
        run: |
          brew install nasm coreutils
          echo "=== 工具版本 ==="
          nasm -v | head -n1

      # ----------------------------------------
      # 3. 安装指定版本 NDK
      # ----------------------------------------
      - name: Install NDK ${{ env.NDK_VERSION }}
        run: |
          yes | $ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager "ndk;${{ env.NDK_VERSION }}" > ndk_install.log
          echo "=== NDK 安装验证 ==="
          find $ANDROID_HOME/ndk/${{ env.NDK_VERSION }} -name "aarch64-linux-android${{ env.TARGET_API_LEVEL }}-clang"

      # ----------------------------------------
      # 4. 配置 NDK 工具链（关键修复）
      # ----------------------------------------
      - name: Configure NDK Toolchain
        id: ndk-config
        run: |
          NDK_DIR="$ANDROID_HOME/ndk/${{ env.NDK_VERSION }}"
          
          # 动态探测工具链路径
          if [ -d "$NDK_DIR/toolchains/llvm/prebuilt/darwin-x86_64" ]; then
            NDK_TOOLCHAIN="$NDK_DIR/toolchains/llvm/prebuilt/darwin-x86_64"
          else
            NDK_TOOLCHAIN="$NDK_DIR/toolchains/llvm/prebuilt/darwin-arm64"
          fi

          # 严格验证编译器存在性
          CLANG_PATH="$NDK_TOOLCHAIN/bin/aarch64-linux-android${{ env.TARGET_API_LEVEL }}-clang"
          if [ ! -f "$CLANG_PATH" ]; then
            echo "::error::Clang 编译器不存在: $CLANG_PATH"
            exit 1
          fi

          # 注入环境变量
          echo "NDK_TOOLCHAIN=$NDK_TOOLCHAIN" >> $GITHUB_ENV
          echo "CC=$CLANG_PATH" >> $GITHUB_ENV
          echo "CXX=$NDK_TOOLCHAIN/bin/aarch64-linux-android${{ env.TARGET_API_LEVEL }}-clang++" >> $GITHUB_ENV
          echo "$NDK_TOOLCHAIN/bin" >> $GITHUB_PATH

          # 生成 local.properties
          echo "ndk.dir=$NDK_DIR" > ${{ env.WORKSPACE_PATH }}/src/RyujinxAndroid/local.properties

      # ----------------------------------------
      # 5. 手动编译 OpenSSL（核心修复）
      # ----------------------------------------
      - name: Build OpenSSL manually
        run: |
          cd ${{ env.WORKSPACE_PATH }}/src/RyujinxAndroid/app
          mkdir -p .openssl && cd .openssl
          
          # 下载源码
          curl -OL https://www.openssl.org/source/openssl-3.2.1.tar.gz
          tar xzf openssl-3.2.1.tar.gz
          cd openssl-3.2.1
          
          # 配置编译参数（关键修复点）
          export ANDROID_NDK_HOME=$NDK_TOOLCHAIN
          ./Configure android-arm64 \
            --prefix=$PWD/install \
            -D__ANDROID_API__=${{ env.TARGET_API_LEVEL }} \
            -fPIC \
            -fstack-protector-strong \
            --sysroot="$NDK_TOOLCHAIN/sysroot" \
            -static \
            no-shared \
            no-tests \
            no-legacy

          # 执行编译（显式指定工具链）
          make CC="$CC" \
               AR="$NDK_TOOLCHAIN/bin/llvm-ar" \
               RANLIB="$NDK_TOOLCHAIN/bin/llvm-ranlib" \
               LD="$NDK_TOOLCHAIN/bin/ld" \
               -j4
          
          make install_sw
          
          # 注入路径
          echo "OPENSSL_ROOT_DIR=$PWD/install" >> $GITHUB_ENV
          echo "=== OpenSSL 产物 ==="
          ls -l $PWD/install/lib/lib*.a

      # ----------------------------------------
      # 6. 构建项目
      # ----------------------------------------
      - name: Build with Gradle
        run: |
          cd ${{ env.WORKSPACE_PATH }}/src/RyujinxAndroid
          chmod +x gradlew
          
          ./gradlew clean assembleRelease \
            -Pandroid.ndkPath="$NDK_DIR" \
            -Pandroid.extraLdFlags="-Wl,--sysroot=$NDK_TOOLCHAIN/sysroot" \
            -Popenssl.root="$OPENSSL_ROOT_DIR" \
            --stacktrace \
            --info \
            --console=verbose
          
          # 产物验证
          echo "=== 产物架构验证 ==="
          find . -name "*.so" -exec file {} \; | grep "ARM aarch64"

      # ----------------------------------------
      # 7. 上传产物
      # ----------------------------------------
      - name: Upload artifacts
        uses: actions/upload-artifact@v4
        with:
          name: ryujinx-build-${{ github.run_number }}
          path: |
            ${{ env.WORKSPACE_PATH }}/src/RyujinxAndroid/app/build/outputs/apk/**/*.apk
            ${{ env.WORKSPACE_PATH }}/src/RyujinxAndroid/app/build/intermediates/stripped_native_libs/**/*.so
          retention-days: 7
