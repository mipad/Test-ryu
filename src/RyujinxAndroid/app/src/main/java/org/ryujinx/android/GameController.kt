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
    
    private var currentDirection = 0 // 0: none, 1: up, 2: right, 3: down, 4: left
    
    fun setDirection(direction: Int) {
        currentDirection = direction
        invalidate()
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val radius = (width.coerceAtMost(height) / 2f) * 0.9f
        
        // 绘制外圈
        canvas.drawCircle(centerX, centerY, radius, outerCirclePaint)
        
        // 绘制内圈
        canvas.drawCircle(centerX, centerY, radius * 0.8f, innerCirclePaint)
        
        // 绘制中心圆
        canvas.drawCircle(centerX, centerY, radius * 0.3f, centerPaint)
        
        // 绘制方向指示
        val arrowSize = radius * 0.2f
        val arrowLength = radius * 0.6f
        
        if (currentDirection == 1 || currentDirection == 0) {
            // 上箭头
            canvas.drawRect(
                centerX - arrowSize, 
                centerY - arrowLength - arrowSize,
                centerX + arrowSize, 
                centerY - arrowLength + arrowSize,
                directionPaint
            )
        }
        
        if (currentDirection == 2 || currentDirection == 0) {
            // 右箭头
            canvas.drawRect(
                centerX + arrowLength - arrowSize,
                centerY - arrowSize,
                centerX + arrowLength + arrowSize,
                centerY + arrowSize,
                directionPaint
            )
        }
        
        if (currentDirection == 3 || currentDirection == 0) {
            // 下箭头
            canvas.drawRect(
                centerX - arrowSize,
                centerY + arrowLength - arrowSize,
                centerX + arrowSize,
                centerY + arrowLength + arrowSize,
                directionPaint
            )
        }
        
        if (currentDirection == 4 || currentDirection == 0) {
            // 左箭头
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
        val size = dpToPx(100)
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
            
            // 创建独立控件
            val container = view.findViewById<FrameLayout>(R.id.controller_container)!!
            
            // 添加十字方向键
            val dPad = DPadView(context).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN, KeyEvent.ACTION_MOVE -> {
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
                            
                            // 发送方向键事件
                            when (direction) {
                                1 -> {
                                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                                }
                                2 -> {
                                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                                }
                                3 -> {
                                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                                }
                                4 -> {
                                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
                                }
                            }
                            
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setDirection(0)
                            // 释放所有方向键
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controller.controllerId)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controller.controllerId)
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
                bottomMargin = dpToPx(context, 20)
                leftMargin = dpToPx(context, 20)
            }
            dPad.layoutParams = dPadLayoutParams
            container.addView(dPad)
            controller.dPad = dPad
            
            // 添加ABXY按钮
            val aButton = DoubleCircleButtonView(context, "A", GamePadButtonInputId.A.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.A.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.A.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val bButton = DoubleCircleButtonView(context, "B", GamePadButtonInputId.B.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.B.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.B.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val xButton = DoubleCircleButtonView(context, "X", GamePadButtonInputId.X.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.X.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.X.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val yButton = DoubleCircleButtonView(context, "Y", GamePadButtonInputId.Y.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.Y.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.Y.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            // 布局ABXY按钮（右下角菱形布局）
            val buttonSize = dpToPx(context, 70)
            val buttonMargin = dpToPx(context, 10)
            
            val aButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = dpToPx(context, 20)
                rightMargin = dpToPx(context, 20)
            }
            aButton.layoutParams = aButtonLayoutParams
            container.addView(aButton)
            controller.aButton = aButton
            
            val bButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = dpToPx(context, 20) + buttonSize + buttonMargin
                rightMargin = dpToPx(context, 20) - buttonSize / 2 - buttonMargin / 2
            }
            bButton.layoutParams = bButtonLayoutParams
            container.addView(bButton)
            controller.bButton = bButton
            
            val xButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = dpToPx(context, 20) - buttonSize / 2 - buttonMargin / 2
                rightMargin = dpToPx(context, 20) + buttonSize + buttonMargin
            }
            xButton.layoutParams = xButtonLayoutParams
            container.addView(xButton)
            controller.xButton = xButton
            
            val yButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = dpToPx(context, 20) - buttonSize - buttonMargin
                rightMargin = dpToPx(context, 20)
            }
            yButton.layoutParams = yButtonLayoutParams
            container.addView(yButton)
            controller.yButton = yButton
            
            // 添加L/R肩键
            val lButton = DoubleCircleButtonView(context, "L", GamePadButtonInputId.LeftShoulder.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.LeftShoulder.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.LeftShoulder.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val rButton = DoubleCircleButtonView(context, "R", GamePadButtonInputId.RightShoulder.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.RightShoulder.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.RightShoulder.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val lButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = dpToPx(context, 20)
                leftMargin = dpToPx(context, 20)
            }
            lButton.layoutParams = lButtonLayoutParams
            container.addView(lButton)
            controller.lButton = lButton
            
            val rButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = dpToPx(context, 20)
                rightMargin = dpToPx(context, 20)
            }
            rButton.layoutParams = rButtonLayoutParams
            container.addView(rButton)
            controller.rButton = rButton
            
            // 添加ZL/ZR扳机键
            val zlButton = DoubleCircleButtonView(context, "ZL", GamePadButtonInputId.LeftTrigger.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.LeftTrigger.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.LeftTrigger.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val zrButton = DoubleCircleButtonView(context, "ZR", GamePadButtonInputId.RightTrigger.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.RightTrigger.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.RightTrigger.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val zlButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = dpToPx(context, 20) + buttonSize + buttonMargin
                leftMargin = dpToPx(context, 20)
            }
            zlButton.layoutParams = zlButtonLayoutParams
            container.addView(zlButton)
            controller.zlButton = zlButton
            
            val zrButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = dpToPx(context, 20) + buttonSize + buttonMargin
                rightMargin = dpToPx(context, 20)
            }
            zrButton.layoutParams = zrButtonLayoutParams
            container.addView(zrButton)
            controller.zrButton = zrButton
            
            // 添加+/-按钮
            val plusButton = DoubleCircleButtonView(context, "+", GamePadButtonInputId.Plus.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.Plus.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.Plus.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val minusButton = DoubleCircleButtonView(context, "-", GamePadButtonInputId.Minus.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.Minus.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.Minus.ordinal, controller.controllerId)
                            true
                        }
                        else -> false
                    }
                }
            }
            
            val plusButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = dpToPx(context, 20)
                rightMargin = dpToPx(context, 20) + buttonSize + buttonMargin
            }
            plusButton.layoutParams = plusButtonLayoutParams
            container.addView(plusButton)
            controller.plusButton = plusButton
            
            val minusButtonLayoutParams = FrameLayout.LayoutParams(buttonSize, buttonSize).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = dpToPx(context, 20)
                leftMargin = dpToPx(context, 20) + buttonSize + buttonMargin
            }
            minusButton.layoutParams = minusButtonLayoutParams
            container.addView(minusButton)
            controller.minusButton = minusButton
            
            // 添加L3按钮
            val l3Button = DoubleCircleButtonView(context, "L3", GamePadButtonInputId.LeftStickButton.ordinal).apply {
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            setPressedState(true)
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.LeftStickButton.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.LeftStickButton.ordinal, controller.controllerId)
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
                            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.RightStickButton.ordinal, controller.controllerId)
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            setPressedState(false)
                            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.RightStickButton.ordinal, controller.controllerId)
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
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.START
                bottomMargin = dpToPx(context, 20)
                leftMargin = dpToPx(context, 120)
            }
            l3Button.layoutParams = l3LayoutParams
            container.addView(l3Button)
            controller.l3Button = l3Button

            val r3LayoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.BOTTOM or android.view.Gravity.END
                bottomMargin = dpToPx(context, 20)
                rightMargin = dpToPx(context, 120)
            }
            r3Button.layoutParams = r3LayoutParams
            container.addView(r3Button)
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
        get() {
            controllerView?.apply {
                return this.isVisible
            }
            return false
        }

    init {
        // 不再使用RadialGamePad
    }

    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible
            dPad?.isVisible = isVisible
            aButton?.isVisible = isVisible
            bButton?.isVisible = isVisible
            xButton?.isVisible = isVisible
            yButton?.isVisible = isVisible
            lButton?.isVisible = isVisible
            rButton?.isVisible = isVisible
            zlButton?.isVisible = isVisible
            zrButton?.isVisible = isVisible
            plusButton?.isVisible = isVisible
            minusButton?.isVisible = isVisible
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
