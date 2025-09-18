package org.ryujinx.android

import android.app.Activity
import android.content.Context
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.View
import android.widget.FrameLayout
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.math.MathUtils
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import com.swordfish.radialgamepad.library.RadialGamePad
import com.swordfish.radialgamepad.library.config.ButtonConfig
import com.swordfish.radialgamepad.library.config.CrossConfig
import com.swordfish.radialgamepad.library.config.CrossContentDescription
import com.swordfish.radialgamepad.library.config.PrimaryDialConfig
import com.swordfish.radialgamepad.library.config.RadialGamePadConfig
import com.swordfish.radialgamepad.library.config.SecondaryDialConfig
import com.swordfish.radialgamepad.library.event.Event
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.merge
import kotlinx.coroutines.flow.shareIn
import kotlinx.coroutines.launch
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings
import org.ryujinx.android.ControllerManager
import org.ryujinx.android.Controller
import org.ryujinx.android.ControllerType

typealias GamePad = RadialGamePad
typealias GamePadConfig = RadialGamePadConfig

class GameController(var activity: Activity) {

    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(controller.leftGamePad)
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(controller.rightGamePad)

            return view
        }

        @Composable
        fun Compose(viewModel: MainViewModel): Unit {
            AndroidView(
                modifier = Modifier.fillMaxSize(), factory = { context ->
                    val controller = GameController(viewModel.activity)
                    val c = Create(context, controller)
                    viewModel.activity.lifecycleScope.apply {
                        viewModel.activity.lifecycleScope.launch {
                            val events = merge(
                                controller.leftGamePad.events(),
                                controller.rightGamePad.events()
                            )
                                .shareIn(viewModel.activity.lifecycleScope, SharingStarted.Lazily)
                            events.safeCollect {
                                controller.handleEvent(it)
                            }
                        }
                    }
                    controller.controllerView = c
                    viewModel.setGameController(controller)
                    controller.setVisible(QuickSettings(viewModel.activity).useVirtualController)
                    
                    // 初始化时设置控制器类型
                    controller.updateControllerTypeFromSettings()
                    
                    c
                })
        }
    }

    private var controllerView: View? = null
    var leftGamePad: GamePad
    var rightGamePad: GamePad
    var controllerId: Int = -1
    val isVisible: Boolean
        get() {
            controllerView?.apply {
                return this.isVisible
            }

            return false
        }

    init {
        leftGamePad = GamePad(generateConfig(true), 16f, activity)
        rightGamePad = GamePad(generateConfig(false), 16f, activity)

        leftGamePad.primaryDialMaxSizeDp = 200f
        rightGamePad.primaryDialMaxSizeDp = 200f

        leftGamePad.gravityX = -1f
        leftGamePad.gravityY = 1f
        rightGamePad.gravityX = 1f
        rightGamePad.gravityY = 1f
        
        // 初始化时注册虚拟控制器到ControllerManager
        val virtualController = Controller(
            id = "virtual_controller_1",
            name = "MeloNX Touch Controller",
            controllerType = getInitialControllerType(), // 使用当前设置的类型
            isVirtual = true
        )
        ControllerManager.addController(activity, virtualController)
    }

    // 新增方法：获取初始控制器类型
    private fun getInitialControllerType(): ControllerType {
        val quickSettings = QuickSettings(activity)
        return when (quickSettings.controllerType) {
            0 -> ControllerType.PRO_CONTROLLER
            1 -> ControllerType.JOYCON_LEFT
            2 -> ControllerType.JOYCON_RIGHT
            3 -> ControllerType.JOYCON_PAIR
            4 -> ControllerType.HANDHELD
            else -> ControllerType.PRO_CONTROLLER
        }
    }

    // 新增方法：从设置更新控制器类型
    fun updateControllerTypeFromSettings() {
        val quickSettings = QuickSettings(activity)
        val newType = when (quickSettings.controllerType) {
            0 -> ControllerType.PRO_CONTROLLER
            1 -> ControllerType.JOYCON_LEFT
            2 -> ControllerType.JOYCON_RIGHT
            3 -> ControllerType.JOYCON_PAIR
            4 -> ControllerType.HANDHELD
            else -> ControllerType.PRO_CONTROLLER
        }
        
        updateControllerType(newType)
    }

    // 新增方法：更新控制器类型
    fun updateControllerType(newType: ControllerType) {
        // 更新ControllerManager中的控制器类型
        ControllerManager.updateControllerType(activity, "virtual_controller_1", newType)
        
        // 设置C++层的控制器类型（设备ID 0对应虚拟控制器）
        RyujinxNative.jnaInstance.setControllerType(0, controllerTypeToInt(newType))
        
        // 重新连接以确保配置生效
        if (controllerId != -1) {
            disconnect()
            connect()
        }
    }

    // 新增方法：将ControllerType转换为整数
    private fun controllerTypeToInt(controllerType: ControllerType): Int {
        return when (controllerType) {
            ControllerType.PRO_CONTROLLER -> 0
            ControllerType.JOYCON_LEFT -> 1
            ControllerType.JOYCON_RIGHT -> 2
            ControllerType.JOYCON_PAIR -> 3
            ControllerType.HANDHELD -> 4
        }
    }

    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible

            if (isVisible)
                connect()
        }
    }

    fun connect() {
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
            // 连接后立即设置控制器类型
            updateControllerTypeFromSettings()
        }
    }

    fun disconnect() {
        if (controllerId != -1) {
            controllerId = -1
        }
    }

    private fun handleEvent(ev: Event) {
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
            // 连接后立即设置控制器类型
            updateControllerTypeFromSettings()
        }

        controllerId.apply {
            when (ev) {
                is Event.Button -> {
                    val action = ev.action
                    when (action) {
                        KeyEvent.ACTION_UP -> {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(ev.id, this)
                        }

                        KeyEvent.ACTION_DOWN -> {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(ev.id, this)
                        }
                    }
                }

                is Event.Direction -> {
                    val direction = ev.id

                    when (direction) {
                        GamePadButtonInputId.DpadUp.ordinal -> {
                            if (ev.xAxis > 0) {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.DpadRight.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadLeft.ordinal,
                                    this
                                )
                            } else if (ev.xAxis < 0) {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.DpadLeft.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadRight.ordinal,
                                    this
                                )
                            } else {
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadLeft.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadRight.ordinal,
                                    this
                                )
                            }
                            if (ev.yAxis < 0) {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.DpadUp.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadDown.ordinal,
                                    this
                                )
                            } else if (ev.yAxis > 0) {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.DpadDown.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadUp.ordinal,
                                    this
                                )
                            } else {
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadDown.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadUp.ordinal,
                                    this
                                )
                            }
                        }

                        GamePadButtonInputId.LeftStick.ordinal -> {
                            val setting = QuickSettings(activity)
                            val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                            val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                            RyujinxNative.jnaInstance.inputSetStickAxis(
                                1,
                                x,
                                -y,
                                this
                            )
                        }

                        GamePadButtonInputId.RightStick.ordinal -> {
                            val setting = QuickSettings(activity)
                            val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                            val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                            RyujinxNative.jnaInstance.inputSetStickAxis(
                                2,
                                x,
                                -y,
                                this
                            )
                        }
                    }
                }
            }
        }
    }
    
    // 新增方法：处理物理控制器连接
    fun handlePhysicalControllerConnected(deviceId: String, deviceName: String) {
        val physicalController = Controller(
            id = deviceId,
            name = deviceName,
            controllerType = ControllerType.PRO_CONTROLLER, // 默认类型
            isVirtual = false
        )
        ControllerManager.addController(activity, physicalController)
        
        // 设置物理控制器的类型（设备ID从1开始）
        val deviceIdInt = deviceId.hashCode() % 3 + 1 // 生成1-3的设备ID
        RyujinxNative.jnaInstance.setControllerType(deviceIdInt, 0) // 默认Pro控制器
    }
    
    // 新增方法：根据控制器类型处理输入
    private fun handleInputBasedOnType(controllerId: String, inputEvent: Event) {
        // 这里可以根据控制器类型进行不同的输入处理
        // 例如Joy-Con左/右柄的特殊映射
        val controllerType = ControllerManager.connectedControllers.value?.firstOrNull { it.id == controllerId }?.controllerType
            ?: ControllerType.PRO_CONTROLLER
        
        when (controllerType) {
            ControllerType.JOYCON_LEFT -> {
                // Joy-Con左柄的特殊输入处理
                handleJoyConLeftInput(inputEvent)
            }
            ControllerType.JOYCON_RIGHT -> {
                // Joy-Con右柄的特殊输入处理
                handleJoyConRightInput(inputEvent)
            }
            else -> {
                // 其他控制器的默认处理
            }
        }
    }
    
    // 新增方法：处理Joy-Con左柄输入
    private fun handleJoyConLeftInput(inputEvent: Event) {
        // Joy-Con左柄的特殊输入映射
        // 例如：SL和SR按钮的特殊处理
    }
    
    // 新增方法：处理Joy-Con右柄输入
    private fun handleJoyConRightInput(inputEvent: Event) {
        // Joy-Con右柄的特殊输入映射
        // 例如：SL和SR按钮的特殊处理
    }
}

suspend fun <T> Flow<T>.safeCollect(
    block: suspend (T) -> Unit
) {
    this.catch {}
        .collect {
            block(it)
        }
}

private fun generateConfig(isLeft: Boolean): GamePadConfig {
    val distance = 0.3f
    val buttonScale = 1f

    if (isLeft) {
        return GamePadConfig(
            12,
            PrimaryDialConfig.Stick(
                GamePadButtonInputId.LeftStick.ordinal,
                GamePadButtonInputId.LeftStickButton.ordinal,
                setOf(),
                "LeftStick",
                null
            ),
            listOf(
                SecondaryDialConfig.Cross(
                    10,
                    3,
                    2.5f,
                    distance,
                    CrossConfig(
                        GamePadButtonInputId.DpadUp.ordinal,
                        CrossConfig.Shape.STANDARD,
                        null,
                        setOf(),
                        CrossContentDescription(),
                        true,
                        null
                    ),
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.SingleButton(
                    1,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.Minus.ordinal,
                        "-",
                        true,
                        null,
                        "Minus",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.DoubleButton(
                    2,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.LeftShoulder.ordinal,
                        "L",
                        true,
                        null,
                        "LeftBumper",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.SingleButton(
                    9,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.LeftTrigger.ordinal,
                        "ZL",
                        true,
                        null,
                        "LeftTrigger",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
            )
        )
    } else {
        return GamePadConfig(
            12,
            PrimaryDialConfig.PrimaryButtons(
                listOf(
                    ButtonConfig(
                        GamePadButtonInputId.A.ordinal,
                        "A",
                        true,
                        null,
                        "A",
                        setOf(),
                        true,
                        null
                    ),
                    ButtonConfig(
                        GamePadButtonInputId.X.ordinal,
                        "X",
                        true,
                        null,
                        "X",
                        setOf(),
                        true,
                        null
                    ),
                    ButtonConfig(
                        GamePadButtonInputId.Y.ordinal,
                        "Y",
                        true,
                        null,
                        "Y",
                        setOf(),
                        true,
                        null
                    ),
                    ButtonConfig(
                        GamePadButtonInputId.B.ordinal,
                        "B",
                        true,
                        null,
                        "B",
                        setOf(),
                        true,
                        null
                    )
                ),
                null,
                0f,
                true,
                null
            ),
            listOf(
                SecondaryDialConfig.Stick(
                    7,
                    2,
                    2f,
                    distance,
                    GamePadButtonInputId.RightStick.ordinal,
                    GamePadButtonInputId.RightStickButton.ordinal,
                    null,
                    setOf(),
                    "RightStick",
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.SingleButton(
                    6,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.Plus.ordinal,
                        "+",
                        true,
                        null,
                        "Plus",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.DoubleButton(
                    3,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.RightShoulder.ordinal,
                        "R",
                        true,
                        null,
                        "RightBumper",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.SingleButton(
                    9,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.RightTrigger.ordinal,
                        "ZR",
                        true,
                        null,
                        "RightTrigger",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                )
            )
        )
    }
}
