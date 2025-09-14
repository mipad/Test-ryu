-optimizationpasses 5
-optimizations !code/simplification/arithmetic
-optimizations !code/simplification/cast
-optimizations !field/*
-optimizations !class/merging/*
-optimizations !method/inlining/short
-optimizations !method/inlining/unique
-optimizations method/inlining/tailrecursion
-optimizations method/removal/parameter
-optimizations code/merging
-optimizations code/simplification/variable
-allowaccessmodification
-repackageclasses ''
-keepattributes Exceptions,InnerClasses,Signature,*Annotation*
-dontpreverify
-keep public class * extends android.app.Activity
-keep public class * extends android.app.Application
-keep public class * extends android.app.Service
-keep public class * extends android.content.BroadcastReceiver
-keep public class * extends android.content.ContentProvider
-keep public class * extends androidx.fragment.app.Fragment
-dontwarn java.awt.Component
-dontwarn java.awt.GraphicsEnvironment
-dontwarn java.awt.HeadlessException
-dontwarn java.awt.Window
-dontwarn javax.lang.model.element.Modifier
-assumenosideeffects class java.lang.Math {
    public static double random();
    public static double sin(...);
    public static double cos(...);
    public static double sqrt(...);
}
-assumenosideeffects public class ** {
  public boolean is*();
  public boolean get*();
  public boolean has*();
}

# 保留所有可能被反射使用的类
-keep class org.ryujinx.HLE.HOS.Services.** { *; }
-keep class org.ryujinx.HLE.Exceptions.** { *; }

# 保留所有公共类及其公共成员（如果上述不够，可以使用更广泛的规则）
# -keep public class * {
#    public *;
# }

# 保留所有系统服务相关的类
-keep class * extends android.app.Service
-keep class * extends android.content.BroadcastReceiver

# 保留所有包含反射调用的类
-keep class **.IpcService { *; }
-keep class **.IUserInterface { *; }
-keep class **.INvDrvServices { *; }
-keep class **.ServiceNotImplementedException { *; }

# 保留所有被动态调用的方法
-keepclassmembers class * {
    @android.webkit.JavascriptInterface public *;
}

# 保留所有序列化相关的类
-keepclassmembers class * implements java.io.Serializable {
    static final long serialVersionUID;
    private static final java.io.ObjectStreamField[] serialPersistentFields;
    private void writeObject(java.io.ObjectOutputStream);
    private void readObject(java.io.ObjectInputStream);
    java.lang.Object writeReplace();
    java.lang.Object readResolve();
}

# 保留注解
-keepattributes *Annotation*

# 保留泛型信息
-keepattributes Signature

# 保留异常信息
-keepattributes Exceptions

# 保留内部类
-keepattributes InnerClasses

# 保留方法参数（对于反射获取参数名可能需要）
-keepattributes MethodParameters

# 防止混淆枚举
-keepclassmembers enum * {
    public static **[] values();
    public static ** valueOf(java.lang.String);
}

# 防止混淆自定义视图
-keepclassmembers class * extends android.view.View {
    void set*(***);
    *** get*();
}

# 防止混淆回调方法
-keepclassmembers class * {
    void *(**On*Event);
    void *(**On*Listener);
}
