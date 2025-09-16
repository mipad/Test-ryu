package org.ryujinx.android

import android.app.Activity
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.util.TypedValue
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
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings

// 自定义双圆形按钮视图
class DoubleCircleButtonView(context: Context, val buttonText: String, val buttonId: Int) : View(context) {
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
        canvas.drawCircle(centerX, centerY, innerRadius, innerCirclePaint)
        
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

// 整体十字方向键
class DPadView(context: Context, val controller: GameController) : View(context) {
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
    
    private val textPaint = Paint().apply {
        color = Color.WHITE
        textSize = 16f
        textAlign = Paint.Align.CENTER
        typeface = Typeface.DEFAULT_BOLD
        isAntiAlias = true
    }
    
    var centerX = 0f
    var centerY = 0f
    private var baseRadius = 100f
    private var directionRadius = 30f
    
    private var currentDirection = -1
    
    init {
        setOnTouchListener { _, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                    val x = event.x
                    val y = event.y
                    
                    // 计算方向
                    val angle = Math.atan2((y - centerY).toDouble(), (x - centerX).toDouble()) * 180 / Math.PI
                    val normalizedAngle = (angle + 360) % 360
                    
                    currentDirection = when {
                        normalizedAngle >= 45 && normalizedAngle < 135 -> 2 // 下
                        normalizedAngle >= 135 && normalizedAngle < 225 -> 3 // 左
                        normalizedAngle >= 225 && normalizedAngle < 315 -> 0 // 上
                        else -> 1 // 右
                    }
                    
                    // 发送方向按下事件
                    sendDirectionEvent(currentDirection, true)
                    invalidate()
                    true
                }
                MotionEvent.ACTION_UP -> {
                    if (currentDirection != -1) {
                        sendDirectionEvent(currentDirection, false)
                        currentDirection = -1
                        invalidate()
                    }
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
        canvas.drawCircle(centerX, centerY, baseRadius, basePaint)
        
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
            val dirX = centerX + (baseRadius * 0.7f * Math.cos(rad)).toFloat()
            val dirY = centerY + (baseRadius * 0.7f * Math.sin(rad)).toFloat()
            
            canvas.drawCircle(dirX, dirY, directionRadius, directionPaint)
        }
        
        // 绘制方向标记
        val directions = listOf("↑", "→", "↓", "←")
        val angles = listOf(90f, 0f, 270f, 180f)
        
        for (i in directions.indices) {
            val rad = Math.toRadians(angles[i].toDouble())
            val textX = centerX + (baseRadius * 0.85f * Math.cos(rad)).toFloat()
            val textY = centerY + (baseRadius * 0.85f * Math.sin(rad)).toFloat() - (textPaint.descent() + textPaint.ascent()) / 2
            
            canvas.drawText(directions[i], textX, textY, textPaint)
        }
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(200)
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

// 整体ABXY按钮组
class ABXYButtonsView(context: Context, val controller: GameController) : View(context) {
    private val buttonPaint = Paint().apply {
        color = Color.argb(200, 80, 80, 80)
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
    
    private val pressedPaint = Paint().apply {
        color = Color.argb(100, 255, 255, 255)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val buttonRadius = 40f
    private var pressedButton = -1 // -1: 无按钮按下, 0: A, 1: B, 2: X, 3: Y
    
    var centerX = 0f
    var centerY = 0f
    
    init {
        setOnTouchListener { _, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                    val x = event.x
                    val y = event.y
                    
                    // 检查点击了哪个按钮
                    val buttons = listOf(
                        Pair(centerX + buttonRadius * 1.5f, centerY), // 右: B
                        Pair(centerX, centerY - buttonRadius * 1.5f), // 上: Y
                        Pair(centerX - buttonRadius * 1.5f, centerY), // 左: X
                        Pair(centerX, centerY + buttonRadius * 1.5f)  // 下: A
                    )
                    
                    var newPressedButton = -1
                    for (i in buttons.indices) {
                        val (btnX, btnY) = buttons[i]
                        val distance = Math.sqrt(
                            Math.pow((x - btnX).toDouble(), 2.0) + 
                            Math.pow((y - btnY).toDouble(), 2.0)
                        )
                        
                        if (distance <= buttonRadius) {
                            newPressedButton = i
                            break
                        }
                    }
                    
                    if (newPressedButton != pressedButton) {
                        // 释放之前按下的按钮
                        if (pressedButton != -1) {
                            sendButtonEvent(pressedButton, false)
                        }
                        
                        // 按下新按钮
                        if (newPressedButton != -1) {
                            sendButtonEvent(newPressedButton, true)
                        }
                        
                        pressedButton = newPressedButton
                        invalidate()
                    }
                    
                    true
                }
                MotionEvent.ACTION_UP -> {
                    if (pressedButton != -1) {
                        sendButtonEvent(pressedButton, false)
                        pressedButton = -1
                        invalidate()
                    }
                    true
                }
                else -> false
            }
        }
    }
    
    private fun sendButtonEvent(buttonIndex: Int, pressed: Boolean) {
        val buttonId = when (buttonIndex) {
            0 -> GamePadButtonInputId.B.ordinal // 右: B
            1 -> GamePadButtonInputId.Y.ordinal // 上: Y
            2 -> GamePadButtonInputId.X.ordinal // 左: X
            3 -> GamePadButtonInputId.A.ordinal // 下: A
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
        
        // 绘制四个按钮
        val buttons = listOf(
            Pair(centerX + buttonRadius * 1.5f, centerY, "B"), // 右
            Pair(centerX, centerY - buttonRadius * 1.5f, "Y"), // 上
            Pair(centerX - buttonRadius * 1.5f, centerY, "X"), // 左
            Pair(centerX, centerY + buttonRadius * 1.5f, "A")  // 下
        )
        
        for ((i, (x, y, text)) in buttons.withIndex()) {
            // 绘制按钮
            canvas.drawCircle(x, y, buttonRadius, buttonPaint)
            
            // 如果按下，添加高亮效果
            if (i == pressedButton) {
                canvas.drawCircle(x, y, buttonRadius, pressedPaint)
            }
            
            // 绘制文字
            val textY = y - (textPaint.descent() + textPaint.ascent()) / 2
            canvas.drawText(text, x, textY, textPaint)
        }
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(200)
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
                    
                    val distanceToBase = Math.sqrt(
                        Math.pow((x - baseX).toDouble(), 2.0) + 
                        Math.pow((y - baseY).toDouble(), 2.0)
                    )
                    
                    if (distanceToBase <= baseRadius) {
                        isMovingBase = true
                    } else if (distanceToBase <= baseRadius + stickRadius) {
                        isMovingStick = true
                        updateStickPosition(event.x, event.y)
                    }
                    invalidate()
                    true
                }
                MotionEvent.ACTION_MOVE -> {
                    if (isMovingBase) {
                        baseX = event.x
                        baseY = event.y
                        stickX = baseX
                        stickY = baseY
                    } else if (isMovingStick) {
                        updateStickPosition(event.x, event.y)
                    }
                    invalidate()
                    true
                }
                MotionEvent.ACTION_UP -> {
                    if (isMovingStick) {
                        stickX = baseX
                        stickY = baseY
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
        val dx = x - baseX
        val dy = y - baseY
        val distance = Math.sqrt((dx * dx + dy * dy).toDouble()).toFloat()
        
        if (distance <= baseRadius) {
            stickX = x
            stickY = y
        } else {
            val angle = Math.atan2(dy.toDouble(), dx.toDouble())
            stickX = baseX + (Math.cos(angle) * baseRadius).toFloat()
            stickY = baseY + (Math.sin(angle) * baseRadius).toFloat()
        }
        
        val xAxis = (stickX - baseX) / baseRadius
        val yAxis = (stickY - baseY) / baseRadius
        
        controller.sendStickEvent(stickId, xAxis, yAxis)
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        canvas.drawCircle(baseX, baseY, baseRadius, basePaint)
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
    private var dpadView: DPadView? = null
    private var abxyView: ABXYButtonsView? = null
    private var leftJoystick: MovableJoystickView? = null
    private var rightJoystick: MovableJoystickView? = null
    private var l3Button: DoubleCircleButtonView? = null
    private var r3Button: DoubleCircleButtonView? = null
    
    private var shoulderButtons = mutableListOf<DoubleCircleButtonView>()
    private var menuButtons = mutableListOf<DoubleCircleButtonView>()
    
    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            
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
            controller.leftJoystick = leftJoystick
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
            controller.rightJoystick = rightJoystick
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(rightJoystick)
            
            // 创建整体十字键
            val dpad = DPadView(context, controller).apply {
                centerX = screenWidth * 0.2f
                centerY = screenHeight * 0.5f
                
                layoutParams = FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
            }
            controller.dpadView = dpad
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(dpad)
            
            // 创建整体ABXY按钮
            val abxy = ABXYButtonsView(context, controller).apply {
                centerX = screenWidth * 0.8f
                centerY = screenHeight * 0.5f
                
                layoutParams = FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
            }
            controller.abxyView = abxy
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(abxy)
            
            // 创建L/R/ZL/ZR按钮
            val shoulderButtonIds = listOf(
                GamePadButtonInputId.LeftShoulder.ordinal to "L",
                GamePadButtonInputId.RightShoulder.ordinal to "R",
                GamePadButtonInputId.LeftTrigger.ordinal to "ZL",
                GamePadButtonInputId.RightTrigger.ordinal to "ZR"
            )
            
            shoulderButtonIds.forEachIndexed { index, (id, text) ->
                val button = DoubleCircleButtonView(context, text, id).apply {
                    val layoutParams = FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT,
                        ViewGroup.LayoutParams.WRAP_CONTENT
                    ).apply {
                        gravity = if (index < 2) android.view.Gravity.TOP or android.view.Gravity.START
                                 else android.view.Gravity.TOP or android.view.Gravity.END
                        topMargin = dpToPx(context, if (index < 2) 20 else 20)
                        if (index < 2) {
                            leftMargin = dpToPx(context, 20 + index * 80)
                        } else {
                            rightMargin = dpToPx(context, 20 + (index - 2) * 80)
                        }
                    }
                    this.layoutParams = layoutParams
                    
                    setOnTouchListener { _, event ->
                        when (event.action) {
                            MotionEvent.ACTION_DOWN -> {
                                setPressedState(true)
                                controller.sendButtonEvent(id, true)
                                true
                            }
                            MotionEvent.ACTION_UP -> {
                                setPressedState(false)
                                controller.sendButtonEvent(id, false)
                                true
                            }
                            else -> false
                        }
                    }
                }
                
                controller.shoulderButtons.add(button)
                view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(button)
            }
            
            // 创建+/-按钮
            val menuButtonIds = listOf(
                GamePadButtonInputId.Minus.ordinal to "-",
                GamePadButtonInputId.Plus.ordinal to "+"
            )
            
            menuButtonIds.forEachIndexed { index, (id, text) ->
                val button = DoubleCircleButtonView(context, text, id).apply {
                    val layoutParams = FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT,
                        ViewGroup.LayoutParams.WRAP_CONTENT
                    ).apply {
                        gravity = android.view.Gravity.TOP or android.view.Gravity.CENTER_HORIZONTAL
                        topMargin = dpToPx(context, 20)
                        if (index == 0) {
                            leftMargin = dpToPx(context, screenWidth / 2 - 100)
                        } else {
                            leftMargin = dpToPx(context, screenWidth / 2 + 20)
                        }
                    }
                    this.layoutParams = layoutParams
                    
                    setOnTouchListener { _, event ->
                        when (event.action) {
                            MotionEvent.ACTION_DOWN -> {
                                setPressedState(true)
                                controller.sendButtonEvent(id, true)
                                true
                            }
                            MotionEvent.ACTION_UP -> {
                                setPressedState(false)
                                controller.sendButtonEvent(id, false)
                                true
                            }
                            else -> false
                        }
                    }
                }
                
                controller.menuButtons.add(button)
                view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(button)
            }
            
            // 添加L3按钮
            val l3Button = DoubleCircleButtonView(context, "L3", GamePadButtonInputId.LeftStickButton.ordinal).apply {
                val layoutParams = FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                ).apply {
                    gravity = android.view.Gravity.BOTTOM or android.view.Gravity.START
                    bottomMargin = dpToPx(context, 30)
                    leftMargin = dpToPx(context, screenWidth / 4 - 35)
                }
                this.layoutParams = layoutParams
                
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            controller.sendButtonEvent(GamePadButtonInputId.LeftStickButton.ordinal, true)
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            controller.sendButtonEvent(GamePadButtonInputId.LeftStickButton.ordinal, false)
                            true
                        }
                        else -> false
                    }
                }
            }
            controller.l3Button = l3Button
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(l3Button)

            // 添加R3按钮
            val r3Button = DoubleCircleButtonView(context, "R3", GamePadButtonInputId.RightStickButton.ordinal).apply {
                val layoutParams = FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                ).apply {
                    gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                    bottomMargin = dpToPx(context, 30)
                    rightMargin = dpToPx(context, screenWidth / 4 - 35)
                }
                this.layoutParams = layoutParams
                
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            controller.sendButtonEvent(GamePadButtonInputId.RightStickButton.ordinal, true)
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            controller.sendButtonEvent(GamePadButtonInputId.RightStickButton.ordinal, false)
                            true
                        }
                        else -> false
                    }
                }
            }
            controller.r3Button = r3Button
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(r3Button)

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
    var controllerId: Int = -1
    val isVisible: Boolean
        get() {
            controllerView?.apply {
                return this.isVisible
            }
            return false
        }

    init {
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
    
    fun sendButtonEvent(buttonId: Int, pressed: Boolean) {
        if (controllerId == -1) return
        
        if (pressed) {
            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controllerId)
        } else {
            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controllerId)
        }
    }
    
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
}
