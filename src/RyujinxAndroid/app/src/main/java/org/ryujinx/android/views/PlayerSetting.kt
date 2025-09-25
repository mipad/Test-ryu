// PlayerSetting.kt
package org.ryujinx.android.views

import kotlinx.serialization.Serializable

@Serializable
data class PlayerSetting(
    val playerIndex: Int,      // 0-based 索引 (0-7)
    var isConnected: Boolean,  // 是否连接
    var controllerType: Int   // 0=Pro, 1=JoyCon L, 2=JoyCon R, 3=JoyCon Pair, 4=Handheld
)
