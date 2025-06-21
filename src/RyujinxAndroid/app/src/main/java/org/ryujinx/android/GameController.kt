package org.ryujinx.android

import android.app.Activity
import android.content.Context
import android.util.Log
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
        Log.d("GameController", "Connecting controllers...")
        
        // 连接两个控制器
        val id0 = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        val id1 = RyujinxNative.jnaInstance.inputConnectGamepad(1)
        
        Log.d("GameController", "Controller 0 connected with ID: $id0")
        Log.d("GameController", "Controller 1 connected with ID: $id1")
        
        controllerId = 1 // 使用Pro控制器接收输入
    }

    private fun handleEvent(ev: Event) {
        Log.d("GameController", "Received event: $ev")
        
        if (controllerId == -1) {
            Log.d("GameController", "Controller not connected, connecting...")
            connect()
        }

        val targetControllerId = 1
        Log.d("GameController", "Handling event on controller $targetControllerId")
        
        when (ev) {
            is Event.Button -> {
                Log.d("GameController", "Button event: id=${ev.id}, action=${ev.action}")
                val action = ev.action
                when (action) {
                    KeyEvent.ACTION_UP -> {
                        RyujinxNative.jnaInstance.inputSetButtonReleased(ev.id, targetControllerId)
                    }

                    KeyEvent.ACTION_DOWN -> {
                        RyujinxNative.jnaInstance.inputSetButtonPressed(ev.id, targetControllerId)
                    }
                }
            }

            is Event.Direction -> {
                Log.d("GameController", "Direction event: id=${ev.id}, x=${ev.xAxis}, y=${ev.yAxis}")
                val direction = ev.id

                when (direction) {
                    GamePadButtonInputId.DpadUp.ordinal -> {
                        if (ev.xAxis > 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadRight.ordinal,
                                targetControllerId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                targetControllerId
                            )
                        } else if (ev.xAxis < 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                targetControllerId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadRight.ordinal,
                                targetControllerId
                            )
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                targetControllerId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadRight.ordinal,
                                targetControllerId
                            )
                        }
                        if (ev.yAxis < 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadUp.ordinal,
                                targetControllerId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadDown.ordinal,
                                targetControllerId
                            )
                        } else if (ev.yAxis > 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadDown.ordinal,
                                targetControllerId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadUp.ordinal,
                                targetControllerId
                            )
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadDown.ordinal,
                                targetControllerId
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadUp.ordinal,
                                targetControllerId
                            )
                        }
                    }

                    GamePadButtonInputId.LeftStick.ordinal -> {
                        Log.d("GameController", "Left stick movement: x=${ev.xAxis}, y=${ev.yAxis}")
                        val setting = QuickSettings(activity)
                        val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                        val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                        
                        // StickInputId.Left 在C#中是0
                        RyujinxNative.jnaInstance.inputSetStickAxis(
                            0,
                            x,
                            -y,
                            targetControllerId
                        )
                    }

                    GamePadButtonInputId.RightStick.ordinal -> {
                        Log.d("GameController", "Right stick movement: x=${ev.xAxis}, y=${ev.yAxis}")
                        val setting = QuickSettings(activity)
                        val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                        val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                        
                        // StickInputId.Right 在C#中是1
                        RyujinxNative.jnaInstance.inputSetStickAxis(
                            1,
                            x,
                            -y,
                            targetControllerId
                        )
                    }
                }
            }
        }
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
