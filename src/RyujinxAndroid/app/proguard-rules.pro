# ==============================
# Ryujinx Android - ProGuard Rules
# 目标：最小化混淆，确保 JNI 和反射正常工作
# ==============================

# --- 基础配置 ---
# 不压缩（我们更关心稳定性而非 APK 大小）
-dontshrink
# 不优化（避免破坏 JNI 和复杂逻辑）
-dontoptimize
# 启用混淆（可选，但需谨慎）
# -dontobfuscate # 如果你完全不想混淆，取消此行注释

# --- 保留关键属性 ---
-keepattributes Signature
-keepattributes *Annotation*
-keepattributes InnerClasses
-keepattributes EnclosingMethod
-keepattributes SourceFile,LineNumberTable # 保留行号，便于崩溃分析

# --- 保留所有 JNI 方法 ---
# 这是最关键的一行！确保所有 Native 方法不被移除或重命名
-keepclasseswithmembernames class * {
    native <methods>;
}

# --- 保留 Ryujinx 的入口点和 JNI Helper 类 ---
-keep public class org.ryujinx.android.MainActivity { *; }
-keep public class org.ryujinx.android.NativeHelpers { *; }
-keep public class org.ryujinx.android.** { *; }

# --- 保留 Android 组件 ---
-keep public class * extends android.app.Activity
-keep public class * extends android.app.Application
-keep public class * extends android.app.Service
-keep public class * extends android.content.BroadcastReceiver
-keep public class * extends android.content.ContentProvider
-keep public class * extends androidx.fragment.app.Fragment

# --- 忽略已知安全的警告 ---
-dontwarn java.awt.**
-dontwarn javax.lang.model.element.Modifier
-dontwarn kotlin.reflect.jvm.internal.**
-dontwarn kotlinx.coroutines.flow.**

# --- 移除无用的配置 ---
# -optimizationpasses 5 # 移除，因为我们禁用了优化
# -allowaccessmodification # 移除，无必要
# -repackageclasses '' # 移除，无必要
# -assumenosideeffects # 移除，太危险！
