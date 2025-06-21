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

// 添加缺失的按钮ID定义
object GamepadButtonInputId {
    const val A = 0
    const val B = 1
    const val X = 2
    const val Y = 3
    const val LeftStick = 4
    const val RightStick = 5
    const val LeftShoulder = 6
    const val RightShoulder = 7
    const val LeftTrigger = 8
    const val RightTrigger = 9
    const val DpadUp = 10
    const val DpadDown = 11
    const val DpadLeft = 12
    const val DpadRight = 13
    const val Minus = 14
    const val Plus = 15
    const val Start = 16
    const val Back = 17
    const val Guide = 18
    const val Misc = 19
    const val Paddle1 = 20
    const val Paddle2 = 21
    const val Paddle3 = 22
    const val Paddle4 = 23
    const val TouchPad = 24
    const val Count = 25
}

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

    // 按钮映射表
    private val buttonMapping = mapOf(
        GamePadButtonInputId.A.ordinal to GamepadButtonInputId.A,
        GamePadButtonInputId.B.ordinal to GamepadButtonInputId.B,
        GamePadButtonInputId.X.ordinal to GamepadButtonInputId.X,
        GamePadButtonInputId.Y.ordinal to GamepadButtonInputId.Y,
        GamePadButtonInputId.LeftShoulder.ordinal to GamepadButtonInputId.LeftShoulder,
        GamePadButtonInputId.RightShoulder.ordinal to GamepadButtonInputId.RightShoulder,
        GamePadButtonInputId.LeftTrigger.ordinal to GamepadButtonInputId.LeftTrigger,
        GamePadButtonInputId.RightTrigger.ordinal to GamepadButtonInputId.RightTrigger,
        GamePadButtonInputId.Minus.ordinal to GamepadButtonInputId.Minus,
        GamePadButtonInputId.Plus.ordinal to GamepadButtonInputId.Plus,
        GamePadButtonInputId.DpadUp.ordinal to GamepadButtonInputId.DpadUp,
        GamePadButtonInputId.DpadDown.ordinal to GamepadButtonInputId.DpadDown,
        GamePadButtonInputId.DpadLeft.ordinal to GamepadButtonInputId.DpadLeft,
        GamePadButtonInputId.DpadRight.ordinal to GamepadButtonInputId.DpadRight
    )

    init {
        leftGamePad = GamePad(generateConfig(true), 16f, activity)
        rightGamePad = GamePad(generateConfig(false), 16f, activity)

        leftGamePad.primaryDialMaxSizeDp = 200f
        rightGamePad.primaryDialMaxSizeDp = 200f

        leftGamePad.gravityX = -1f
        leftGamePad.gravityY = 1f
        rightGamePad.gravityX = 1f
        rightGamePad.gravityY = 1f
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
        }
    }

    private fun handleEvent(ev: Event) {
        // 使用固定设备ID=0
        val deviceId = 0
        
        if (controllerId == -1) {
            connect()
        }

        when (ev) {
            is Event.Button -> {
                // 使用映射表转换按钮ID
                val mappedId = buttonMapping[ev.id] ?: ev.id
                when (ev.action) {
                    KeyEvent.ACTION_UP -> {
                        RyujinxNative.jnaInstance.inputSetButtonReleased(mappedId, deviceId)
                    }
                    KeyEvent.ACTION_DOWN -> {
                        RyujinxNative.jnaInstance.inputSetButtonPressed(mappedId, deviceId)
                    }
                }
            }
            is Event.Direction -> {
                val direction = ev.id

                when (direction) {
                    GamePadButtonInputId.DpadUp.ordinal -> {
                        if (ev.xAxis > 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamepadButtonInputId.DpadRight,
                                deviceId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamepadButtonInputId.DpadLeft,
                                deviceId
                            )
                        } else if (ev.xAxis < 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamepadButtonInputId.DpadLeft,
                                deviceId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamepadButtonInputId.DpadRight,
                                deviceId
                            )
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamepadButtonInputId.DpadLeft,
                                deviceId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamepadButtonInputId.DpadRight,
                                deviceId
                            )
                        }
                        if (ev.yAxis < 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamepadButtonInputId.DpadUp,
                                deviceId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamepadButtonInputId.DpadDown,
                                deviceId
                            )
                        } else if (ev.yAxis > 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamepadButtonInputId.DpadDown,
                                deviceId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamepadButtonInputId.DpadUp,
                                deviceId
                            )
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamepadButtonInputId.DpadDown,
                                deviceId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamepadButtonInputId.DpadUp,
                                deviceId
                            )
                        }
                    }
                    GamePadButtonInputId.LeftStick.ordinal -> {
                        val setting = QuickSettings(activity)
                        val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                        val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                        RyujinxNative.jnaInstance.inputSetStickAxis(
                            GamepadButtonInputId.LeftStick,
                            x,
                            -y,
                            deviceId
                        )
                    }
                    GamePadButtonInputId.RightStick.ordinal -> {
                        val setting = QuickSettings(activity)
                        val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                        val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                        RyujinxNative.jnaInstance.inputSetStickAxis(
                            GamepadButtonInputId.RightStick,
                            x,
                            -y,
                            deviceId
                        )
                    }
                }
            }
        }
    }
    
    // 添加陀螺仪处理方法
    fun handleGyroData(x: Float, y: Float, z: Float) {
        if (controllerId == -1) return
        RyujinxNative.jnaInstance.inputSetGyroData(x, y, z, 0)
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
