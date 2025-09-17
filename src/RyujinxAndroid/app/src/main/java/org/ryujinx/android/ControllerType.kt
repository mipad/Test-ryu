package org.ryujinx.android

import kotlinx.serialization.Serializable

@Serializable
enum class ControllerType {
    PRO_CONTROLLER,
    JOYCON_LEFT,
    JOYCON_RIGHT,
    JOYCON_PAIR,
    HANDHELD
}
