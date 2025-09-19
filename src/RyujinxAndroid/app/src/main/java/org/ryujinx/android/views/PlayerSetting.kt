// PlayerSetting.kt
package org.ryujinx.android.views

import kotlinx.serialization.Serializable

@Serializable
data class PlayerSetting(
    val playerNumber: Int,
    val isConnected: Boolean,
    var controllerType: Int // 0=Pro, 1=JoyCon L, 2=JoyCon R, 3=JoyCon Pair, 4=Handheld
)
