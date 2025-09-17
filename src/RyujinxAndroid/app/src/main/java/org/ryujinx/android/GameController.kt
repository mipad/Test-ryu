package org.ryujinx.android

import android.app.Activity
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.util.AttributeSet
import android.util.TypedValue
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.MotionEvent
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
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.merge
import kotlinx.coroutines.flow.shareIn
import kotlinx.coroutines.launch
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings

// 自定义方向键视图（整体十字键）
class DPadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    private val paint = Paint().apply {
        color = Color.argb(128, 255, 255, 255)
        style = Paint.Style.FILL_AND_STROKE
        strokeWidth = 4f
        isAntiAlias = true
    }

    private var currentDirection = Direction.NONE

    enum class Direction {
        NONE, UP, DOWN, LEFT, RIGHT, UP_LEFT, UP_RIGHT, DOWN_LEFT, DOWN_RIGHT
    }

    var onDirectionChanged: ((Direction) -> Unit)? = null

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val size = width.coerceAtMost(height) * 0.8f
        val armWidth = size * 0.3f
        val armLength = size * 0.4f

        // 绘制中心圆
        canvas.drawCircle(centerX, centerY, armWidth / 2, paint)

        // 绘制四个方向臂
        // 上
        canvas.drawRect(
            centerX - armWidth / 2,
            centerY - armLength - armWidth / 2,
            centerX + armWidth / 2,
            centerY - armWidth / 2,
            paint
        )
        
        // 下
        canvas.drawRect(
            centerX - armWidth / 2,
            centerY + armWidth / 2,
            centerX + armWidth / 2,
            centerY + armLength + armWidth / 2,
            paint
        )
        
        // 左
        canvas.drawRect(
            centerX - armLength - armWidth / 2,
            centerY - armWidth / 2,
            centerX - armWidth / 2,
            centerY + armWidth / 2,
            paint
        )
        
        // 右
        canvas.drawRect(
            centerX + armWidth / 2,
            centerY - armWidth / 2,
            centerX + armLength + armWidth / 2,
            centerY + armWidth / 2,
            paint
        )
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                val x = event.x
                val y = event.y
                val centerX = width / 2f
                val centerY = height / 2f
                
                val direction = calculateDirection(x, y, centerX, centerY)
                if (direction != currentDirection) {
                    currentDirection = direction
                    onDirectionChanged?.invoke(direction)
                }
                return true
            }
            MotionEvent.ACTION_UP -> {
                currentDirection = Direction.NONE
                onDirectionChanged?.invoke(Direction.NONE)
                return true
            }
        }
        return super.onTouchEvent(event)
    }

    private fun calculateDirection(x: Float, y: Float, centerX: Float, centerY: Float): Direction {
        val dx = x - centerX
        val dy = y - centerY
        val distance = Math.sqrt((dx * dx + dy * dy).toDouble()).toFloat()
        
        if (distance < width * 0.1f) return Direction.NONE
        
        val angle = Math.toDegrees(Math.atan2(dy.toDouble(), dx.toDouble())).toFloat()
        
        return when {
            angle in -45f..45f -> Direction.RIGHT
            angle in 45f..135f -> Direction.DOWN
            angle in 135f..180f || angle in -180f..-135f -> Direction.LEFT
            angle in -135f..-45f -> Direction.UP
            else -> Direction.NONE
        }
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(120)
        setMeasuredDimension(size, size)
    }

    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
}

// 自定义按钮视图
class GameButtonView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
    val buttonText: String = "",
    val buttonId: Int = -1
) : View(context, attrs, defStyleAttr) {

    private val circlePaint = Paint().apply {
        color = Color.argb(128, 255, 255, 255)
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
    
    var onButtonStateChanged: ((Boolean) -> Unit)? = null

    fun setPressedState(pressed: Boolean) {
        isPressed = pressed
        if (pressed) {
            circlePaint.color = Color.argb(192, 200, 200, 200)
            val scaleAnimation = ScaleAnimation(
                1.0f, 0.8f, 1.0f, 0.8f,
                Animation.RELATIVE_TO_SELF, 0.5f,
                Animation.RELATIVE_TO_SELF, 0.5f
            )
            scaleAnimation.duration = 100
            scaleAnimation.fillAfter = true
            startAnimation(scaleAnimation)
        } else {
            circlePaint.color = Color.argb(128, 255, 255, 255)
            val scaleAnimation = ScaleAnimation(
                0.8f, 1.0f, 0.8f, 1.0f,
                Animation.RELATIVE_TO_SELF, 0.5f,
                Animation.RELATIVE_TO_SELF, 0.5f
            )
            scaleAnimation.duration = 100
            scaleAnimation.fillAfter = true
            startAnimation(scaleAnimation)
        }
        onButtonStateChanged?.invoke(pressed)
        invalidate()
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val radius = (width.coerceAtMost(height) / 2f) * 0.8f
        
        canvas.drawCircle(centerX, centerY, radius, circlePaint)
        
        val textY = centerY - (textPaint.descent() + textPaint.ascent()) / 2
        canvas.drawText(buttonText, centerX, textY, textPaint)
    }
    
    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {
                setPressedState(true)
                return true
            }
            MotionEvent.ACTION_UP -> {
                setPressedState(false)
                return true
            }
        }
        return super.onTouchEvent(event)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(70)
        setMeasuredDimension(size, size)
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
}

// 自定义摇杆视图
class JoystickView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    private val basePaint = Paint().apply {
        color = Color.argb(128, 100, 100, 100)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val stickPaint = Paint().apply {
        color = Color.argb(192, 255, 255, 255)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private var stickX = 0f
    private var stickY = 0f
    private var baseRadius = 0f
    private var stickRadius = 0f
    
    var onStickMoved: ((Float, Float) -> Unit)? = null

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        baseRadius = (w.coerceAtMost(h) / 2f) * 0.8f
        stickRadius = baseRadius * 0.5f
        stickX = w / 2f
        stickY = h / 2f
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        
        // 绘制底座
        canvas.drawCircle(centerX, centerY, baseRadius, basePaint)
        
        // 绘制摇杆
        canvas.drawCircle(stickX, stickY, stickRadius, stickPaint)
    }
    
    override fun onTouchEvent(event: MotionEvent): Boolean {
        val centerX = width / 2f
        val centerY = height / 2f
        
        when (event.action) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                val x = event.x
                val y = event.y
                
                // 计算距离和角度
                val dx = x - centerX
                val dy = y - centerY
                val distance = Math.sqrt((dx * dx + dy * dy).toDouble()).toFloat()
                
                if (distance <= baseRadius) {
                    stickX = x
                    stickY = y
                } else {
                    val angle = Math.atan2(dy.toDouble(), dx.toDouble())
                    stickX = centerX + (baseRadius * Math.cos(angle)).toFloat()
                    stickY = centerY + (baseRadius * Math.sin(angle)).toFloat()
                }
                
                // 计算归一化的坐标值
                val normalizedX = (stickX - centerX) / baseRadius
                val normalizedY = (stickY - centerY) / baseRadius
                
                onStickMoved?.invoke(normalizedX, normalizedY)
                invalidate()
                return true
            }
            MotionEvent.ACTION_UP -> {
                // 回归中心
                stickX = centerX
                stickY = centerY
                onStickMoved?.invoke(0f, 0f)
                invalidate()
                return true
            }
        }
        return super.onTouchEvent(event)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(120)
        setMeasuredDimension(size, size)
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
}

class GameController(var activity: Activity) {

    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            val leftContainer = view.findViewById<FrameLayout>(R.id.leftcontainer)!!
            val rightContainer = view.findViewById<FrameLayout>(R.id.rightcontainer)!!

            // 创建左侧控件
            val leftStick = JoystickView(context).apply {
                onStickMoved = { x, y ->
                    if (controller.controllerId != -1) {
                        val setting = QuickSettings(controller.activity)
                        val clampedX = MathUtils.clamp(x * setting.controllerStickSensitivity, -1f, 1f)
                        val clampedY = MathUtils.clamp(y * setting.controllerStickSensitivity, -1f, 1f)
                        RyujinxNative.jnaInstance.inputSetStickAxis(
                            1,
                            clampedX,
                            -clampedY,
                            controller.controllerId
                        )
                    }
                }
            }

            val dPad = DPadView(context).apply {
                onDirectionChanged = { direction ->
                    if (controller.controllerId != -1) {
                        when (direction) {
                            DPadView.Direction.UP -> {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                            }
                            DPadView.Direction.DOWN -> {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                            }
                            DPadView.Direction.LEFT -> {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                            }
                            DPadView.Direction.RIGHT -> {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            }
                            else -> {
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                            }
                        }
                    }
                }
            }

            val lButton = GameButtonView(context, buttonText = "L", buttonId = GamePadButtonInputId.LeftShoulder.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            val zlButton = GameButtonView(context, buttonText = "ZL", buttonId = GamePadButtonInputId.LeftTrigger.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            val minusButton = GameButtonView(context, buttonText = "-", buttonId = GamePadButtonInputId.Minus.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            // 创建右侧控件
            val rightStick = JoystickView(context).apply {
                onStickMoved = { x, y ->
                    if (controller.controllerId != -1) {
                        val setting = QuickSettings(controller.activity)
                        val clampedX = MathUtils.clamp(x * setting.controllerStickSensitivity, -1f, 1f)
                        val clampedY = MathUtils.clamp(y * setting.controllerStickSensitivity, -1f, 1f)
                        RyujinxNative.jnaInstance.inputSetStickAxis(
                            2,
                            clampedX,
                            -clampedY,
                            controller.controllerId
                        )
                    }
                }
            }

            val aButton = GameButtonView(context, buttonText = "A", buttonId = GamePadButtonInputId.A.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            val bButton = GameButtonView(context, buttonText = "B", buttonId = GamePadButtonInputId.B.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            val xButton = GameButtonView(context, buttonText = "X", buttonId = GamePadButtonInputId.X.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            val yButton = GameButtonView(context, buttonText = "Y", buttonId = GamePadButtonInputId.Y.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            val rButton = GameButtonView(context, buttonText = "R", buttonId = GamePadButtonInputId.RightShoulder.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            val zrButton = GameButtonView(context, buttonText = "ZR", buttonId = GamePadButtonInputId.RightTrigger.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            val plusButton = GameButtonView(context, buttonText = "+", buttonId = GamePadButtonInputId.Plus.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            // L3和R3按钮
            val l3Button = GameButtonView(context, buttonText = "L3", buttonId = GamePadButtonInputId.LeftStickButton.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            val r3Button = GameButtonView(context, buttonText = "R3", buttonId = GamePadButtonInputId.RightStickButton.ordinal).apply {
                onButtonStateChanged = { pressed ->
                    if (controller.controllerId != -1) {
                        if (pressed) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                        }
                    }
                }
            }

            // 设置布局参数并添加到容器
            val layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )

            // 左侧布局
            leftStick.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.START
                leftMargin = dpToPx(context, 30)
                bottomMargin = dpToPx(context, 30)
            }
            leftContainer.addView(leftStick)

            dPad.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                rightMargin = dpToPx(context, 30)
                bottomMargin = dpToPx(context, 30)
            }
            leftContainer.addView(dPad)

            lButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                leftMargin = dpToPx(context, 30)
                topMargin = dpToPx(context, 30)
            }
            leftContainer.addView(lButton)

            zlButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                rightMargin = dpToPx(context, 30)
                topMargin = dpToPx(context, 30)
            }
            leftContainer.addView(zlButton)

            minusButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.CENTER_HORIZONTAL
                topMargin = dpToPx(context, 20)
            }
            leftContainer.addView(minusButton)

            l3Button.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.CENTER
                leftMargin = dpToPx(context, 80)
                topMargin = dpToPx(context, 80)
            }
            leftContainer.addView(l3Button)

            // 右侧布局
            rightStick.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                rightMargin = dpToPx(context, 30)
                bottomMargin = dpToPx(context, 30)
            }
            rightContainer.addView(rightStick)

            aButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.START
                leftMargin = dpToPx(context, 30)
                bottomMargin = dpToPx(context, 30)
            }
            rightContainer.addView(aButton)

            bButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                rightMargin = dpToPx(context, 30)
                bottomMargin = dpToPx(context, 30)
            }
            rightContainer.addView(bButton)

            xButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                leftMargin = dpToPx(context, 30)
                topMargin = dpToPx(context, 30)
            }
            rightContainer.addView(xButton)

            yButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                rightMargin = dpToPx(context, 30)
                topMargin = dpToPx(context, 30)
            }
            rightContainer.addView(yButton)

            rButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                leftMargin = dpToPx(context, 30)
                topMargin = dpToPx(context, 30)
            }
            rightContainer.addView(rButton)

            zrButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                rightMargin = dpToPx(context, 30)
                topMargin = dpToPx(context, 30)
            }
            rightContainer.addView(zrButton)

            plusButton.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.CENTER_HORIZONTAL
                topMargin = dpToPx(context, 20)
            }
            rightContainer.addView(plusButton)

            r3Button.layoutParams = layoutParams.apply {
                gravity = android.view.Gravity.CENTER
                rightMargin = dpToPx(context, 80)
                topMargin = dpToPx(context, 80)
            }
            rightContainer.addView(r3Button)

            // 保存引用
            controller.leftStick = leftStick
            controller.rightStick = rightStick
            controller.dPad = dPad
            controller.aButton = aButton
            controller.bButton = bButton
            controller.xButton = xButton
            controller.yButton = yButton
            controller.lButton = lButton
            controller.rButton = rButton
            controller.zlButton = zlButton
            controller.zrButton = zrButton
            controller.minusButton = minusButton
            controller.plusButton = plusButton
            controller.l3Button = l3Button
            controller.r3Button = r3Button

            return view
        }
        
        private fun dpToPx(context: Context, dp: Int): Int {
            return TypedValue.applyDimension(
                TypedValue.COMPLEX_UNIT_DIP, 
                dp.toFloat(), 
                context.resources.displayMetrics
            ).toInt()
        }

        @Composable
        fun Compose(viewModel: MainViewModel): Unit {
            AndroidView(
                modifier = Modifier.fillMaxSize(), factory = { context ->
                    val controller = GameController(viewModel.activity)
                    val c = Create(context, controller)
                    controller.controllerView = c
                    viewModel.setGameController(controller)
                    controller.setVisible(QuickSettings(viewModel.activity).useVirtualController)
                    c
                })
        }
    }

    private var controllerView: View? = null
    var leftStick: JoystickView? = null
    var rightStick: JoystickView? = null
    var dPad: DPadView? = null
    var aButton: GameButtonView? = null
    var bButton: GameButtonView? = null
    var xButton: GameButtonView? = null
    var yButton: GameButtonView? = null
    var lButton: GameButtonView? = null
    var rButton: GameButtonView? = null
    var zlButton: GameButtonView? = null
    var zrButton: GameButtonView? = null
    var minusButton: GameButtonView? = null
    var plusButton: GameButtonView? = null
    var l3Button: GameButtonView? = null
    var r3Button: GameButtonView? = null
    
    var controllerId: Int = -1
    val isVisible: Boolean
        get() {
            controllerView?.apply {
                return this.isVisible
            }
            return false
        }

    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible
            if (isVisible) connect()
        }
    }

    fun connect() {
        if (controllerId == -1)
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
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
