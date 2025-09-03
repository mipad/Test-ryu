# 基本优化配置
-optimizationpasses 5
-dontusemixedcaseclassnames
-dontskipnonpubliclibraryclasses
-dontpreverify
-verbose

# 优化选项
-optimizations !code/simplification/arithmetic,!code/simplification/cast,!field/*,!class/merging/*,!method/inlining/short,!method/inlining/unique
-optimizations method/inlining/tailrecursion,method/removal/parameter,code/merging,code/simplification/variable
-allowaccessmodification
-repackageclasses ''

# 保留重要属性
-keepattributes Exceptions,InnerClasses,Signature,*Annotation*,EnclosingMethod

# 保留 Android 组件
-keep public class * extends android.app.Activity
-keep public class * extends android.app.Application
-keep public class * extends android.app.Service
-keep public class * extends android.content.BroadcastReceiver
-keep public class * extends android.content.ContentProvider
-keep public class * extends androidx.fragment.app.Fragment
-keep public class * extends android.view.View
-keep public class com.android.vending.licensing.ILicensingService

# 保留注解
-keepattributes *Annotation*
-keep @androidx.annotation.Keep class *
-keepclasseswithmembers class * {
    @androidx.annotation.Keep <methods>;
}
-keepclasseswithmembers class * {
    @androidx.annotation.Keep <fields>;
}
-keepclasseswithmembers class * {
    @androidx.annotation.Keep <init>(...);
}

# 保留序列化类
-keepclassmembers class * implements java.io.Serializable {
    static final long serialVersionUID;
    private static final java.io.ObjectStreamField[] serialPersistentFields;
    private void writeObject(java.io.ObjectOutputStream);
    private void readObject(java.io.ObjectInputStream);
    java.lang.Object writeReplace();
    java.lang.Object readResolve();
}

# 保留本地方法
-keepclasseswithmembernames class * {
    native <methods>;
}

# 保留枚举
-keepclassmembers enum * {
    public static **[] values();
    public static ** valueOf(java.lang.String);
}

# 保留自定义视图
-keepclassmembers class * extends android.view.View {
    void set*(***);
    *** get*();
}

# 保留Parcelable实现
-keepclassmembers class * implements android.os.Parcelable {
    public static final android.os.Parcelable$Creator CREATOR;
}

# 保留R类
-keepclassmembers class **.R$* {
    public static <fields>;
}

# 忽略警告
-dontwarn java.awt.**
-dontwarn javax.lang.model.**
-dontwarn org.w3c.dom.**
-dontwarn org.xml.sax.**
-dontwarn org.xmlpull.v1.**

# 假设某些方法没有副作用（可选）
-assumenosideeffects class java.lang.Math {
    public static double random();
    public static double sin(...);
    public static double cos(...);
    public static double sqrt(...);
}
