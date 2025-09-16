package org.ryujinx.android

import android.app.Activity
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
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
        textSize = 20f // 增大文字尺寸
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

// 可移动按钮视图
class MovableButtonView(context: Context, val buttonText: String, val buttonId: Int, val controller: GameController) : View(context) {
    private val paint = Paint().apply {
        isAntiAlias = true
        color = Color.argb(200, 80, 80, 80)
        style = Paint.Style.FILL
    }
    
    private val textPaint = Paint().apply {
        color = Color.WHITE
        textSize = 24f
        textAlign = Paint.Align.CENTER
        typeface = Typeface.DEFAULT_BOLD
        isAntiAlias = true
    }
    
    private var isPressed = false
    private var startX = 0f
    private var startY = 0f
    private var offsetX = 0f
    private var offsetY = 0f
    
    var currentX = 0f
    var currentY = 0f
    var defaultX = 0f
    var defaultY = 0f
    
    private val radius = 50f
    
    init {
        setOnTouchListener { _, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> {
                    isPressed = true
                    startX = event.rawX
                    startY = event.rawY
                    offsetX = currentX - event.rawX
                    offsetY = currentY - event.rawY
                    invalidate()
                    
                    // 发送按钮按下事件
                    controller.sendButtonEvent(buttonId, true)
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    currentX = event.rawX + offsetX
                    currentY = event.rawY + offsetY
                    invalidate()
                    true
                }
                MotionEvent.ACTION_UP -> {
                    isPressed = false
                    invalidate()
                    
                    // 发送按钮释放事件
                    controller.sendButtonEvent(buttonId, false)
                    true
                }
                else -> false
            }
        }
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        // 绘制按钮
        canvas.drawCircle(currentX, currentY, radius, paint)
        
        // 绘制文字
        val textY = currentY - (textPaint.descent() + textPaint.ascent()) / 2
        canvas.drawText(buttonText, currentX, textY, textPaint)
        
        // 如果按下，添加高亮效果
        if (isPressed) {
            val highlightPaint = Paint().apply {
                color = Color.argb(100, 255, 255, 255)
                style = Paint.Style.FILL
                isAntiAlias = true
            }
            canvas.drawCircle(currentX, currentY, radius, highlightPaint)
        }
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
    
    fun resetPosition() {
        currentX = defaultX
        currentY = defaultY
        invalidate()
    }
    
    fun savePosition() {
        val prefs = context.getSharedPreferences("gamepad_layout", Context.MODE_PRIVATE)
        prefs.edit().apply {
            putFloat("btn_${buttonId}_x", currentX)
            putFloat("btn_${buttonId}_y", currentY)
            apply()
        }
    }
    
    fun loadPosition() {
        val prefs = context.getSharedPreferences("gamepad_layout", Context.MODE_PRIVATE)
        currentX = prefs.getFloat("btn_${buttonId}_x", defaultX)
        currentY = prefs.getFloat("btn_${buttonId}_y", defaultY)
        invalidate()
    }
}

// 可移动的十字方向键
class MovableDpadView(context: Context, val controller: GameController) : View(context) {
    private val basePaint = Paint().apply {
        color = Color.argb(180, 100, 100, 100)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val directionPaint = Paint().apply {
        color = Color.argb(200, 120, 120, 120)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    var baseX = 0f
    var baseY = 0f
    private var baseRadius = 80f
    private var directionRadius = 30f
    
    private var currentDirection = -1 // -1: 无方向, 0: 上, 1: 右, 2: 下, 3: 左
    private var isMovingBase = false
    
    var defaultX = 0f
    var defaultY = 0f
    
    init {
        setOnTouchListener { _, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> {
                    val x = event.x
                    val y = event.y
                    
                    // 检查是否点击了底座（准备移动整个D-pad）
                    val distanceToBase = Math.sqrt(
                        Math.pow((x - baseX).toDouble(), 2.0) + 
                        Math.pow((y - baseY).toDouble(), 2.0)
                    )
                    
                    if (distanceToBase <= baseRadius) {
                        isMovingBase = true
                    } else {
                        // 检查方向
                        val angle = Math.atan2((y - baseY).toDouble(), (x - baseX).toDouble()) * 180 / Math.PI
                        val normalizedAngle = (angle + 360) % 360
                        
                        currentDirection = when {
                            normalizedAngle >= 45 && normalizedAngle < 135 -> 2 // 下
                            normalizedAngle >= 135 && normalizedAngle < 225 -> 3 // 左
                            normalizedAngle >= 225 && normalizedAngle < 315 -> 0 // 上
                            else -> 1 // 右
                        }
                        
                        // 发送方向按下事件
                        sendDirectionEvent(currentDirection, true)
                    }
                    invalidate()
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    if (isMovingBase) {
                        // 移动整个D-pad
                        baseX = event.x
                        baseY = event.y
                    }
                    invalidate()
                    true
                }
                MotionEvent.ACTION_UP -> {
                    if (currentDirection != -1) {
                        // 发送方向释放事件
                        sendDirectionEvent(currentDirection, false)
                        currentDirection = -1
                    }
                    isMovingBase = false
                    invalidate()
                    true
                }
                else -> false
            }
        }
    }
    
    private fun sendDirectionEvent(direction: Int, pressed: Boolean) {
        val buttonId = when (direction) {
            0 -> GamePadButtonInputId.DpadUp.ordinal
            1 -> GamePadButtonInputId.DpadRight.ordinal
            2 -> GamePadButtonInputId.DpadDown.ordinal
            3 -> GamePadButtonInputId.DpadLeft.ordinal
            else -> return
        }
        
        if (pressed) {
            controller.sendButtonEvent(buttonId, true)
        } else {
            controller.sendButtonEvent(buttonId, false)
        }
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        // 绘制底座
        canvas.drawCircle(baseX, baseY, baseRadius, basePaint)
        
        // 绘制方向指示器
        if (currentDirection != -1) {
            val angle = when (currentDirection) {
                0 -> 90f // 上
                1 -> 0f  // 右
                2 -> 270f // 下
                3 -> 180f // 左
                else -> 0f
            }
            
            val rad = Math.toRadians(angle.toDouble())
            val dirX = baseX + (baseRadius * 0.7f * Math.cos(rad)).toFloat()
            val dirY = baseY + (baseRadius * 0.7f * Math.sin(rad)).toFloat()
            
            canvas.drawCircle(dirX, dirY, directionRadius, directionPaint)
        }
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(160) 
        setMeasuredDimension(size, size)
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
    
    fun resetPosition() {
        baseX = defaultX
        baseY = defaultY
        invalidate()
    }
    
    fun savePosition() {
        val prefs = context.getSharedPreferences("gamepad_layout", Context.MODE_PRIVATE)
        prefs.edit().apply {
            putFloat("dpad_x", baseX)
            putFloat("dpad_y", baseY)
            apply()
        }
    }
    
    fun loadPosition() {
        val prefs = context.getSharedPreferences("gamepad_layout", Context.MODE_PRIVATE)
        baseX = prefs.getFloat("dpad_x", defaultX)
        baseY = prefs.getFloat("dpad_y", defaultY)
        invalidate()
    }
}

// 可移动的摇杆
class MovableJoystickView(context: Context, val stickId: Int, val isLeftStick: Boolean, val controller: GameController) : View(context) {
    private val basePaint = Paint().apply {
        color = Color.argb(150, 100, 100, 100)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val stickPaint = Paint().apply {
        color = Color.argb(200, 60, 60, 60)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    var baseX = 0f
    var baseY = 0f
    var stickX = 0f
    var stickY = 0f
    private var baseRadius = 80f
    private var stickRadius = 40f
    
    private var isMovingBase = false
    private var isMovingStick = false
    
    var defaultX = 0f
    var defaultY = 0f
    
    init {
        setOnTouchListener { _, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> {
                    val x = event.x
                    val y = event.y
                    
                    // 检查是否点击了底座（准备移动整个摇杆）
                    val distanceToBase = Math.sqrt(
                        Math.pow((x - baseX).toDouble(), 2.0) + 
                        Math.pow((y - baseY).toDouble(), 2.0)
                    )
                    
                    if (distanceToBase <= baseRadius) {
                        isMovingBase = true
                    } 
                    // 检查是否点击了摇杆（准备操作摇杆）
                    else if (distanceToBase <= baseRadius + stickRadius) {
                        isMovingStick = true
                        updateStickPosition(event.x, event.y)
                    }
                    invalidate()
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    if (isMovingBase) {
                        // 移动整个摇杆
                        baseX = event.x
                        baseY = event.y
                        stickX = baseX
                        stickY = baseY
                    } else if (isMovingStick) {
                        // 移动摇杆头
                        updateStickPosition(event.x, event.y)
                    }
                    invalidate()
                    true
                }
                MotionEvent.ACTION_UP -> {
                    if (isMovingStick) {
                        // 摇杆回中
                        stickX = baseX
                        stickY = baseY
                        
                        // 发送摇杆回中事件
                        controller.sendStickEvent(stickId, 0f, 0f)
                    }
                    
                    isMovingBase = false
                    isMovingStick = false
                    invalidate()
                    true
                }
                else -> false
            }
        }
    }
    
    private fun updateStickPosition(x: Float, y: Float) {
        // 计算摇杆偏移量（限制在底座范围内）
        val dx = x - baseX
        val dy = y - baseY
        val distance = Math.sqrt((dx * dx + dy * dy).toDouble()).toFloat()
        
        if (distance <= baseRadius) {
            stickX = x
            stickY = y
        } else {
            // 限制在圆形范围内
            val angle = Math.atan2(dy.toDouble(), dx.toDouble())
            stickX = baseX + (Math.cos(angle) * baseRadius).toFloat()
            stickY = baseY + (Math.sin(angle) * baseRadius).toFloat()
        }
        
        // 计算并发送摇杆输入
        val xAxis = (stickX - baseX) / baseRadius
        val yAxis = (stickY - baseY) / baseRadius
        
        controller.sendStickEvent(stickId, xAxis, yAxis)
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        // 绘制底座
        canvas.drawCircle(baseX, baseY, baseRadius, basePaint)
        
        // 绘制摇杆
        canvas.drawCircle(stickX, stickY, stickRadius, stickPaint)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(160) 
        setMeasuredDimension(size, size)
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
    
    fun resetPosition() {
        baseX = defaultX
        baseY = defaultY
        stickX = baseX
        stickY = baseY
        invalidate()
    }
    
    fun savePosition() {
        val prefs = context.getSharedPreferences("gamepad_layout", Context.MODE_PRIVATE)
        prefs.edit().apply {
            putFloat("joystick_${stickId}_x", baseX)
            putFloat("joystick_${stickId}_y", baseY)
            apply()
        }
    }
    
    fun loadPosition() {
        val prefs = context.getSharedPreferences("gamepad_layout", Context.MODE_PRIVATE)
        baseX = prefs.getFloat("joystick_${stickId}_x", defaultX)
        baseY = prefs.getFloat("joystick_${stickId}_y", defaultY)
        stickX = baseX
        stickY = baseY
        invalidate()
    }
}

class GameController(var activity: Activity) {
    // 添加可移动按钮的列表
    private val movableButtons = mutableListOf<MovableButtonView>()
    private val movableJoysticks = mutableListOf<MovableJoystickView>()
    private var dpadView: MovableDpadView? = null
    
    // 添加编辑模式标志
    var isEditMode = false
        set(value) {
            field = value
            // 在编辑模式下显示布局按钮，游戏模式下隐藏
            toggleEditMode(value)
        }
    
    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            
            // 获取屏幕尺寸
            val displayMetrics = context.resources.displayMetrics
            val screenWidth = displayMetrics.widthPixels
            val screenHeight = displayMetrics.heightPixels
            
            // 创建左摇杆
            val leftJoystick = MovableJoystickView(context, 1, true, controller).apply {
                defaultX = screenWidth * 0.2f
                defaultY = screenHeight * 0.7f
                baseX = defaultX
                baseY = defaultY
                stickX = baseX
                stickY = baseY
                
                layoutParams = FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
            }
            controller.movableJoysticks.add(leftJoystick)
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(leftJoystick)
            
            // 创建右摇杆
            val rightJoystick = MovableJoystickView(context, 2, false, controller).apply {
                defaultX = screenWidth * 0.8f
                defaultY = screenHeight * 0.7f
                baseX = defaultX
                baseY = defaultY
                stickX = baseX
                stickY = baseY
                
                layoutParams = FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
            }
            controller.movableJoysticks.add(rightJoystick)
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(rightJoystick)
            
            // 创建十字键
            val dpad = MovableDpadView(context, controller).apply {
                defaultX = screenWidth * 0.2f
                defaultY = screenHeight * 0.5f
                baseX = defaultX
                baseY = defaultY
                
                layoutParams = FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
            }
            controller.dpadView = dpad
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(dpad)
            
            // 创建ABXY按钮
            val buttonIds = listOf(
                GamePadButtonInputId.A.ordinal to "A",
                GamePadButtonInputId.B.ordinal to "B",
                GamePadButtonInputId.X.ordinal to "X",
                GamePadButtonInputId.Y.ordinal to "Y"
            )
            
            buttonIds.forEachIndexed { index, (id, text) ->
                val button = MovableButtonView(context, text, id, controller).apply {
                    // 设置默认位置
                    defaultX = screenWidth * 0.8f - (index % 2) * 100
                    defaultY = screenHeight * 0.5f - (index / 2) * 100
                    currentX = defaultX
                    currentY = defaultY
                    
                    layoutParams = FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT,
                        ViewGroup.LayoutParams.WRAP_CONTENT
                    )
                }
                
                controller.movableButtons.add(button)
                view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(button)
            }
            
            // 创建L/R/ZL/ZR按钮
            val shoulderButtonIds = listOf(
                GamePadButtonInputId.LeftShoulder.ordinal to "L",
                GamePadButtonInputId.RightShoulder.ordinal to "R",
                GamePadButtonInputId.LeftTrigger.ordinal to "ZL",
                GamePadButtonInputId.RightTrigger.ordinal to "ZR"
            )
            
            shoulderButtonIds.forEachIndexed { index, (id, text) ->
                val button = MovableButtonView(context, text, id, controller).apply {
                    // 设置默认位置
                    defaultX = if (index < 2) screenWidth * 0.2f + index * 100 else screenWidth * 0.2f + (index - 2) * 100
                    defaultY = if (index < 2) screenHeight * 0.2f else screenHeight * 0.1f
                    currentX = defaultX
                    currentY = defaultY
                    
                    layoutParams = FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT,
                        ViewGroup.LayoutParams.WRAP_CONTENT
                    )
                }
                
                controller.movableButtons.add(button)
                view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(button)
            }
            
            // 创建+/-按钮
            val menuButtonIds = listOf(
                GamePadButtonInputId.Minus.ordinal to "-",
                GamePadButtonInputId.Plus.ordinal to "+"
            )
            
            menuButtonIds.forEachIndexed { index, (id, text) ->
                val button = MovableButtonView(context, text, id, controller).apply {
                    // 设置默认位置
                    defaultX = screenWidth * 0.5f - 50 + index * 100
                    defaultY = screenHeight * 0.1f
                    currentX = defaultX
                    currentY = defaultY
                    
                    layoutParams = FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT,
                        ViewGroup.LayoutParams.WRAP_CONTENT
                    )
                }
                
                controller.movableButtons.add(button)
                view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(button)
            }
            
            // 添加L3按钮
            val l3Button = DoubleCircleButtonView(context, "L3", GamePadButtonInputId.LeftStickButton.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            controller.sendButtonEvent(
                                GamePadButtonInputId.LeftStickButton.ordinal,
                                true
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            controller.sendButtonEvent(
                                GamePadButtonInputId.LeftStickButton.ordinal,
                                false
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
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            controller.sendButtonEvent(
                                GamePadButtonInputId.RightStickButton.ordinal,
                                true
                            )
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            controller.sendButtonEvent(
                                GamePadButtonInputId.RightStickButton.ordinal,
                                false
                            )
                            true
                        }
                        else -> false
                    }
                }
            }
            
            // 设置L3和R3按钮的布局参数
            val l3LayoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = dpToPx(context, 30) // 距离顶部30dp，靠近边缘
                leftMargin = dpToPx(context, 280) // 距离左侧280dp，位于L键右边
            }
            l3Button.layoutParams = l3LayoutParams
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(l3Button)
            controller.l3Button = l3Button

            val r3LayoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = dpToPx(context, 30) // 距离顶部30dp，靠近边缘
                rightMargin = dpToPx(context, 280) // 距离右侧280dp，位于R键左边
            }
            r3Button.layoutParams = r3LayoutParams
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(r3Button)
            controller.r3Button = r3Button

            // 加载保存的位置
            controller.loadButtonPositions()
            
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
        // 初始化controllerId
        connect()
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
    
    // 发送按钮事件
    fun sendButtonEvent(buttonId: Int, pressed: Boolean) {
        if (controllerId == -1) return
        
        if (pressed) {
            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controllerId)
        } else {
            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controllerId)
        }
    }
    
    // 发送摇杆事件
    fun sendStickEvent(stickId: Int, xAxis: Float, yAxis: Float) {
        if (controllerId == -1) return
        
        val setting = QuickSettings(activity)
        val clampedX = MathUtils.clamp(xAxis * setting.controllerStickSensitivity, -1f, 1f)
        val clampedY = MathUtils.clamp(yAxis * setting.controllerStickSensitivity, -1f, 1f)
        
        RyujinxNative.jnaInstance.inputSetStickAxis(
            stickId,
            clampedX,
            -clampedY,
            controllerId
        )
    }
    
    // 切换编辑模式
    private fun toggleEditMode(enable: Boolean) {
        // 在这里实现编辑模式的切换逻辑
        // 例如显示/隐藏编辑控件，启用/禁用按钮移动等
    }
    
    // 保存按钮位置
    fun saveButtonPositions() {
        movableButtons.forEach { it.savePosition() }
        movableJoysticks.forEach { it.savePosition() }
        dpadView?.savePosition()
    }
    
    // 加载按钮位置
    fun loadButtonPositions() {
        movableButtons.forEach { it.loadPosition() }
        movableJoysticks.forEach { it.loadPosition() }
        dpadView?.loadPosition()
    }
    
    // 重置按钮位置
    fun resetButtonPositions() {
        movableButtons.forEach { it.resetPosition() }
        movableJoysticks.forEach { it.resetPosition() }
        dpadView?.resetPosition()
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
