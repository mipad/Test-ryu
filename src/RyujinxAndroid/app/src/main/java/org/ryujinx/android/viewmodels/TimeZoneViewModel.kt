package org.ryujinx.android.viewmodels

import androidx.lifecycle.ViewModel
import org.ryujinx.android.RyujinxNative

class TimeZoneViewModel : ViewModel() {
    fun getTimeZoneList(): Array<String> {
        return RyujinxNative.jnaInstance.deviceGetTimeZoneList()
    }
    
    fun setTimeZone(timeZone: String) {
        // 这里需要实现设置时区的逻辑
        // 你可能需要在JNI层添加一个设置时区的方法
        RyujinxNative.jnaInstance.setTimeZone(timeZone)
    }
    
    fun getCurrentTimeZone(): String {
        // 这里需要实现获取当前时区的逻辑
        // 你可能需要在JNI层添加一个获取当前时区的方法
        return RyujinxNative.jnaInstance.getCurrentTimeZone()
    }
}
