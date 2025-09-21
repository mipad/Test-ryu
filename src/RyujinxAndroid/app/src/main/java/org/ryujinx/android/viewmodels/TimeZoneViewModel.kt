package org.ryujinx.android.viewmodels

import androidx.lifecycle.ViewModel
import org.ryujinx.android.RyujinxNative

class TimeZoneViewModel : ViewModel() {
    fun getTimeZoneList(): Array<String> {
        return RyujinxNative.getTimeZoneList()
    }
    
    fun setTimeZone(timeZone: String) {
        RyujinxNative.setTimeZone(timeZone)
    }
    
    fun getCurrentTimeZone(): String {
        return RyujinxNative.getCurrentTimeZone()
    }
}
