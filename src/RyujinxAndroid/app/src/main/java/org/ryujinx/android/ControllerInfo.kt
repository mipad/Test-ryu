package org.ryujinx.android

data class Controller(
    val id: String,
    val name: String,
    var controllerType: ControllerType = ControllerType.PRO_CONTROLLER,
    val isVirtual: Boolean = false,
    var deviceId: Int = -1 // 新增deviceId字段
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (javaClass != other?.javaClass) return false
        other as Controller
        return id == other.id
    }

    override fun hashCode(): Int {
        return id.hashCode()
    }
}
