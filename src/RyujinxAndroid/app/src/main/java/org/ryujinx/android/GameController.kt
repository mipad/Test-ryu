package org.ryujinx.android

import android.app.Activity
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.view.animation.Animation
import android.view.animation.ScaleAnimation
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

// 自定义双圆形按钮视图
class DoubleCircleButtonView(context: Context, val buttonText: String, val buttonId: Int) : View(context) {
    private val outerCirclePaint = Paint().apply {
        color = Color.argb(128, 255, 255, 255) // 半透明白色外圈
        style = Paint.Style.STROKE
        strokeWidth = 4f
        isAntiAlias = true
    }
    
    private val innerCirclePaint = Paint().apply {
        color = Color.argb(64, 200, 200, 200) // 更透明的内圈
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val textPaint = Paint().apply {
        color = Color.WHITE
        textSize = 24f
        textAlign = Paint.Align.CENTER
        typeface = Typeface.DEFAULT_BOLD
        isAntiAlias = true
    }
    
    private var isPressed = false
    
    fun setPressedState(pressed: Boolean) {
        isPressed = pressed
        if (pressed) {
            // 按压动画
            val scaleAnimation = ScaleAnimation(
                1.0f, 0.8f, 1.0f, 0.8f,
                Animation.RELATIVE_TO_SELF, 0.5f,
                Animation.RELATIVE_TO_SELF, 0.5f
            )
            scaleAnimation.duration = 100
            scaleAnimation.fillAfter = true
            startAnimation(scaleAnimation)
        } else {
            // 回弹动画
            val scaleAnimation = ScaleAnimation(
                0.8f, 1.0f, 0.8f, 1.0f,
                Animation.RELATIVE_TO_SELF, 0.5f,
                Animation.RELATIVE_TO_SELF, 0.5f
            )
            scaleAnimation.duration = 100
            scaleAnimation.fillAfter = true
            startAnimation(scaleAnimation)
        }
        invalidate()
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val outerRadius = (width.coerceAtMost(height) / 2f) * 0.9f
        val innerRadius = outerRadius * 0.6f
        
        // 绘制外圈
        canvas.drawCircle(centerX, centerY, outerRadius, outerCirclePaint)
        
        // 绘制内圈
        canvas.drawCircle(centerX, centerY, innerRadius, innerCirclePaint)
        
        // 绘制文字
        val textY = centerY - (textPaint.descent() + textPaint.ascent()) / 2
        canvas.drawText(buttonText, centerX, textY, textPaint)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = 80 // 80dp
        setMeasuredDimension(size, size)
    }
}

class GameController(var activity: Activity) {

    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(controller.leftGamePad)
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(controller.rightGamePad)

            // 添加L3按钮
            val l3Button = DoubleCircleButtonView(context, "L3", GamePadButtonInputId.LeftStickButton.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.LeftStickButton.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.LeftStickButton.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        else -> false
                    }
                }
            }
            
            // 添加R3按钮
            val r3Button = DoubleCircleButtonView(context, "R3", GamePadButtonInputId.RightStickButton.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.RightStickButton.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.RightStickButton.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        else -> false
                    }
                }
            }
            
            // 设置L3和R3按钮的布局参数
            val layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
            
            // 将L3按钮添加到左侧容器 - 放置在L按钮右侧
            layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = 150 // 根据截图调整位置
                leftMargin = 250 // 根据截图调整位置
            }
            l3Button.layoutParams = layoutParams
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(l3Button)
            controller.l3Button = l3Button
            
            // 将R3按钮添加到右侧容器 - 放置在R按钮左侧
            layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = 150 // 根据截图调整位置
                rightMargin = 250 // 根据截图调整位置
            }
            r3Button.layoutParams = layoutParams
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(r3Button)
            controller.r3Button = r3Button

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
    var l3Button: DoubleCircleButtonView? = null
    var r3Button: DoubleCircleButtonView? = null
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
            l3Button?.isVisible = isVisible
            r3Button?.isVisible = isVisible

            if (isVisible)
                connect()
        }
    }

    fun connect() {
        if (controllerId == -1)
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
    }

    private fun handleEvent(ev: Event) {
        if (controllerId == -1)
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)

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
