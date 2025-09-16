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
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

// 自定义双圆形按钮视图
class DoubleCircleButtonView @JvmOverloads constructor(
    context: Context, 
    val buttonText: String, 
    val buttonId: Int,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    private val outerCirclePaint = Paint().apply {
        color = Color.argb(128, 255, 255, 255)
        style = Paint.Style.STROKE
        strokeWidth = 4f
        isAntiAlias = true
    }
    
    private val innerCirclePaint = Paint().apply {
        color = Color.argb(64, 200, 200, 200)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val pressedInnerCirclePaint = Paint().apply {
        color = Color.argb(128, 150, 150, 150)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val textPaint = Paint().apply {
        color = Color.WHITE
        textSize = 20f
        textAlign = Paint.Align.CENTER
        typeface = Typeface.DEFAULT_BOLD
        isAntiAlias = true
    }
    
    private var isPressed = false
    
    fun setPressedState(pressed: Boolean) {
        isPressed = pressed
        if (pressed) {
            val scaleAnimation = ScaleAnimation(
                1.0f, 0.8f, 1.0f, 0.8f,
                Animation.RELATIVE_TO_SELF, 0.5f,
                Animation.RELATIVE_TO_SELF, 0.5f
            )
            scaleAnimation.duration = 100
            scaleAnimation.fillAfter = true
            startAnimation(scaleAnimation)
        } else {
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
        
        canvas.drawCircle(centerX, centerY, outerRadius, outerCirclePaint)
        canvas.drawCircle(centerX, centerY, innerRadius, 
            if (isPressed) pressedInnerCirclePaint else innerCirclePaint)
        
        val textY = centerY - (textPaint.descent() + textPaint.ascent()) / 2
        canvas.drawText(buttonText, centerX, textY, textPaint)
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

// 自定义十字方向键视图
class DPadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    private val paint = Paint().apply {
        color = Color.argb(128, 255, 255, 255)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val pressedPaint = Paint().apply {
        color = Color.argb(180, 200, 200, 200)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val centerPaint = Paint().apply {
        color = Color.argb(180, 100, 100, 100)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private var currentDirection = Direction.NONE
    
    enum class Direction {
        NONE, UP, DOWN, LEFT, RIGHT, UP_LEFT, UP_RIGHT, DOWN_LEFT, DOWN_RIGHT
    }
    
    var onDirectionChanged: ((Direction) -> Unit)? = null
    
    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                val x = event.x
                val y = event.y
                val centerX = width / 2f
                val centerY = height / 2f
                
                val dx = x - centerX
                val dy = y - centerY
                val distance = sqrt(dx * dx + dy * dy)
                
                if (distance > width / 6f) {
                    val angle = Math.toDegrees(atan2(dy.toDouble(), dx.toDouble())).toFloat()
                    val newDirection = calculateDirection(angle)
                    if (newDirection != currentDirection) {
                        currentDirection = newDirection
                        onDirectionChanged?.invoke(currentDirection)
                    }
                } else {
                    if (currentDirection != Direction.NONE) {
                        currentDirection = Direction.NONE
                        onDirectionChanged?.invoke(Direction.NONE)
                    }
                }
                invalidate()
                return true
            }
            MotionEvent.ACTION_UP -> {
                if (currentDirection != Direction.NONE) {
                    currentDirection = Direction.NONE
                    onDirectionChanged?.invoke(Direction.NONE)
                    invalidate()
                }
                return true
            }
        }
        return super.onTouchEvent(event)
    }
    
    private fun calculateDirection(angle: Float): Direction {
        return when {
            angle in -22.5f..22.5f -> Direction.RIGHT
            angle in 22.5f..67.5f -> Direction.DOWN_RIGHT
            angle in 67.5f..112.5f -> Direction.DOWN
            angle in 112.5f..157.5f -> Direction.DOWN_LEFT
            angle in 157.5f..180f || angle in -180f..-157.5f -> Direction.LEFT
            angle in -157.5f..-112.5f -> Direction.UP_LEFT
            angle in -112.5f..-67.5f -> Direction.UP
            angle in -67.5f..-22.5f -> Direction.UP_RIGHT
            else -> Direction.NONE
        }
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val radius = width / 3f
        val armLength = width / 3f
        val armWidth = width / 4f
        
        // Draw center circle
        canvas.drawCircle(centerX, centerY, radius / 2, centerPaint)
        
        // Draw arms based on current direction
        when (currentDirection) {
            Direction.UP -> {
                canvas.drawRect(centerX - armWidth / 2, centerY - armLength, 
                               centerX + armWidth / 2, centerY, pressedPaint)
            }
            Direction.DOWN -> {
                canvas.drawRect(centerX - armWidth / 2, centerY, 
                               centerX + armWidth / 2, centerY + armLength, pressedPaint)
            }
            Direction.LEFT -> {
                canvas.drawRect(centerX - armLength, centerY - armWidth / 2, 
                               centerX, centerY + armWidth / 2, pressedPaint)
            }
            Direction.RIGHT -> {
                canvas.drawRect(centerX, centerY - armWidth / 2, 
                               centerX + armLength, centerY + armWidth / 2, pressedPaint)
            }
            Direction.UP_LEFT -> {
                canvas.drawRect(centerX - armLength, centerY - armWidth / 2, 
                               centerX, centerY + armWidth / 2, pressedPaint)
                canvas.drawRect(centerX - armWidth / 2, centerY - armLength, 
                               centerX + armWidth / 2, centerY, pressedPaint)
            }
            Direction.UP_RIGHT -> {
                canvas.drawRect(centerX, centerY - armWidth / 2, 
                               centerX + armLength, centerY + armWidth / 2, pressedPaint)
                canvas.drawRect(centerX - armWidth / 2, centerY - armLength, 
                               centerX + armWidth / 2, centerY, pressedPaint)
            }
            Direction.DOWN_LEFT -> {
                canvas.drawRect(centerX - armLength, centerY - armWidth / 2, 
                               centerX, centerY + armWidth / 2, pressedPaint)
                canvas.drawRect(centerX - armWidth / 2, centerY, 
                               centerX + armWidth / 2, centerY + armLength, pressedPaint)
            }
            Direction.DOWN_RIGHT -> {
                canvas.drawRect(centerX, centerY - armWidth / 2, 
                               centerX + armLength, centerY + armWidth / 2, pressedPaint)
                canvas.drawRect(centerX - armWidth / 2, centerY, 
                               centerX + armWidth / 2, centerY + armLength, pressedPaint)
            }
            else -> {
                // Draw all arms in default state
                canvas.drawRect(centerX - armWidth / 2, centerY - armLength, 
                               centerX + armWidth / 2, centerY, paint)
                canvas.drawRect(centerX - armWidth / 2, centerY, 
                               centerX + armWidth / 2, centerY + armLength, paint)
                canvas.drawRect(centerX - armLength, centerY - armWidth / 2, 
                               centerX, centerY + armWidth / 2, paint)
                canvas.drawRect(centerX, centerY - armWidth / 2, 
                               centerX + armWidth / 2, centerY + armWidth / 2, paint)
            }
        }
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(150)
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
        color = Color.argb(180, 200, 200, 200)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private var stickX = 0f
    private var stickY = 0f
    private var baseRadius = 0f
    private var stickRadius = 0f
    private var centerX = 0f
    private var centerY = 0f
    
    var onPositionChanged: ((Float, Float) -> Unit)? = null
    
    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        centerX = w / 2f
        centerY = h / 2f
        baseRadius = (w.coerceAtMost(h) / 2f) * 0.8f
        stickRadius = baseRadius * 0.4f
        stickX = centerX
        stickY = centerY
    }
    
    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                val x = event.x
                val y = event.y
                
                val dx = x - centerX
                val dy = y - centerY
                val distance = sqrt(dx * dx + dy * dy)
                
                if (distance <= baseRadius) {
                    stickX = x
                    stickY = y
                } else {
                    val angle = atan2(dy, dx)
                    stickX = centerX + cos(angle) * baseRadius
                    stickY = centerY + sin(angle) * baseRadius
                }
                
                val normalizedX = (stickX - centerX) / baseRadius
                val normalizedY = (stickY - centerY) / baseRadius
                
                onPositionChanged?.invoke(normalizedX, normalizedY)
                invalidate()
                return true
            }
            MotionEvent.ACTION_UP -> {
                stickX = centerX
                stickY = centerY
                onPositionChanged?.invoke(0f, 0f)
                invalidate()
                return true
            }
        }
        return super.onTouchEvent(event)
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        canvas.drawCircle(centerX, centerY, baseRadius, basePaint)
        canvas.drawCircle(stickX, stickY, stickRadius, stickPaint)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(150)
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

// 自定义ABXY按钮组（菱形排列，类似Switch布局）
class ABXYButtonsView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    private val buttonPaint = Paint().apply {
        color = Color.argb(180, 100, 100, 100)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val pressedButtonPaint = Paint().apply {
        color = Color.argb(220, 150, 150, 150)
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
    
    // 菱形排列的按钮位置（与圆形排列不同）
    private val buttons = listOf(
        ButtonInfo("A", GamePadButtonInputId.A.ordinal, 0f, 1f, Color.argb(180, 0, 180, 0)), // 下
        ButtonInfo("B", GamePadButtonInputId.B.ordinal, 1f, 0f, Color.argb(180, 200, 0, 0)), // 右
        ButtonInfo("X", GamePadButtonInputId.X.ordinal, -1f, 0f, Color.argb(180, 0, 0, 200)), // 左
        ButtonInfo("Y", GamePadButtonInputId.Y.ordinal, 0f, -1f, Color.argb(180, 220, 220, 0)) // 上
    )
    
    private var centerX = 0f
    private var centerY = 0f
    private var radius = 0f
    private var buttonRadius = 0f
    private var pressedButton: ButtonInfo? = null
    
    private data class ButtonInfo(
        val text: String, 
        val id: Int, 
        val xMultiplier: Float, 
        val yMultiplier: Float,
        val color: Int
    )
    
    var onButtonPressed: ((Int) -> Unit)? = null
    var onButtonReleased: ((Int) -> Unit)? = null
    
    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        centerX = w / 2f
        centerY = h / 2f
        radius = (w.coerceAtMost(h) / 2f) * 0.7f
        buttonRadius = radius * 0.3f
    }
    
    override fun onTouchEvent(event: MotionEvent): Boolean {
        val x = event.x
        val y = event.y
        
        when (event.action) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                var hitButton: ButtonInfo? = null
                
                for (button in buttons) {
                    val buttonX = centerX + button.xMultiplier * radius
                    val buttonY = centerY + button.yMultiplier * radius
                    val dx = x - buttonX
                    val dy = y - buttonY
                    val distance = sqrt(dx * dx + dy * dy)
                    
                    if (distance <= buttonRadius) {
                        hitButton = button
                        break
                    }
                }
                
                if (hitButton != pressedButton) {
                    pressedButton?.let {
                        onButtonReleased?.invoke(it.id)
                    }
                    pressedButton = hitButton
                    hitButton?.let {
                        onButtonPressed?.invoke(it.id)
                    }
                    invalidate()
                }
                return true
            }
            MotionEvent.ACTION_UP -> {
                pressedButton?.let {
                    onButtonReleased?.invoke(it.id)
                    pressedButton = null
                    invalidate()
                }
                return true
            }
        }
        return super.onTouchEvent(event)
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        for (button in buttons) {
            val buttonX = centerX + button.xMultiplier * radius
            val buttonY = centerY + button.yMultiplier * radius
            
            val isPressed = button == pressedButton
            val paint = if (isPressed) pressedButtonPaint else buttonPaint
            paint.color = button.color
            
            canvas.drawCircle(buttonX, buttonY, buttonRadius, paint)
            
            val textY = buttonY - (textPaint.descent() + textPaint.ascent()) / 2
            canvas.drawText(button.text, buttonX, textY, textPaint)
        }
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(150)
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
            
            // 添加左侧摇杆 - 位置调整到左上角
            val leftStick = JoystickView(context).apply {
                onPositionChanged = { x, y ->
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
            
            val leftStickParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = dpToPx(context, 30)
                leftMargin = dpToPx(context, 30)
            }
            leftContainer.addView(leftStick, leftStickParams)
            controller.leftStick = leftStick
            
            // 添加右侧摇杆 - 位置调整到右上角
            val rightStick = JoystickView(context).apply {
                onPositionChanged = { x, y ->
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
            
            val rightStickParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = dpToPx(context, 30)
                rightMargin = dpToPx(context, 30)
            }
            rightContainer.addView(rightStick, rightStickParams)
            controller.rightStick = rightStick
            
            // 添加十字方向键 - 位置调整到左下角
            val dPad = DPadView(context).apply {
                onDirectionChanged = { direction ->
                    when (direction) {
                        DPadView.Direction.UP -> {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                        }
                        DPadView.Direction.DOWN -> {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                        }
                        DPadView.Direction.LEFT -> {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                        }
                        DPadView.Direction.RRIGHT -> {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                        }
                        DPadView.Direction.UP_LEFT -> {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                        }
                        DPadView.Direction.UP_RIGHT -> {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                        }
                        DPadView.Direction.DOWN_LEFT -> {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                        }
                        DPadView.Direction.DOWN_RIGHT -> {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
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
            
            val dPadParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.START
                bottomMargin = dpToPx(context, 30)
                leftMargin = dpToPx(context, 30)
            }
            leftContainer.addView(dPad, dPadParams)
            controller.dPad = dPad
            
            // 添加ABXY按钮 - 位置调整到右下角
            val abxyButtons = ABXYButtonsView(context).apply {
                onButtonPressed = { buttonId ->
                    RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                }
                onButtonReleased = { buttonId ->
                    RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                }
            }
            
            val abxyParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = dpToPx(context, 30)
                rightMargin = dpToPx(context, 30)
            }
            rightContainer.addView(abxyButtons, abxyParams)
            controller.abxyButtons = abxyButtons
            
            // 添加L按钮 - 位置调整到左上角
            val lButton = DoubleCircleButtonView(context, "L", GamePadButtonInputId.LeftShoulder.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.LeftShoulder.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.LeftShoulder.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val lButtonParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = dpToPx(context, 30)
                leftMargin = dpToPx(context, 150)
            }
            leftContainer.addView(lButton, lButtonParams)
            controller.lButton = lButton
            
            // 添加R按钮 - 位置调整到右上角
            val rButton = DoubleCircleButtonView(context, "R", GamePadButtonInputId.RightShoulder.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.RightShoulder.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.RightShoulder.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val rButtonParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = dpToPx(context, 30)
                rightMargin = dpToPx(context, 150)
            }
            rightContainer.addView(rButton, rButtonParams)
            controller.rButton = rButton
            
            // 添加ZL按钮 - 位置调整到左上角，L按钮下方
            val zlButton = DoubleCircleButtonView(context, "ZL", GamePadButtonInputId.LeftTrigger.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.LeftTrigger.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.LeftTrigger.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val zlButtonParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = dpToPx(context, 100)
                leftMargin = dpToPx(context, 150)
            }
            leftContainer.addView(zlButton, zlButtonParams)
            controller.zlButton = zlButton
            
            // 添加ZR按钮 - 位置调整到右上角，R按钮下方
            val zrButton = DoubleCircleButtonView(context, "ZR", GamePadButtonInputId.RightTrigger.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.RightTrigger.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.RightTrigger.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val zrButtonParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = dpToPx(context, 100)
                rightMargin = dpToPx(context, 150)
            }
            rightContainer.addView(zrButton, zrButtonParams)
            controller.zrButton = zrButton
            
            // 添加Minus按钮 - 位置调整到左侧中间
            val minusButton = DoubleCircleButtonView(context, "-", GamePadButtonInputId.Minus.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.Minus.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.Minus.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val minusButtonParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.CENTER or android.view.Gravity.START
                leftMargin = dpToPx(context, 30)
            }
            leftContainer.addView(minusButton, minusButtonParams)
            controller.minusButton = minusButton
            
            // 添加Plus按钮 - 位置调整到右侧中间
            val plusButton = DoubleCircleButtonView(context, "+", GamePadButtonInputId.Plus.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.Plus.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.Plus.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val plusButtonParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.CENTER or android.view.Gravity.END
                rightMargin = dpToPx(context, 30)
            }
            rightContainer.addView(plusButton, plusButtonParams)
            controller.plusButton = plusButton
            
            // 添加L3按钮 - 位置调整到左侧摇杆下方
            val l3Button = DoubleCircleButtonView(context, "L3", GamePadButtonInputId.LeftStickButton.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.LeftStickButton.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
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
            
            val l3ButtonParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = dpToPx(context, 180)
                leftMargin = dpToPx(context, 80)
            }
            leftContainer.addView(l3Button, l3ButtonParams)
            controller.l3Button = l3Button
            
            // 添加R3按钮 - 位置调整到右侧摇杆下方
            val r3Button = DoubleCircleButtonView(context, "R3", GamePadButtonInputId.RightStickButton.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.RightStickButton.ordinal,
                                controller.controllerId
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
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
            
            val r3ButtonParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = dpToPx(context, 180)
                rightMargin = dpToPx(context, 80)
            }
            rightContainer.addView(r3Button, r3ButtonParams)
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
    var abxyButtons: ABXYButtonsView? = null
    var lButton: DoubleCircleButtonView? = null
    var rButton: DoubleCircleButtonView? = null
    var zlButton: DoubleCircleButtonView? = null
    var zrButton: DoubleCircleButtonView? = null
    var minusButton: DoubleCircleButtonView? = null
    var plusButton: DoubleCircleButtonView? = null
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
        // 初始化时连接游戏手柄
        connect()
    }

    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible
            leftStick?.isVisible = isVisible
            rightStick?.isVisible = isVisible
            dPad?.isVisible = isVisible
            abxyButtons?.isVisible = isVisible
            lButton?.isVisible = isVisible
            rButton?.isVisible = isVisible
            zlButton?.isVisible = isVisible
            zrButton?.isVisible = isVisible
            minusButton?.isVisible = isVisible
            plusButton?.isVisible = isVisible
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
}

suspend fun <T> Flow<T>.safeCollect(
    block: suspend (T) -> Unit
) {
    this.catch {}
        .collect {
            block(it)
        }
}
