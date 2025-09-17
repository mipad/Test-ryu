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
        val size = dpToPx(50) // 减小按钮尺寸
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
class DPadView(context: Context) : View(context) {
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
    
    private val centerPaint = Paint().apply {
        color = Color.argb(128, 150, 150, 150)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val directionPaint = Paint().apply {
        color = Color.argb(200, 255, 255, 255)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private var currentDirection = 0
    
    fun setDirection(direction: Int) {
        currentDirection = direction
        invalidate()
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val radius = (width.coerceAtMost(height) / 2f) * 0.9f
        
        canvas.drawCircle(centerX, centerY, radius, outerCirclePaint)
        canvas.drawCircle(centerX, centerY, radius * 0.8f, innerCirclePaint)
        canvas.drawCircle(centerX, centerY, radius * 0.3f, centerPaint)
        
        val arrowSize = radius * 0.2f
        val arrowLength = radius * 0.6f
        
        if (currentDirection == 1 || currentDirection == 0) {
            canvas.drawRect(
                centerX - arrowSize, 
                centerY - arrowLength - arrowSize,
                centerX + arrowSize, 
                centerY - arrowLength + arrowSize,
                directionPaint
            )
        }
        
        if (currentDirection == 2 || currentDirection == 0) {
            canvas.drawRect(
                centerX + arrowLength - arrowSize,
                centerY - arrowSize,
                centerX + arrowLength + arrowSize,
                centerY + arrowSize,
                directionPaint
            )
        }
        
        if (currentDirection == 3 || currentDirection == 0) {
            canvas.drawRect(
                centerX - arrowSize,
                centerY + arrowLength - arrowSize,
                centerX + arrowSize,
                centerY + arrowLength + arrowSize,
                directionPaint
            )
        }
        
        if (currentDirection == 4 || currentDirection == 0) {
            canvas.drawRect(
                centerX - arrowLength - arrowSize,
                centerY - arrowSize,
                centerX - arrowLength + arrowSize,
                centerY + arrowSize,
                directionPaint
            )
        }
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(80) // 减小DPad尺寸
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
            
            // 使用整个屏幕作为容器
            val container = view.findViewById<FrameLayout>(R.id.constraint) ?: view as FrameLayout
            
            // 清除原有的左右容器内容
            view.findViewById<FrameLayout>(R.id.leftcontainer)?.removeAllViews()
            view.findViewById<FrameLayout>(R.id.rightcontainer)?.removeAllViews()
            
            val buttonSize = dpToPx(context, 50) // 统一按钮尺寸
            val buttonMargin = dpToPx(context, 8)
            val screenMargin = dpToPx(context, 15)
            
            // 添加十字方向键（左下角）
            val dPad = DPadView(context).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                            val x = event.x
                            val y = event.y
                            val centerX = width / 2f
                            val centerY = height / 2f
                            
                            val dx = x - centerX
                            val dy = y - centerY
                            val angle = Math.atan2(dy.toDouble(), dx.toDouble()) * 180 / Math.PI
                            
                            val direction = when {
                                angle in -45.0..45.0 -> 2 // 右
                                angle in 45.0..135.0 -> 3 // 下
                                angle > 135.0 || angle < -135.0 -> 4 // 左
                                else -> 1 // 上
                            }
                            
                            setDirection(direction)
                            
                            when (direction) {
                                1 -> handleDirectionPress(GamePadButtonInputId.DpadUp.ordinal, controller)
                                2 -> handleDirectionPress(GamePadButtonInputId.DpadRight.ordinal, controller)
                                3 -> handleDirectionPress(GamePadButtonInputId.DpadDown.ordinal, controller)
                                4 -> handleDirectionPress(GamePadButtonInputId.DpadLeft.ordinal, controller)
                            }
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setDirection(0)
                            releaseAllDirections(controller)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val dPadLayoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.START
                bottomMargin = screenMargin
                leftMargin = screenMargin
            }
            dPad.layoutParams = dPadLayoutParams
            container.addView(dPad)
            controller.dPad = dPad
            
            // 添加ABXY按钮（右下角菱形布局）
            val aButton = createButton(context, "A", GamePadButtonInputId.A.ordinal, controller, buttonSize)
            val bButton = createButton(context, "B", GamePadButtonInputId.B.ordinal, controller, buttonSize)
            val xButton = createButton(context, "X", GamePadButtonInputId.X.ordinal, controller, buttonSize)
            val yButton = createButton(context, "Y", GamePadButtonInputId.Y.ordinal, controller, buttonSize)
            
            val aButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = screenMargin
                rightMargin = screenMargin
            }
            aButton.layoutParams = aButtonLayoutParams
            container.addView(aButton)
            controller.aButton = aButton
            
            val bButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = screenMargin + buttonSize + buttonMargin
                rightMargin = screenMargin - buttonSize / 2
            }
            bButton.layoutParams = bButtonLayoutParams
            container.addView(bButton)
            controller.bButton = bButton
            
            val xButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = screenMargin - buttonSize / 2
                rightMargin = screenMargin + buttonSize + buttonMargin
            }
            xButton.layoutParams = xButtonLayoutParams
            container.addView(xButton)
            controller.xButton = xButton
            
            val yButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = screenMargin - buttonSize - buttonMargin
                rightMargin = screenMargin
            }
            yButton.layoutParams = yButtonLayoutParams
            container.addView(yButton)
            controller.yButton = yButton
            
            // 添加L/R肩键（左上角和右上角）
            val lButton = createButton(context, "L", GamePadButtonInputId.LeftShoulder.ordinal, controller, buttonSize)
            val rButton = createButton(context, "R", GamePadButtonInputId.RightShoulder.ordinal, controller, buttonSize)
            
            val lButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = screenMargin
                leftMargin = screenMargin
            }
            lButton.layoutParams = lButtonLayoutParams
            container.addView(lButton)
            controller.lButton = lButton
            
            val rButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = screenMargin
                rightMargin = screenMargin
            }
            rButton.layoutParams = rButtonLayoutParams
            container.addView(rButton)
            controller.rButton = rButton
            
            // 添加ZL/ZR扳机键（L/R键下方）
            val zlButton = createButton(context, "ZL", GamePadButtonInputId.LeftTrigger.ordinal, controller, buttonSize)
            val zrButton = createButton(context, "ZR", GamePadButtonInputId.RightTrigger.ordinal, controller, buttonSize)
            
            val zlButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = screenMargin + buttonSize + buttonMargin
                leftMargin = screenMargin
            }
            zlButton.layoutParams = zlButtonLayoutParams
            container.addView(zlButton)
            controller.zlButton = zlButton
            
            val zrButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = screenMargin + buttonSize + buttonMargin
                rightMargin = screenMargin
            }
            zrButton.layoutParams = zrButtonLayoutParams
            container.addView(zrButton)
            controller.zrButton = zrButton
            
            // 添加+/-按钮（顶部中间）
            val plusButton = createButton(context, "+", GamePadButtonInputId.Plus.ordinal, controller, buttonSize)
            val minusButton = createButton(context, "-", GamePadButtonInputId.Minus.ordinal, controller, buttonSize)
            
            val plusButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = screenMargin
                rightMargin = screenMargin + buttonSize + buttonMargin * 2
            }
            plusButton.layoutParams = plusButtonLayoutParams
            container.addView(plusButton)
            controller.plusButton = plusButton
            
            val minusButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = screenMargin
                leftMargin = screenMargin + buttonSize + buttonMargin * 2
            }
            minusButton.layoutParams = minusButtonLayoutParams
            container.addView(minusButton)
            controller.minusButton = minusButton
            
            // 添加L3/R3按钮（底部中间）
            val l3Button = createButton(context, "L3", GamePadButtonInputId.LeftStickButton.ordinal, controller, buttonSize)
            val r3Button = createButton(context, "R3", GamePadButtonInputId.RightStickButton.ordinal, controller, buttonSize)
            
            val l3LayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.START
                bottomMargin = screenMargin
                leftMargin = screenMargin + buttonSize * 2
            }
            l3Button.layoutParams = l3LayoutParams
            container.addView(l3Button)
            controller.l3Button = l3Button

            val r3LayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = screenMargin
                rightMargin = screenMargin + buttonSize * 2
            }
            r3Button.layoutParams = r3LayoutParams
            container.addView(r3Button)
            controller.r3Button = r3Button

            return view
        }
        
        private fun createButton(context: Context, text: String, buttonId: Int, controller: GameController, size: Int): DoubleCircleButtonView {
            return DoubleCircleButtonView(context, text, buttonId).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        MotionEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(buttonId, controller.controllerId)
                            true
                        }
                        MotionEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(buttonId, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
        }
        
        private fun handleDirectionPress(direction: Int, controller: GameController) {
            // 释放所有方向键
            releaseAllDirections(controller)
            // 按下当前方向
            RyujinxNative.jnaInstance.inputSetButtonPressed(direction, controller.controllerId)
        }
        
        private fun releaseAllDirections(controller: GameController) {
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
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
    var dPad: DPadView? = null
    var aButton: DoubleCircleButtonView? = null
    var bButton: DoubleCircleButtonView? = null
    var xButton: DoubleCircleButtonView? = null
    var yButton: DoubleCircleButtonView? = null
    var lButton: DoubleCircleButtonView? = null
    var rButton: DoubleCircleButtonView? = null
    var zlButton: DoubleCircleButtonView? = null
    var zrButton: DoubleCircleButtonView? = null
    var plusButton: DoubleCircleButtonView? = null
    var minusButton: DoubleCircleButtonView? = null
    var l3Button: DoubleCircleButtonView? = null
    var r3Button: DoubleCircleButtonView? = null
    var controllerId: Int = -1
    val isVisible: Boolean
        get() = controllerView?.isVisible ?: false

    init {
        // 不再使用RadialGamePad
    }

    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible
            listOf(dPad, aButton, bButton, xButton, yButton, lButton, rButton, 
                  zlButton, zrButton, plusButton, minusButton, l3Button, r3Button)
                .forEach { it?.isVisible = isVisible }

            if (isVisible) connect()
        }
    }

    fun connect() {
        if (controllerId == -1)
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
    }
}
