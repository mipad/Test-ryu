// SPDX-FileCopyrightText: Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

package org.ryujinx.android

import android.app.Activity
import android.content.Context
import android.graphics.*
import android.graphics.drawable.Drawable
import android.util.AttributeSet
import android.util.TypedValue
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.Button
import android.widget.LinearLayout
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.core.math.MathUtils
import androidx.core.view.isVisible
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings
import android.graphics.drawable.GradientDrawable

// 组合按键视图
class CombinationOverlayView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
    var individualScale: Int = 50
) : View(context, attrs, defStyleAttr) {
    
    var combinationId: Int = 0
    var combinationName: String = ""
    var combinationKeys: List<Int> = emptyList()
    var combinationPressed: Boolean = false
    var opacity: Int = 255
        set(value) {
            field = value
            invalidate()
        }
    
    private var defaultBitmap: Bitmap? = null
    private var pressedBitmap: Bitmap? = null
    
    // 修改：将组合按键的基础缩放从0.45改为1.0
    private val combinationBaseScale = 1.0f
    
    init {
        setBackgroundResource(0)
        loadBitmaps()
    }
    
    fun loadBitmaps() {
        defaultBitmap = getBitmapFromVectorDrawable(R.drawable.combination_default, combinationBaseScale)
        pressedBitmap = getBitmapFromVectorDrawable(R.drawable.combination_pressed, combinationBaseScale)
    }
    
    private fun getBitmapFromVectorDrawable(drawableId: Int, baseScale: Float): Bitmap {
        val drawable = ContextCompat.getDrawable(context, drawableId) ?: 
            throw IllegalArgumentException("Drawable not found: $drawableId")
        
        val userScale = (individualScale.toFloat() + 50) / 100f
        val finalScale = baseScale * userScale
        
        val width = (drawable.intrinsicWidth * finalScale).toInt().takeIf { it > 0 } ?: 100
        val height = (drawable.intrinsicHeight * finalScale).toInt().takeIf { it > 0 } ?: 100
        
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        
        val canvas = Canvas(bitmap)
        drawable.setBounds(0, 0, width, height)
        drawable.draw(canvas)
        return bitmap
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val desiredWidth = defaultBitmap?.width ?: dpToPx(80)
        val desiredHeight = defaultBitmap?.height ?: dpToPx(80)
        
        val minSize = dpToPx(70)
        val width = Math.max(desiredWidth, minSize)
        val height = Math.max(desiredHeight, minSize)
        
        setMeasuredDimension(width, height)
    }
    
    fun setPosition(x: Int, y: Int) {
        val params = layoutParams as? FrameLayout.LayoutParams ?: FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        )
        params.leftMargin = x - width / 2
        params.topMargin = y - height / 2
        layoutParams = params
    }
    
    fun getPosition(): Pair<Int, Int> {
        val params = layoutParams as? FrameLayout.LayoutParams ?: return Pair(0, 0)
        return Pair(params.leftMargin + width / 2, params.topMargin + height / 2)
    }
    
    fun setPressedState(pressed: Boolean) {
        combinationPressed = pressed
        invalidate()
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val paint = Paint().apply {
            alpha = opacity
            isFilterBitmap = true
        }
        
        val bitmap = if (combinationPressed) pressedBitmap else defaultBitmap
        bitmap?.let {
            val left = (width - it.width) / 2f
            val top = (height - it.height) / 2f
            canvas.drawBitmap(it, left, top, paint)
        }
        
        // 绘制组合按键名称 - 确保使用自定义名称
        val textPaint = Paint().apply {
            color = Color.WHITE
            textSize = 16f
            textAlign = Paint.Align.CENTER
            alpha = opacity
        }
        
        val textX = width / 2f
        val textY = height / 2f - (textPaint.descent() + textPaint.ascent()) / 2
        canvas.drawText(combinationName, textX, textY, textPaint)
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
}

// 修正的摇杆视图 - 修复尺寸计算和绘制问题
class JoystickOverlayView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
    var individualScale: Int = 50 // 单个摇杆的缩放
) : View(context, attrs, defStyleAttr) {
    
    var stickId: Int = 0
    var isLeftStick: Boolean = true
    var stickX: Float = 0f
    var stickY: Float = 0f
    private var isTouching: Boolean = false
    
    private var outerRect: Rect = Rect()
    private var innerRect: Rect = Rect()
    private var centerX: Float = 0f
    private var centerY: Float = 0f
    
    private var outerBitmap: Bitmap? = null
    private var innerDefaultBitmap: Bitmap? = null
    private var innerPressedBitmap: Bitmap? = null
    
    private var movementRadius: Float = 0f
    var opacity: Int = 255
        set(value) {
            field = value
            invalidate()
        }
    
    // 外圈基础缩放
    private val outerBaseScale = 0.45f
    // 内圈相对于外圈的比例
    private val innerRelativeScale = 0.85f // 减小内圈相对比例，避免裁剪
    
    init {
        setBackgroundResource(0)
        loadBitmaps()
    }
    
    // 将 loadBitmaps 改为 public
    fun loadBitmaps() {
        // 外圈使用基础缩放 + 单个缩放
        outerBitmap = getBitmapFromVectorDrawable(R.drawable.joystick_range, outerBaseScale)
        
        // 内圈使用相对比例计算
        val innerActualScale = outerBaseScale * innerRelativeScale
        innerDefaultBitmap = getBitmapFromVectorDrawable(R.drawable.joystick, innerActualScale)
        innerPressedBitmap = getBitmapFromVectorDrawable(R.drawable.joystick_depressed, innerActualScale)
    }
    
    private fun getBitmapFromVectorDrawable(drawableId: Int, baseScale: Float): Bitmap {
        val drawable = ContextCompat.getDrawable(context, drawableId) ?: 
            throw IllegalArgumentException("Drawable not found: $drawableId")
        
        // 计算最终缩放 - 使用单个缩放
        val finalScale = baseScale * (individualScale.toFloat() + 50) / 100f
        
        val width = (drawable.intrinsicWidth * finalScale).toInt().takeIf { it > 0 } ?: 100
        val height = (drawable.intrinsicHeight * finalScale).toInt().takeIf { it > 0 } ?: 100
        
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        
        val canvas = Canvas(bitmap)
        drawable.setBounds(0, 0, width, height)
        drawable.draw(canvas)
        return bitmap
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        // 基于外圈位图尺寸计算，确保视图足够大以容纳位图
        val outerWidth = outerBitmap?.width ?: dpToPx(120)
        val outerHeight = outerBitmap?.height ?: dpToPx(120)
        
        // 确保视图尺寸至少为位图尺寸 + 一些边距，避免裁剪
        val desiredWidth = (outerWidth * 1.2f).toInt() // 增加20%边距
        val desiredHeight = (outerHeight * 1.2f).toInt()
        
        val minSize = dpToPx(120)
        val width = Math.max(desiredWidth, minSize)
        val height = Math.max(desiredHeight, minSize)
        
        setMeasuredDimension(width, height)
    }
    
    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        
        centerX = w / 2f
        centerY = h / 2f
        outerRect.set(0, 0, w, h)
        
        // 内圈尺寸基于外圈位图尺寸的比例计算
        val innerSize = innerDefaultBitmap?.width ?: (w * innerRelativeScale).toInt()
        innerRect.set(0, 0, innerSize, innerSize)
        
        // 修正移动半径计算，确保内圈能在外圈内正常移动
        val outerWidth = outerBitmap?.width ?: w
        movementRadius = (outerWidth - innerSize) / 2.5f
    }
    
    fun setPosition(x: Int, y: Int) {
        val params = layoutParams as? FrameLayout.LayoutParams ?: FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        )
        params.leftMargin = x - width / 2
        params.topMargin = y - height / 2
        layoutParams = params
    }
    
    fun getPosition(): Pair<Int, Int> {
        val params = layoutParams as? FrameLayout.LayoutParams ?: return Pair(0, 0)
        return Pair(params.leftMargin + width / 2, params.topMargin + height / 2)
    }
    
    fun updateStickPosition(x: Float, y: Float, isTouching: Boolean = this.isTouching) {
        stickX = MathUtils.clamp(x, -1f, 1f)
        stickY = MathUtils.clamp(y, -1f, 1f)
        this.isTouching = isTouching
        invalidate()
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val paint = Paint().apply {
            alpha = opacity
            isFilterBitmap = true
        }
        
        // 绘制外圈 - 确保居中且不会被裁剪
        outerBitmap?.let { bitmap ->
            val outerLeft = (width - bitmap.width) / 2f
            val outerTop = (height - bitmap.height) / 2f
            canvas.drawBitmap(bitmap, outerLeft, outerTop, paint)
        }
        
        // 绘制内圈 - 修正位置计算，确保内圈在外圈的中心
        val innerBitmap = if (isTouching) innerPressedBitmap else innerDefaultBitmap
        innerBitmap?.let { bitmap ->
            val innerWidth = bitmap.width
            val innerHeight = bitmap.height
            
            // 计算内圈在外圈内的可移动范围
            val outerWidth = outerBitmap?.width ?: width
            val outerHeight = outerBitmap?.height ?: height
            
            // 外圈的实际绘制区域
            val outerDrawLeft = (width - outerWidth) / 2f
            val outerDrawTop = (height - outerHeight) / 2f
            val outerDrawRight = outerDrawLeft + outerWidth
            val outerDrawBottom = outerDrawTop + outerHeight
            
            // 外圈的中心点（实际绘制区域的中心）
            val outerCenterX = outerDrawLeft + outerWidth / 2f
            val outerCenterY = outerDrawTop + outerHeight / 2f
            
            // 内圈在外圈内的最大移动距离 - 确保不会超出外圈边界
            val maxMoveDistance = (outerWidth - innerWidth) / 2.8f
            
            // 计算内圈位置，确保在内圈始终在外圈范围内
            val innerTargetX = outerCenterX + stickX * maxMoveDistance - innerWidth / 2f
            val innerTargetY = outerCenterY + stickY * maxMoveDistance - innerHeight / 2f
            
            // 确保内圈不会超出外圈边界
            val clampedX = MathUtils.clamp(innerTargetX, outerDrawLeft, outerDrawRight - innerWidth)
            val clampedY = MathUtils.clamp(innerTargetY, outerDrawTop, outerDrawBottom - innerHeight)
            
            canvas.drawBitmap(bitmap, clampedX, clampedY, paint)
        }
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
}

// 修正的方向键视图 - 修复尺寸计算和绘制问题
class DpadOverlayView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
    var individualScale: Int = 50 // 单个方向键的缩放
) : View(context, attrs, defStyleAttr) {
    
    var currentDirection: DpadDirection = DpadDirection.NONE
    var opacity: Int = 255
        set(value) {
            field = value
            invalidate()
        }
    
    enum class DpadDirection {
        NONE, UP, DOWN, LEFT, RIGHT, UP_LEFT, UP_RIGHT, DOWN_LEFT, DOWN_RIGHT
    }
    
    private var defaultBitmap: Bitmap? = null
    private var pressedOneDirectionBitmap: Bitmap? = null
    private var pressedTwoDirectionsBitmap: Bitmap? = null
    
    // 方向键基础缩放
    private val dpadBaseScale = 0.45f
    
    init {
        setBackgroundResource(0)
        loadBitmaps()
    }
    
    // 将 loadBitmaps 改为 public
    fun loadBitmaps() {
        defaultBitmap = getBitmapFromVectorDrawable(R.drawable.dpad_standard, dpadBaseScale)
        pressedOneDirectionBitmap = getBitmapFromVectorDrawable(R.drawable.dpad_standard_cardinal_depressed, dpadBaseScale)
        pressedTwoDirectionsBitmap = getBitmapFromVectorDrawable(R.drawable.dpad_standard_diagonal_depressed, dpadBaseScale)
    }
    
    private fun getBitmapFromVectorDrawable(drawableId: Int, baseScale: Float): Bitmap {
        val drawable = ContextCompat.getDrawable(context, drawableId) ?: 
            throw IllegalArgumentException("Drawable not found: $drawableId")
        
        val userScale = (individualScale.toFloat() + 50) / 100f
        val finalScale = baseScale * userScale
        
        val width = (drawable.intrinsicWidth * finalScale).toInt().takeIf { it > 0 } ?: 120
        val height = (drawable.intrinsicHeight * finalScale).toInt().takeIf { it > 0 } ?: 120
        
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        
        val canvas = Canvas(bitmap)
        drawable.setBounds(0, 0, width, height)
        drawable.draw(canvas)
        return bitmap
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        // 基于位图尺寸计算，确保视图足够大以容纳位图
        val bitmapWidth = defaultBitmap?.width ?: dpToPx(100)
        val bitmapHeight = defaultBitmap?.height ?: dpToPx(100)
        
        // 确保视图尺寸至少为位图尺寸 + 一些边距，避免裁剪
        val desiredWidth = (bitmapWidth * 1.2f).toInt() // 增加20%边距
        val desiredHeight = (bitmapHeight * 1.2f).toInt()
        
        val minSize = dpToPx(100)
        val width = Math.max(desiredWidth, minSize)
        val height = Math.max(desiredHeight, minSize)
        
        setMeasuredDimension(width, height)
    }
    
    fun setPosition(x: Int, y: Int) {
        val params = layoutParams as? FrameLayout.LayoutParams ?: FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        )
        params.leftMargin = x - width / 2
        params.topMargin = y - height / 2
        layoutParams = params
    }
    
    fun getPosition(): Pair<Int, Int> {
        val params = layoutParams as? FrameLayout.LayoutParams ?: return Pair(0, 0)
        return Pair(params.leftMargin + width / 2, params.topMargin + height / 2)
    }
    
    fun getDirectionFromTouch(x: Float, y: Float): DpadDirection {
        val centerX = width / 2f
        val centerY = height / 2f
        val relX = (x - centerX) / (width / 2f)
        val relY = (y - centerY) / (height / 2f)
        
        val deadzone = 0.3f
        
        return when {
            relY < -deadzone && relX < -deadzone -> DpadDirection.UP_LEFT
            relY < -deadzone && relX > deadzone -> DpadDirection.UP_RIGHT
            relY > deadzone && relX < -deadzone -> DpadDirection.DOWN_LEFT
            relY > deadzone && relX > deadzone -> DpadDirection.DOWN_RIGHT
            relY < -deadzone -> DpadDirection.UP
            relY > deadzone -> DpadDirection.DOWN
            relX < -deadzone -> DpadDirection.LEFT
            relX > deadzone -> DpadDirection.RIGHT
            else -> DpadDirection.NONE
        }
    }
    
    fun updateDirection(direction: DpadDirection) {
        currentDirection = direction
        invalidate()
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val paint = Paint().apply {
            alpha = opacity
            isFilterBitmap = true
        }
        
        val centerX = width / 2f
        val centerY = height / 2f
        
        val bitmapToDraw = when (currentDirection) {
            DpadDirection.UP, DpadDirection.DOWN, DpadDirection.LEFT, DpadDirection.RIGHT -> pressedOneDirectionBitmap
            DpadDirection.UP_LEFT, DpadDirection.UP_RIGHT, DpadDirection.DOWN_LEFT, DpadDirection.DOWN_RIGHT -> pressedTwoDirectionsBitmap
            else -> defaultBitmap
        }
        
        bitmapToDraw?.let { bitmap ->
            val left = (width - bitmap.width) / 2f
            val top = (height - bitmap.height) / 2f
            
            when (currentDirection) {
                DpadDirection.UP -> {
                    canvas.save()
                    canvas.rotate(0f, centerX, centerY)
                    canvas.drawBitmap(bitmap, left, top, paint)
                    canvas.restore()
                }
                DpadDirection.DOWN -> {
                    canvas.save()
                    canvas.rotate(180f, centerX, centerY)
                    canvas.drawBitmap(bitmap, left, top, paint)
                    canvas.restore()
                }
                DpadDirection.LEFT -> {
                    canvas.save()
                    canvas.rotate(270f, centerX, centerY)
                    canvas.drawBitmap(bitmap, left, top, paint)
                    canvas.restore()
                }
                DpadDirection.RIGHT -> {
                    canvas.save()
                    canvas.rotate(90f, centerX, centerY)
                    canvas.drawBitmap(bitmap, left, top, paint)
                    canvas.restore()
                }
                DpadDirection.UP_RIGHT -> {
                    canvas.save()
                    canvas.rotate(90f, centerX, centerY)
                    canvas.drawBitmap(bitmap, left, top, paint)
                    canvas.restore()
                }
                DpadDirection.DOWN_RIGHT -> {
                    canvas.save()
                    canvas.rotate(180f, centerX, centerY)
                    canvas.drawBitmap(bitmap, left, top, paint)
                    canvas.restore()
                }
                DpadDirection.DOWN_LEFT -> {
                    canvas.save()
                    canvas.rotate(270f, centerX, centerY)
                    canvas.drawBitmap(bitmap, left, top, paint)
                    canvas.restore()
                }
                else -> {
                    canvas.drawBitmap(bitmap, left, top, paint)
                }
            }
        }
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
}

// 修正的按钮视图 - 添加 individualScale 参数
class ButtonOverlayView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
    var individualScale: Int = 50 // 单个按钮的缩放
) : View(context, attrs, defStyleAttr) {
    
    var buttonId: Int = 0
    var buttonText: String = ""
    var buttonPressed: Boolean = false
    var opacity: Int = 255
        set(value) {
            field = value
            invalidate()
        }
    
    private var defaultBitmap: Bitmap? = null
    private var pressedBitmap: Bitmap? = null
    
    init {
        setBackgroundResource(0)
    }
    
    fun setBitmaps(defaultResId: Int, pressedResId: Int) {
        val scale = getScaleForButton()
        defaultBitmap = getBitmapFromVectorDrawable(defaultResId, scale)
        pressedBitmap = getBitmapFromVectorDrawable(pressedResId, scale)
    }
    
    private fun getScaleForButton(): Float {
        // 按钮基础缩放
        return when (buttonId) {
            1, 2, 3, 4 -> 0.45f // ABXY 按钮
            5, 6 -> 0.35f // L, R 肩键
            7, 8 -> 0.35f // ZL, ZR 扳机键
            9, 10 -> 0.3f // +, - 按钮
            11, 12 -> 1f // L3, R3 摇杆按钮
            else -> 0.38f // 其他按钮默认
        }
    }
    
    private fun getBitmapFromVectorDrawable(drawableId: Int, baseScale: Float): Bitmap {
        val drawable = ContextCompat.getDrawable(context, drawableId) ?: 
            throw IllegalArgumentException("Drawable not found: $drawableId")
        
        val userScale = (individualScale.toFloat() + 50) / 100f
        val finalScale = baseScale * userScale
        
        val width = (drawable.intrinsicWidth * finalScale).toInt().takeIf { it > 0 } ?: 100
        val height = (drawable.intrinsicHeight * finalScale).toInt().takeIf { it > 0 } ?: 100
        
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        
        val canvas = Canvas(bitmap)
        drawable.setBounds(0, 0, width, height)
        drawable.draw(canvas)
        return bitmap
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val desiredWidth = defaultBitmap?.width ?: dpToPx(80)
        val desiredHeight = defaultBitmap?.height ?: dpToPx(80)
        
        val minSize = when (buttonId) {
            1, 2, 3, 4 -> dpToPx(70)
            5, 6 -> dpToPx(100)
            7, 8 -> dpToPx(100)
            9, 10 -> dpToPx(60)
            11, 12 -> dpToPx(80)
            else -> dpToPx(70)
        }
        
        val width = Math.max(desiredWidth, minSize)
        val height = Math.max(desiredHeight, minSize)
        
        setMeasuredDimension(width, height)
    }
    
    fun setPosition(x: Int, y: Int) {
        val params = layoutParams as? FrameLayout.LayoutParams ?: FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        )
        params.leftMargin = x - width / 2
        params.topMargin = y - height / 2
        layoutParams = params
    }
    
    fun getPosition(): Pair<Int, Int> {
        val params = layoutParams as? FrameLayout.LayoutParams ?: return Pair(0, 0)
        return Pair(params.leftMargin + width / 2, params.topMargin + height / 2)
    }
    
    fun setPressedState(pressed: Boolean) {
        buttonPressed = pressed
        invalidate()
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val paint = Paint().apply {
            alpha = opacity
            isFilterBitmap = true
        }
        
        val bitmap = if (buttonPressed) pressedBitmap else defaultBitmap
        bitmap?.let {
            val left = (width - it.width) / 2f
            val top = (height - it.height) / 2f
            canvas.drawBitmap(it, left, top, paint)
        }
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
}

// 数据类 - 添加组合按键配置
data class ButtonConfig(
    val id: Int,
    val text: String,
    val defaultX: Float,
    val defaultY: Float,
    val keyCode: Int,
    var enabled: Boolean = true,
    var opacity: Int = 100,
    var scale: Int = 50
)

data class JoystickConfig(
    val id: Int,
    val isLeft: Boolean,
    val defaultX: Float,
    val defaultY: Float,
    var enabled: Boolean = true,
    var opacity: Int = 100,
    var scale: Int = 50
)

data class DpadConfig(
    val id: Int,
    val defaultX: Float,
    val defaultY: Float,
    var enabled: Boolean = true,
    var opacity: Int = 100,
    var scale: Int = 50
)

data class CombinationConfig(
    val id: Int,
    val name: String,
    val keyCodes: List<Int>,
    val defaultX: Float,
    val defaultY: Float,
    var enabled: Boolean = true,
    var opacity: Int = 100,
    var scale: Int = 50
)

// 按键管理器 - 修改按钮初始位置，添加组合按键管理
class ButtonLayoutManager(private val context: Context) {
    private val prefs = context.getSharedPreferences("virtual_controls", Context.MODE_PRIVATE)
    
    // 修改按钮初始位置
    private val buttonConfigs = listOf(
        ButtonConfig(1, "A", 0.90f, 0.6f, GamePadButtonInputId.A.ordinal),
        ButtonConfig(2, "B", 0.85f, 0.7f, GamePadButtonInputId.B.ordinal),
        ButtonConfig(3, "X", 0.85f, 0.5f, GamePadButtonInputId.X.ordinal),
        ButtonConfig(4, "Y", 0.80f, 0.6f, GamePadButtonInputId.Y.ordinal),
        ButtonConfig(5, "L", 0.1f, 0.2f, GamePadButtonInputId.LeftShoulder.ordinal),
        ButtonConfig(6, "R", 0.9f, 0.2f, GamePadButtonInputId.RightShoulder.ordinal),
        ButtonConfig(7, "ZL", 0.1f, 0.1f, GamePadButtonInputId.LeftTrigger.ordinal),
        ButtonConfig(8, "ZR", 0.9f, 0.1f, GamePadButtonInputId.RightTrigger.ordinal),
        ButtonConfig(9, "+", 0.8f, 0.1f, GamePadButtonInputId.Plus.ordinal),
        ButtonConfig(10, "-", 0.2f, 0.1f, GamePadButtonInputId.Minus.ordinal),
        ButtonConfig(11, "L3", 0.2f, 0.2f, GamePadButtonInputId.LeftStickButton.ordinal),
        ButtonConfig(12, "R3", 0.8f, 0.2f, GamePadButtonInputId.RightStickButton.ordinal)
    )
    
    private val joystickConfigs = listOf(
        JoystickConfig(101, true, 0.2f, 0.7f),
        JoystickConfig(102, false, 0.7f, 0.7f)
    )
    
    private val dpadConfig = DpadConfig(201, 0.1f, 0.5f)
    
    // 组合按键配置 - 动态加载
    private val combinationConfigs = mutableListOf<CombinationConfig>()
    
    init {
        loadCombinationConfigs()
    }
    
    private fun loadCombinationConfigs() {
        combinationConfigs.clear()
        val combinationIds = prefs.getString("combination_ids", "") ?: ""
        
        if (combinationIds.isNotEmpty()) {
            val ids = combinationIds.split(",").mapNotNull { it.toIntOrNull() }
            for (id in ids) {
                val name = prefs.getString("combination_${id}_name", "组合${id}") ?: "组合${id}"
                val keyCount = prefs.getInt("combination_${id}_key_count", 0)
                val keyCodes = mutableListOf<Int>()
                
                for (j in 0 until keyCount) {
                    val keyCode = prefs.getInt("combination_${id}_key_${j}", -1)
                    if (keyCode != -1) {
                        keyCodes.add(keyCode)
                    }
                }
                
                val defaultX = prefs.getFloat("combination_${id}_default_x", 0.5f)
                val defaultY = prefs.getFloat("combination_${id}_default_y", 0.3f)
                val enabled = prefs.getBoolean("combination_${id}_enabled", true)
                val opacity = prefs.getInt("combination_${id}_opacity", 100)
                val scale = prefs.getInt("combination_${id}_scale", 50)
                
                combinationConfigs.add(
                    CombinationConfig(id, name, keyCodes, defaultX, defaultY, enabled, opacity, scale)
                )
            }
        }
    }
    
    fun getButtonPosition(buttonId: Int, containerWidth: Int, containerHeight: Int): Pair<Int, Int> {
        val xPref = prefs.getFloat("button_${buttonId}_x", -1f)
        val yPref = prefs.getFloat("button_${buttonId}_y", -1f)
        
        val config = buttonConfigs.find { it.id == buttonId } ?: return Pair(0, 0)
        
        val x = if (xPref != -1f) (xPref * containerWidth) else (config.defaultX * containerWidth)
        val y = if (yPref != -1f) (yPref * containerHeight) else (config.defaultY * containerHeight)
        
        return Pair(x.toInt(), y.toInt())
    }
    
    fun getJoystickPosition(joystickId: Int, containerWidth: Int, containerHeight: Int): Pair<Int, Int> {
        val xPref = prefs.getFloat("joystick_${joystickId}_x", -1f)
        val yPref = prefs.getFloat("joystick_${joystickId}_y", -1f)
        
        val config = joystickConfigs.find { it.id == joystickId } ?: return Pair(0, 0)
        
        val x = if (xPref != -1f) (xPref * containerWidth) else (config.defaultX * containerWidth)
        val y = if (yPref != -1f) (yPref * containerHeight) else (config.defaultY * containerHeight)
        
        return Pair(x.toInt(), y.toInt())
    }
    
    fun getDpadPosition(containerWidth: Int, containerHeight: Int): Pair<Int, Int> {
        val xPref = prefs.getFloat("dpad_x", -1f)
        val yPref = prefs.getFloat("dpad_y", -1f)
        
        val x = if (xPref != -1f) (xPref * containerWidth) else (dpadConfig.defaultX * containerWidth)
        val y = if (yPref != -1f) (yPref * containerHeight) else (dpadConfig.defaultY * containerHeight)
        
        return Pair(x.toInt(), y.toInt())
    }
    
    fun getCombinationPosition(combinationId: Int, containerWidth: Int, containerHeight: Int): Pair<Int, Int> {
        val xPref = prefs.getFloat("combination_${combinationId}_x", -1f)
        val yPref = prefs.getFloat("combination_${combinationId}_y", -1f)
        
        val config = combinationConfigs.find { it.id == combinationId } ?: return Pair(0, 0)
        
        val x = if (xPref != -1f) (xPref * containerWidth) else (config.defaultX * containerWidth)
        val y = if (yPref != -1f) (yPref * containerHeight) else (config.defaultY * containerHeight)
        
        return Pair(x.toInt(), y.toInt())
    }
    
    fun isButtonEnabled(buttonId: Int): Boolean {
        return prefs.getBoolean("button_${buttonId}_enabled", true)
    }
    
    fun isJoystickEnabled(joystickId: Int): Boolean {
        return prefs.getBoolean("joystick_${joystickId}_enabled", true)
    }
    
    fun isDpadEnabled(): Boolean {
        return prefs.getBoolean("dpad_enabled", true)
    }
    
    fun isCombinationEnabled(combinationId: Int): Boolean {
        return prefs.getBoolean("combination_${combinationId}_enabled", true)
    }
    
    fun getButtonOpacity(buttonId: Int): Int {
        return prefs.getInt("button_${buttonId}_opacity", 100)
    }
    
    fun getJoystickOpacity(joystickId: Int): Int {
        return prefs.getInt("joystick_${joystickId}_opacity", 100)
    }
    
    fun getDpadOpacity(): Int {
        return prefs.getInt("dpad_opacity", 100)
    }
    
    fun getCombinationOpacity(combinationId: Int): Int {
        return prefs.getInt("combination_${combinationId}_opacity", 100)
    }
    
    fun getButtonScale(buttonId: Int): Int {
        return prefs.getInt("button_${buttonId}_scale", 50)
    }
    
    fun getJoystickScale(joystickId: Int): Int {
        return prefs.getInt("joystick_${joystickId}_scale", 50)
    }
    
    fun getDpadScale(): Int {
        return prefs.getInt("dpad_scale", 50)
    }
    
    fun getCombinationScale(combinationId: Int): Int {
        return prefs.getInt("combination_${combinationId}_scale", 50)
    }
    
    fun saveButtonPosition(buttonId: Int, x: Int, y: Int, containerWidth: Int, containerHeight: Int) {
        if (containerWidth <= 0 || containerHeight <= 0) return
        
        val xNormalized = x.toFloat() / containerWidth
        val yNormalized = y.toFloat() / containerHeight
        
        prefs.edit()
            .putFloat("button_${buttonId}_x", xNormalized)
            .putFloat("button_${buttonId}_y", yNormalized)
            .apply()
    }
    
    fun saveJoystickPosition(joystickId: Int, x: Int, y: Int, containerWidth: Int, containerHeight: Int) {
        if (containerWidth <= 0 || containerHeight <= 0) return
        
        val xNormalized = x.toFloat() / containerWidth
        val yNormalized = y.toFloat() / containerHeight
        
        prefs.edit()
            .putFloat("joystick_${joystickId}_x", xNormalized)
            .putFloat("joystick_${joystickId}_y", yNormalized)
            .apply()
    }
    
    fun saveDpadPosition(x: Int, y: Int, containerWidth: Int, containerHeight: Int) {
        if (containerWidth <= 0 || containerHeight <= 0) return
        
        val xNormalized = x.toFloat() / containerWidth
        val yNormalized = y.toFloat() / containerHeight
        
        prefs.edit()
            .putFloat("dpad_x", xNormalized)
            .putFloat("dpad_y", yNormalized)
            .apply()
    }
    
    fun saveCombinationPosition(combinationId: Int, x: Int, y: Int, containerWidth: Int, containerHeight: Int) {
        if (containerWidth <= 0 || containerHeight <= 0) return
        
        val xNormalized = x.toFloat() / containerWidth
        val yNormalized = y.toFloat() / containerHeight
        
        prefs.edit()
            .putFloat("combination_${combinationId}_x", xNormalized)
            .putFloat("combination_${combinationId}_y", yNormalized)
            .apply()
    }
    
    fun setButtonEnabled(buttonId: Int, enabled: Boolean) {
        prefs.edit().putBoolean("button_${buttonId}_enabled", enabled).apply()
    }
    
    fun setJoystickEnabled(joystickId: Int, enabled: Boolean) {
        prefs.edit().putBoolean("joystick_${joystickId}_enabled", enabled).apply()
    }
    
    fun setDpadEnabled(enabled: Boolean) {
        prefs.edit().putBoolean("dpad_enabled", enabled).apply()
    }
    
    fun setCombinationEnabled(combinationId: Int, enabled: Boolean) {
        prefs.edit().putBoolean("combination_${combinationId}_enabled", enabled).apply()
    }
    
    fun setButtonOpacity(buttonId: Int, opacity: Int) {
        prefs.edit().putInt("button_${buttonId}_opacity", opacity.coerceIn(0, 100)).apply()
    }
    
    fun setJoystickOpacity(joystickId: Int, opacity: Int) {
        prefs.edit().putInt("joystick_${joystickId}_opacity", opacity.coerceIn(0, 100)).apply()
    }
    
    fun setDpadOpacity(opacity: Int) {
        prefs.edit().putInt("dpad_opacity", opacity.coerceIn(0, 100)).apply()
    }
    
    fun setCombinationOpacity(combinationId: Int, opacity: Int) {
        prefs.edit().putInt("combination_${combinationId}_opacity", opacity.coerceIn(0, 100)).apply()
    }
    
    fun setButtonScale(buttonId: Int, scale: Int) {
        prefs.edit().putInt("button_${buttonId}_scale", scale.coerceIn(10, 200)).apply()
    }
    
    fun setJoystickScale(joystickId: Int, scale: Int) {
        prefs.edit().putInt("joystick_${joystickId}_scale", scale.coerceIn(10, 200)).apply()
    }
    
    fun setDpadScale(scale: Int) {
        prefs.edit().putInt("dpad_scale", scale.coerceIn(10, 200)).apply()
    }
    
    fun setCombinationScale(combinationId: Int, scale: Int) {
        prefs.edit().putInt("combination_${combinationId}_scale", scale.coerceIn(10, 200)).apply()
    }
    
    // 组合按键管理方法 - 修复ID生成和存储问题
    fun createCombination(name: String, keyCodes: List<Int>): Int {
        // 使用递增的ID，确保每次重启后ID不重复
        val nextId = prefs.getInt("next_combination_id", 301)
        val newId = nextId
        
        // 保存下一个可用的ID
        prefs.edit().putInt("next_combination_id", nextId + 1).apply()
        
        // 获取现有的组合按键ID列表
        val existingIds = prefs.getString("combination_ids", "") ?: ""
        val idList = if (existingIds.isEmpty()) {
            mutableListOf<String>()
        } else {
            existingIds.split(",").toMutableList()
        }
        idList.add(newId.toString())
        prefs.edit().putString("combination_ids", idList.joinToString(",")).apply()

        val config = CombinationConfig(
            newId, 
            name, 
            keyCodes, 
            0.5f, 
            0.3f, 
            true, 
            100, 
            50
        )
        combinationConfigs.add(config)
        
        // 保存到 SharedPreferences
        val editor = prefs.edit()
        editor.putString("combination_${newId}_name", name)
        editor.putInt("combination_${newId}_key_count", keyCodes.size)
        keyCodes.forEachIndexed { index, keyCode ->
            editor.putInt("combination_${newId}_key_${index}", keyCode)
        }
        editor.putFloat("combination_${newId}_default_x", 0.5f)
        editor.putFloat("combination_${newId}_default_y", 0.3f)
        editor.putFloat("combination_${newId}_x", 0.5f) // 保存初始位置
        editor.putFloat("combination_${newId}_y", 0.3f) // 保存初始位置
        editor.putBoolean("combination_${newId}_enabled", true)
        editor.putInt("combination_${newId}_opacity", 100)
        editor.putInt("combination_${newId}_scale", 50)
        editor.apply()
        
        return newId
    }
    
    fun deleteCombination(combinationId: Int) {
        combinationConfigs.removeAll { it.id == combinationId }
        
        // 从ID列表中移除
        val existingIds = prefs.getString("combination_ids", "") ?: ""
        val idList = if (existingIds.isEmpty()) {
            mutableListOf<String>()
        } else {
            existingIds.split(",").toMutableList()
        }
        idList.remove(combinationId.toString())
        prefs.edit().putString("combination_ids", idList.joinToString(",")).apply()
        
        // 更新 SharedPreferences
        val editor = prefs.edit()
        
        // 移除该组合的所有数据
        for (i in 0 until 10) { // 假设最多10个按键
            editor.remove("combination_${combinationId}_key_${i}")
        }
        editor.remove("combination_${combinationId}_name")
        editor.remove("combination_${combinationId}_key_count")
        editor.remove("combination_${combinationId}_x")
        editor.remove("combination_${combinationId}_y")
        editor.remove("combination_${combinationId}_default_x")
        editor.remove("combination_${combinationId}_default_y")
        editor.remove("combination_${combinationId}_enabled")
        editor.remove("combination_${combinationId}_opacity")
        editor.remove("combination_${combinationId}_scale")
        editor.apply()
    }
    
    fun getAllButtonConfigs(): List<ButtonConfig> = buttonConfigs
    fun getAllJoystickConfigs(): List<JoystickConfig> = joystickConfigs
    fun getDpadConfig(): DpadConfig = dpadConfig
    fun getAllCombinationConfigs(): List<CombinationConfig> = combinationConfigs
}

// 修正的 GameController 类
class GameController(var activity: Activity) {

    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            
            val buttonContainer = view.findViewById<FrameLayout>(R.id.buttonContainer)!!
            val editModeContainer = view.findViewById<FrameLayout>(R.id.editModeContainer)!!
            
            controller.buttonLayoutManager = ButtonLayoutManager(context)
            controller.createVirtualControls(buttonContainer)
            controller.createEditButtons(editModeContainer)
            
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
            var isEditing by remember { mutableStateOf(false) }
            
            LaunchedEffect(isEditing) {
                viewModel.controller?.setEditingMode(isEditing)
            }
            
            AndroidView(
                modifier = Modifier.fillMaxSize(), 
                factory = { context ->
                    val controller = GameController(viewModel.activity)
                    val c = Create(context, controller)
                    controller.controllerView = c
                    viewModel.setGameController(controller)
                    controller.setVisible(QuickSettings(viewModel.activity).useVirtualController)
                    c
                }
            )
        }
    }

    private var controllerView: View? = null
    private var buttonContainer: FrameLayout? = null
    private var editModeContainer: FrameLayout? = null
    private var saveButton: Button? = null
    private var cancelButton: Button? = null
    private var buttonLayout: LinearLayout? = null
    var buttonLayoutManager: ButtonLayoutManager? = null
    private val virtualButtons = mutableMapOf<Int, ButtonOverlayView>()
    private val virtualJoysticks = mutableMapOf<Int, JoystickOverlayView>()
    private val virtualCombinations = mutableMapOf<Int, CombinationOverlayView>()
    private var dpadView: DpadOverlayView? = null
    var controllerId: Int = -1
    private var isEditing = false
    
    // 新增：统一触摸事件处理
    private var activeTouches = mutableMapOf<Int, MutableList<View>>() // 记录每个触摸点激活的控件

    val isVisible: Boolean
        get() {
            controllerView?.apply {
                return this.isVisible
            }
            return false
        }

    private fun createVirtualControls(buttonContainer: FrameLayout) {
        this.buttonContainer = buttonContainer
        val manager = buttonLayoutManager ?: return
        createControlsImmediately(buttonContainer, manager)
        
        // 添加统一触摸处理器
        setupUnifiedTouchHandler()
    }
    
    // 新增：设置统一触摸处理器
    private fun setupUnifiedTouchHandler() {
        buttonContainer?.setOnTouchListener { _, event ->
            handleUnifiedTouchEvent(event)
            true
        }
    }
    
    // 新增：统一触摸事件处理
    private fun handleUnifiedTouchEvent(event: MotionEvent) {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                handleTouchDown(event, 0)
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                val pointerIndex = event.actionIndex
                handleTouchDown(event, pointerIndex)
            }
            MotionEvent.ACTION_MOVE -> {
                for (pointerIndex in 0 until event.pointerCount) {
                    handleTouchMove(event, pointerIndex)
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                val pointerIndex = event.actionIndex
                handleTouchUp(event, pointerIndex)
            }
            MotionEvent.ACTION_CANCEL -> {
                handleTouchCancel()
            }
        }
    }
    
    // 新增：触摸按下处理
    private fun handleTouchDown(event: MotionEvent, pointerIndex: Int) {
        val pointerId = event.getPointerId(pointerIndex)
        val x = event.getX(pointerIndex)
        val y = event.getY(pointerIndex)
        
        val touchedViews = mutableListOf<View>()
        
        // 检查所有控件，找出被触摸的控件
        virtualButtons.values.forEach { button ->
            if (isPointInView(x, y, button) && button.isVisible) {
                touchedViews.add(button)
                // 立即触发按钮按下
                val config = buttonLayoutManager?.getAllButtonConfigs()?.find { it.id == button.buttonId }
                config?.let {
                    handleButtonPress(it.keyCode, button.buttonId, true)
                }
            }
        }
        
        virtualJoysticks.values.forEach { joystick ->
            if (isPointInView(x, y, joystick) && joystick.isVisible) {
                touchedViews.add(joystick)
                // 立即触发摇杆触摸开始
                val config = buttonLayoutManager?.getAllJoystickConfigs()?.find { it.id == joystick.stickId }
                config?.let {
                    handleJoystickTouchStart(event, pointerIndex, joystick.stickId, it.isLeft)
                }
            }
        }
        
        virtualCombinations.values.forEach { combination ->
            if (isPointInView(x, y, combination) && combination.isVisible) {
                touchedViews.add(combination)
                // 立即触发组合按键按下
                val config = buttonLayoutManager?.getAllCombinationConfigs()?.find { it.id == combination.combinationId }
                config?.let {
                    handleCombinationPress(it.keyCodes, combination.combinationId, true)
                }
            }
        }
        
        dpadView?.let { dpad ->
            if (isPointInView(x, y, dpad) && dpad.isVisible) {
                touchedViews.add(dpad)
                // 立即触发方向键触摸开始
                handleDpadTouchStart(event, pointerIndex)
            }
        }
        
        activeTouches[pointerId] = touchedViews
    }
    
    // 新增：触摸移动处理
    private fun handleTouchMove(event: MotionEvent, pointerIndex: Int) {
        val pointerId = event.getPointerId(pointerIndex)
        val x = event.getX(pointerIndex)
        val y = event.getY(pointerIndex)
        
        val activeViews = activeTouches[pointerId] ?: return
        
        activeViews.forEach { view ->
            when (view) {
                is ButtonOverlayView -> {
                    // 按钮在移动时保持按下状态
                    val config = buttonLayoutManager?.getAllButtonConfigs()?.find { it.id == view.buttonId }
                    config?.let {
                        // 如果移出按钮区域，则释放按钮
                        val stillInView = isPointInView(x, y, view)
                        if (!stillInView) {
                            handleButtonPress(it.keyCode, view.buttonId, false)
                        }
                    }
                }
                is JoystickOverlayView -> {
                    // 更新摇杆位置
                    val config = buttonLayoutManager?.getAllJoystickConfigs()?.find { it.id == view.stickId }
                    config?.let {
                        handleJoystickTouchMove(event, pointerIndex, view.stickId, it.isLeft)
                    }
                }
                is CombinationOverlayView -> {
                    // 组合按键在移动时保持按下状态
                    val config = buttonLayoutManager?.getAllCombinationConfigs()?.find { it.id == view.combinationId }
                    config?.let {
                        // 如果移出组合按键区域，则释放
                        val stillInView = isPointInView(x, y, view)
                        if (!stillInView) {
                            handleCombinationPress(it.keyCodes, view.combinationId, false)
                        }
                    }
                }
                is DpadOverlayView -> {
                    // 更新方向键
                    handleDpadTouchMove(event, pointerIndex)
                }
            }
        }
    }
    
    // 新增：触摸抬起处理
    private fun handleTouchUp(event: MotionEvent, pointerIndex: Int) {
        val pointerId = event.getPointerId(pointerIndex)
        
        val activeViews = activeTouches[pointerId] ?: return
        
        activeViews.forEach { view ->
            when (view) {
                is ButtonOverlayView -> {
                    val config = buttonLayoutManager?.getAllButtonConfigs()?.find { it.id == view.buttonId }
                    config?.let {
                        handleButtonPress(it.keyCode, view.buttonId, false)
                    }
                }
                is JoystickOverlayView -> {
                    val config = buttonLayoutManager?.getAllJoystickConfigs()?.find { it.id == view.stickId }
                    config?.let {
                        handleJoystickTouchEnd(view.stickId, it.isLeft)
                    }
                }
                is CombinationOverlayView -> {
                    val config = buttonLayoutManager?.getAllCombinationConfigs()?.find { it.id == view.combinationId }
                    config?.let {
                        handleCombinationPress(it.keyCodes, view.combinationId, false)
                    }
                }
                is DpadOverlayView -> {
                    handleDpadTouchEnd()
                }
            }
        }
        
        activeTouches.remove(pointerId)
    }
    
    // 新增：触摸取消处理
    private fun handleTouchCancel() {
        // 取消所有激活的触摸
        activeTouches.values.forEach { views ->
            views.forEach { view ->
                when (view) {
                    is ButtonOverlayView -> {
                        val config = buttonLayoutManager?.getAllButtonConfigs()?.find { it.id == view.buttonId }
                        config?.let {
                            handleButtonPress(it.keyCode, view.buttonId, false)
                        }
                    }
                    is JoystickOverlayView -> {
                        val config = buttonLayoutManager?.getAllJoystickConfigs()?.find { it.id == view.stickId }
                        config?.let {
                            handleJoystickTouchEnd(view.stickId, it.isLeft)
                        }
                    }
                    is CombinationOverlayView -> {
                        val config = buttonLayoutManager?.getAllCombinationConfigs()?.find { it.id == view.combinationId }
                        config?.let {
                            handleCombinationPress(it.keyCodes, view.combinationId, false)
                        }
                    }
                    is DpadOverlayView -> {
                        handleDpadTouchEnd()
                    }
                }
            }
        }
        activeTouches.clear()
    }
    
    // 新增：判断触摸点是否在视图内
    private fun isPointInView(x: Float, y: Float, view: View): Boolean {
        val location = IntArray(2)
        view.getLocationOnScreen(location)
        val left = location[0]
        val top = location[1]
        val right = left + view.width
        val bottom = top + view.height
        
        val containerLocation = IntArray(2)
        buttonContainer?.getLocationOnScreen(containerLocation)
        val screenX = x + containerLocation[0]
        val screenY = y + containerLocation[1]
        
        return screenX >= left && screenX <= right && screenY >= top && screenY <= bottom
    }
    
    // 新增：按钮按下处理
    private fun handleButtonPress(keyCode: Int, buttonId: Int, pressed: Boolean) {
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        }
        
        virtualButtons[buttonId]?.setPressedState(pressed)
        
        if (pressed) {
            RyujinxNative.jnaInstance.inputSetButtonPressed(keyCode, controllerId)
        } else {
            RyujinxNative.jnaInstance.inputSetButtonReleased(keyCode, controllerId)
        }
    }
    
    // 新增：组合按键按下处理
    private fun handleCombinationPress(keyCodes: List<Int>, combinationId: Int, pressed: Boolean) {
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        }
        
        virtualCombinations[combinationId]?.setPressedState(pressed)
        
        keyCodes.forEach { keyCode ->
            val actualKeyCode = when (keyCode) {
                0 -> GamePadButtonInputId.A.ordinal
                1 -> GamePadButtonInputId.B.ordinal
                2 -> GamePadButtonInputId.X.ordinal
                3 -> GamePadButtonInputId.Y.ordinal
                4 -> GamePadButtonInputId.LeftShoulder.ordinal
                5 -> GamePadButtonInputId.RightShoulder.ordinal
                6 -> GamePadButtonInputId.LeftTrigger.ordinal
                7 -> GamePadButtonInputId.RightTrigger.ordinal
                8 -> GamePadButtonInputId.Plus.ordinal
                9 -> GamePadButtonInputId.Minus.ordinal
                10 -> GamePadButtonInputId.LeftStickButton.ordinal
                11 -> GamePadButtonInputId.RightStickButton.ordinal
                12 -> GamePadButtonInputId.DpadUp.ordinal
                13 -> GamePadButtonInputId.DpadDown.ordinal
                14 -> GamePadButtonInputId.DpadLeft.ordinal
                15 -> GamePadButtonInputId.DpadRight.ordinal
                else -> keyCode
            }
            if (pressed) {
                RyujinxNative.jnaInstance.inputSetButtonPressed(actualKeyCode, controllerId)
            } else {
                RyujinxNative.jnaInstance.inputSetButtonReleased(actualKeyCode, controllerId)
            }
        }
    }
    
    // 新增：摇杆触摸开始
    private fun handleJoystickTouchStart(event: MotionEvent, pointerIndex: Int, joystickId: Int, isLeftStick: Boolean) {
        virtualJoysticks[joystickId]?.updateStickPosition(0f, 0f, true)
    }
    
    // 新增：摇杆触摸移动
    private fun handleJoystickTouchMove(event: MotionEvent, pointerIndex: Int, joystickId: Int, isLeftStick: Boolean) {
        virtualJoysticks[joystickId]?.let { joystick ->
            val centerX = joystick.width / 2f
            val centerY = joystick.height / 2f
            
            val x = event.getX(pointerIndex) - (joystick.left + centerX)
            val y = event.getY(pointerIndex) - (joystick.top + centerY)
            
            val maxDistance = centerX * 0.8f
            val normalizedX = MathUtils.clamp(x / maxDistance, -1f, 1f)
            val normalizedY = MathUtils.clamp(y / maxDistance, -1f, 1f)
            
            joystick.updateStickPosition(normalizedX, normalizedY, true)
            
            val setting = QuickSettings(activity)
            val sensitivity = setting.controllerStickSensitivity
            
            val adjustedX = MathUtils.clamp(normalizedX * sensitivity, -1f, 1f)
            val adjustedY = MathUtils.clamp(normalizedY * sensitivity, -1f, 1f)
            
            if (isLeftStick) {
                RyujinxNative.jnaInstance.inputSetStickAxis(1, adjustedX, -adjustedY, controllerId)
            } else {
                RyujinxNative.jnaInstance.inputSetStickAxis(2, adjustedX, -adjustedY, controllerId)
            }
        }
    }
    
    // 新增：摇杆触摸结束
    private fun handleJoystickTouchEnd(joystickId: Int, isLeftStick: Boolean) {
        virtualJoysticks[joystickId]?.updateStickPosition(0f, 0f, false)
        
        if (isLeftStick) {
            RyujinxNative.jnaInstance.inputSetStickAxis(1, 0f, 0f, controllerId)
        } else {
            RyujinxNative.jnaInstance.inputSetStickAxis(2, 0f, 0f, controllerId)
        }
    }
    
    // 新增：方向键触摸开始
    private fun handleDpadTouchStart(event: MotionEvent, pointerIndex: Int) {
        dpadView?.let { dpad ->
            val x = event.getX(pointerIndex) - dpad.left
            val y = event.getY(pointerIndex) - dpad.top
            val direction = dpad.getDirectionFromTouch(x, y)
            if (dpad.currentDirection != direction) {
                handleDpadDirection(dpad.currentDirection, false)
                dpad.currentDirection = direction
                dpad.updateDirection(direction)
                handleDpadDirection(direction, true)
            }
        }
    }
    
    // 新增：方向键触摸移动
    private fun handleDpadTouchMove(event: MotionEvent, pointerIndex: Int) {
        dpadView?.let { dpad ->
            val x = event.getX(pointerIndex) - dpad.left
            val y = event.getY(pointerIndex) - dpad.top
            val direction = dpad.getDirectionFromTouch(x, y)
            if (dpad.currentDirection != direction) {
                handleDpadDirection(dpad.currentDirection, false)
                dpad.currentDirection = direction
                dpad.updateDirection(direction)
                handleDpadDirection(direction, true)
            }
        }
    }
    
    // 新增：方向键触摸结束
    private fun handleDpadTouchEnd() {
        handleDpadDirection(dpadView?.currentDirection ?: DpadOverlayView.DpadDirection.NONE, false)
        dpadView?.currentDirection = DpadOverlayView.DpadDirection.NONE
        dpadView?.updateDirection(DpadOverlayView.DpadDirection.NONE)
    }
    
    private fun createControlsImmediately(buttonContainer: FrameLayout, manager: ButtonLayoutManager) {
        val containerWidth = buttonContainer.width
        val containerHeight = buttonContainer.height
        
        // 创建摇杆 - 传递 individualScale 参数
        manager.getAllJoystickConfigs().forEach { config ->
            val isEnabled = manager.isJoystickEnabled(config.id)
            val joystick = JoystickOverlayView(
                buttonContainer.context,
                individualScale = manager.getJoystickScale(config.id) // 传递 individualScale
            ).apply {
                stickId = config.id
                isLeftStick = config.isLeft
                opacity = (manager.getJoystickOpacity(config.id) * 255 / 100)
                
                // 修复：确保启用状态正确应用
                isVisible = isEnabled
                
                // 不在这里设置位置，统一在 refreshControlPositions 中设置
                
                // 移除原有的触摸监听器，使用统一触摸处理
            }
            
            buttonContainer.addView(joystick)
            virtualJoysticks[config.id] = joystick
        }
        
        // 创建方向键 - 传递 individualScale 参数
        val isDpadEnabled = manager.isDpadEnabled()
        dpadView = DpadOverlayView(
            buttonContainer.context,
            individualScale = manager.getDpadScale() // 传递 individualScale
        ).apply {
            opacity = (manager.getDpadOpacity() * 255 / 100)
            
            // 修复：确保启用状态正确应用
            isVisible = isDpadEnabled
            
            // 不在这里设置位置，统一在 refreshControlPositions 中设置
            
            // 移除原有的触摸监听器，使用统一触摸处理
        }
        buttonContainer.addView(dpadView)
        
        // 创建按钮 - 传递 individualScale 参数
        manager.getAllButtonConfigs().forEach { config ->
            val isEnabled = manager.isButtonEnabled(config.id)
            val button = ButtonOverlayView(
                buttonContainer.context,
                individualScale = manager.getButtonScale(config.id) // 传递 individualScale
            ).apply {
                buttonId = config.id
                buttonText = config.text
                opacity = (manager.getButtonOpacity(config.id) * 255 / 100)
                
                // 修复：确保启用状态正确应用
                isVisible = isEnabled
                
                when (config.id) {
                    1 -> setBitmaps(R.drawable.facebutton_a, R.drawable.facebutton_a_depressed)
                    2 -> setBitmaps(R.drawable.facebutton_b, R.drawable.facebutton_b_depressed)
                    3 -> setBitmaps(R.drawable.facebutton_x, R.drawable.facebutton_x_depressed)
                    4 -> setBitmaps(R.drawable.facebutton_y, R.drawable.facebutton_y_depressed)
                    5 -> setBitmaps(R.drawable.l_shoulder, R.drawable.l_shoulder_depressed)
                    6 -> setBitmaps(R.drawable.r_shoulder, R.drawable.r_shoulder_depressed)
                    7 -> setBitmaps(R.drawable.zl_trigger, R.drawable.zl_trigger_depressed)
                    8 -> setBitmaps(R.drawable.zr_trigger, R.drawable.zr_trigger_depressed)
                    9 -> setBitmaps(R.drawable.facebutton_plus, R.drawable.facebutton_plus_depressed)
                    10 -> setBitmaps(R.drawable.facebutton_minus, R.drawable.facebutton_minus_depressed)
                    11 -> setBitmaps(R.drawable.button_l3, R.drawable.button_l3_depressed)
                    12 -> setBitmaps(R.drawable.button_r3, R.drawable.button_r3_depressed)
                }
                
                // 不在这里设置位置，统一在 refreshControlPositions 中设置
                
                // 移除原有的触摸监听器，使用统一触摸处理
            }
            
            buttonContainer.addView(button)
            virtualButtons[config.id] = button
        }
        
        // 创建组合按键
        manager.getAllCombinationConfigs().forEach { config ->
            val isEnabled = manager.isCombinationEnabled(config.id)
            val combination = CombinationOverlayView(
                buttonContainer.context,
                individualScale = manager.getCombinationScale(config.id)
            ).apply {
                combinationId = config.id
                combinationName = config.name  // 使用自定义名称
                combinationKeys = config.keyCodes
                opacity = (manager.getCombinationOpacity(config.id) * 255 / 100)
                
                // 修复：确保启用状态正确应用
                isVisible = isEnabled
                
                // 不在这里设置位置，统一在 refreshControlPositions 中设置
                
                // 移除原有的触摸监听器，使用统一触摸处理
            }
            
            buttonContainer.addView(combination)
            virtualCombinations[config.id] = combination
        }
        
        // 统一设置位置
        refreshControlPositions()
        
        // 如果容器尺寸为0，延迟刷新位置
        if (containerWidth <= 0 || containerHeight <= 0) {
            buttonContainer.post {
                refreshControlPositions()
            }
        }
    }
    
    fun refreshControls() {
        val manager = buttonLayoutManager ?: return
        val buttonContainer = this.buttonContainer ?: return
        
        // 获取当前容器尺寸
        val containerWidth = buttonContainer.width
        val containerHeight = buttonContainer.height
        
        // 清除现有控件
        virtualButtons.values.forEach { buttonContainer.removeView(it) }
        virtualJoysticks.values.forEach { buttonContainer.removeView(it) }
        virtualCombinations.values.forEach { buttonContainer.removeView(it) }
        dpadView?.let { buttonContainer.removeView(it) }
        
        virtualButtons.clear()
        virtualJoysticks.clear()
        virtualCombinations.clear()
        dpadView = null
        
        // 重新创建控件，使用相同的逻辑
        createControlsImmediately(buttonContainer, manager)
    }
    
    // 修改后的 setControlEnabled 方法 - 只更新单个控件而不是重建所有控件
    fun setControlEnabled(controlId: Int, enabled: Boolean) {
        when {
            controlId in 1..12 -> {
                buttonLayoutManager?.setButtonEnabled(controlId, enabled)
                // 只更新单个按钮
                updateSingleButtonEnabled(controlId, enabled)
            }
            controlId in 101..102 -> {
                buttonLayoutManager?.setJoystickEnabled(controlId, enabled)
                // 只更新单个摇杆
                updateSingleJoystickEnabled(controlId, enabled)
            }
            controlId == 201 -> {
                buttonLayoutManager?.setDpadEnabled(enabled)
                // 只更新方向键
                updateSingleDpadEnabled(enabled)
            }
            controlId >= 300 -> {
                buttonLayoutManager?.setCombinationEnabled(controlId, enabled)
                // 只更新组合按键
                updateSingleCombinationEnabled(controlId, enabled)
            }
        }
    }
    
    // 新增方法：更新单个按钮的启用状态
    private fun updateSingleButtonEnabled(buttonId: Int, enabled: Boolean) {
        val button = virtualButtons[buttonId] ?: return
        
        // 更新单个按钮的可见性
        button.isVisible = enabled
        
        // 如果禁用按钮，确保按钮状态重置
        if (!enabled) {
            button.setPressedState(false)
            // 发送释放事件，确保不会卡住按键状态
            RyujinxNative.jnaInstance.inputSetButtonReleased(
                buttonLayoutManager?.getAllButtonConfigs()?.find { it.id == buttonId }?.keyCode ?: 0, 
                controllerId
            )
        }
    }
    
    // 新增方法：更新单个摇杆的启用状态
    private fun updateSingleJoystickEnabled(joystickId: Int, enabled: Boolean) {
        val joystick = virtualJoysticks[joystickId] ?: return
        
        // 更新单个摇杆的可见性
        joystick.isVisible = enabled
        
        // 如果禁用摇杆，确保摇杆状态重置
        if (!enabled) {
            joystick.updateStickPosition(0f, 0f, false)
            // 发送归零事件，确保不会卡住摇杆状态
            val config = buttonLayoutManager?.getAllJoystickConfigs()?.find { it.id == joystickId }
            if (config != null) {
                if (config.isLeft) {
                    RyujinxNative.jnaInstance.inputSetStickAxis(1, 0f, 0f, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetStickAxis(2, 0f, 0f, controllerId)
                }
            }
        }
    }
    
    // 新增方法：更新单个方向键的启用状态
    private fun updateSingleDpadEnabled(enabled: Boolean) {
        val dpad = dpadView ?: return
        
        // 更新方向键的可见性
        dpad.isVisible = enabled
        
        // 如果禁用方向键，确保方向键状态重置
        if (!enabled) {
            dpad.currentDirection = DpadOverlayView.DpadDirection.NONE
            dpad.updateDirection(DpadOverlayView.DpadDirection.NONE)
            // 发送释放所有方向的事件，确保不会卡住方向键状态
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controllerId)
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controllerId)
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controllerId)
        }
    }
    
    // 新增方法：更新单个组合按键的启用状态
    private fun updateSingleCombinationEnabled(combinationId: Int, enabled: Boolean) {
        val combination = virtualCombinations[combinationId] ?: return
        
        // 更新组合按键的可见性
        combination.isVisible = enabled
        
        // 如果禁用组合按键，确保状态重置
        if (!enabled) {
            combination.setPressedState(false)
            // 发送释放所有按键的事件
            val config = buttonLayoutManager?.getAllCombinationConfigs()?.find { it.id == combinationId }
            config?.keyCodes?.forEach { keyCode ->
                RyujinxNative.jnaInstance.inputSetButtonReleased(keyCode, controllerId)
            }
        }
    }
    
    fun setControlOpacity(controlId: Int, opacity: Int) {
        when {
            controlId in 1..12 -> {
                buttonLayoutManager?.setButtonOpacity(controlId, opacity)
                // 只更新单个按钮的透明度
                virtualButtons[controlId]?.opacity = (opacity * 255 / 100)
                virtualButtons[controlId]?.invalidate()
            }
            controlId in 101..102 -> {
                buttonLayoutManager?.setJoystickOpacity(controlId, opacity)
                // 只更新单个摇杆的透明度
                virtualJoysticks[controlId]?.opacity = (opacity * 255 / 100)
                virtualJoysticks[controlId]?.invalidate()
            }
            controlId == 201 -> {
                buttonLayoutManager?.setDpadOpacity(opacity)
                // 只更新方向键的透明度
                dpadView?.opacity = (opacity * 255 / 100)
                dpadView?.invalidate()
            }
            controlId >= 300 -> {
                buttonLayoutManager?.setCombinationOpacity(controlId, opacity)
                // 只更新组合按键的透明度
                virtualCombinations[controlId]?.opacity = (opacity * 255 / 100)
                virtualCombinations[controlId]?.invalidate()
            }
        }
    }
    
    // 修改后的 setControlScale 方法 - 只更新单个控件而不是重建所有控件
    fun setControlScale(controlId: Int, scale: Int) {
        when {
            controlId in 1..12 -> {
                buttonLayoutManager?.setButtonScale(controlId, scale)
                // 只更新单个按钮
                updateSingleButtonScale(controlId, scale)
            }
            controlId in 101..102 -> {
                buttonLayoutManager?.setJoystickScale(controlId, scale)
                // 只更新单个摇杆
                updateSingleJoystickScale(controlId, scale)
            }
            controlId == 201 -> {
                buttonLayoutManager?.setDpadScale(scale)
                // 只更新方向键
                updateSingleDpadScale(scale)
            }
            controlId >= 300 -> {
                buttonLayoutManager?.setCombinationScale(controlId, scale)
                // 只更新组合按键
                updateSingleCombinationScale(controlId, scale)
            }
        }
        // 刷新位置确保控件正确布局
        refreshControlPositions()
    }
    
    // 新增方法：更新单个按钮的缩放
    private fun updateSingleButtonScale(buttonId: Int, scale: Int) {
        val button = virtualButtons[buttonId] ?: return
        val manager = buttonLayoutManager ?: return
        
        // 更新单个按钮的缩放
        button.individualScale = scale
        
        // 重新加载位图
        when (buttonId) {
            1 -> button.setBitmaps(R.drawable.facebutton_a, R.drawable.facebutton_a_depressed)
            2 -> button.setBitmaps(R.drawable.facebutton_b, R.drawable.facebutton_b_depressed)
            3 -> button.setBitmaps(R.drawable.facebutton_x, R.drawable.facebutton_x_depressed)
            4 -> button.setBitmaps(R.drawable.facebutton_y, R.drawable.facebutton_y_depressed)
            5 -> button.setBitmaps(R.drawable.l_shoulder, R.drawable.l_shoulder_depressed)
            6 -> button.setBitmaps(R.drawable.r_shoulder, R.drawable.r_shoulder_depressed)
            7 -> button.setBitmaps(R.drawable.zl_trigger, R.drawable.zl_trigger_depressed)
            8 -> button.setBitmaps(R.drawable.zr_trigger, R.drawable.zr_trigger_depressed)
            9 -> button.setBitmaps(R.drawable.facebutton_plus, R.drawable.facebutton_plus_depressed)
            10 -> button.setBitmaps(R.drawable.facebutton_minus, R.drawable.facebutton_minus_depressed)
            11 -> button.setBitmaps(R.drawable.button_l3, R.drawable.button_l3_depressed)
            12 -> button.setBitmaps(R.drawable.button_r3, R.drawable.button_r3_depressed)
        }
        
        // 请求重新测量和绘制
        button.requestLayout()
        button.invalidate()
    }
    
    // 新增方法：更新单个摇杆的缩放
    private fun updateSingleJoystickScale(joystickId: Int, scale: Int) {
        val joystick = virtualJoysticks[joystickId] ?: return
        
        // 更新单个摇杆的缩放
        joystick.individualScale = scale
        
        // 重新加载位图 - 现在可以访问 public 的 loadBitmaps 方法
        joystick.loadBitmaps()
        
        // 请求重新测量和绘制
        joystick.requestLayout()
        joystick.invalidate()
    }
    
    // 新增方法：更新单个方向键的缩放
    private fun updateSingleDpadScale(scale: Int) {
        val dpad = dpadView ?: return
        
        // 更新方向键的缩放
        dpad.individualScale = scale
        
        // 重新加载位图 - 现在可以访问 public 的 loadBitmaps 方法
        dpad.loadBitmaps()
        
        // 请求重新测量和绘制
        dpad.requestLayout()
        dpad.invalidate()
    }
    
    // 新增方法：更新单个组合按键的缩放
    private fun updateSingleCombinationScale(combinationId: Int, scale: Int) {
        val combination = virtualCombinations[combinationId] ?: return
        
        // 更新组合按键的缩放
        combination.individualScale = scale
        
        // 重新加载位图
        combination.loadBitmaps()
        
        // 请求重新测量和绘制
        combination.requestLayout()
        combination.invalidate()
    }
    
    fun getControlScale(controlId: Int): Int {
        return when {
            controlId in 1..12 -> buttonLayoutManager?.getButtonScale(controlId) ?: 50
            controlId in 101..102 -> buttonLayoutManager?.getJoystickScale(controlId) ?: 50
            controlId == 201 -> buttonLayoutManager?.getDpadScale() ?: 50
            controlId >= 300 -> buttonLayoutManager?.getCombinationScale(controlId) ?: 50
            else -> 50
        }
    }
    
    fun getControlOpacity(controlId: Int): Int {
        return when {
            controlId in 1..12 -> buttonLayoutManager?.getButtonOpacity(controlId) ?: 100
            controlId in 101..102 -> buttonLayoutManager?.getJoystickOpacity(controlId) ?: 100
            controlId == 201 -> buttonLayoutManager?.getDpadOpacity() ?: 100
            controlId >= 300 -> buttonLayoutManager?.getCombinationOpacity(controlId) ?: 100
            else -> 100
        }
    }
    
    fun isControlEnabled(controlId: Int): Boolean {
        return when {
            controlId in 1..12 -> buttonLayoutManager?.isButtonEnabled(controlId) ?: true
            controlId in 101..102 -> buttonLayoutManager?.isJoystickEnabled(controlId) ?: true
            controlId == 201 -> buttonLayoutManager?.isDpadEnabled() ?: true
            controlId >= 300 -> buttonLayoutManager?.isCombinationEnabled(controlId) ?: true
            else -> true
        }
    }
    
    // 组合按键管理方法
    fun createCombination(name: String, keyCodes: List<Int>): Int {
        val manager = buttonLayoutManager ?: return -1
        val newId = manager.createCombination(name, keyCodes)
        
        // 刷新控件以显示新的组合按键
        refreshControls()
        
        return newId
    }
    
    fun deleteCombination(combinationId: Int) {
        val manager = buttonLayoutManager ?: return
        manager.deleteCombination(combinationId)
        
        // 刷新控件以移除组合按键
        refreshControls()
    }
    
    fun getAllCombinations(): List<CombinationConfig> {
        return buttonLayoutManager?.getAllCombinationConfigs() ?: emptyList()
    }
    
    private fun refreshControlPositions() {
        val manager = buttonLayoutManager ?: return
        val buttonContainer = this.buttonContainer ?: return
        
        val containerWidth = buttonContainer.width
        val containerHeight = buttonContainer.height
        
        if (containerWidth <= 0 || containerHeight <= 0) return
        
        // 统一使用布局管理器读取位置
        virtualJoysticks.forEach { (joystickId, joystick) ->
            val (x, y) = manager.getJoystickPosition(joystickId, containerWidth, containerHeight)
            joystick.setPosition(x, y)
        }
        
        dpadView?.let { dpad ->
            val (x, y) = manager.getDpadPosition(containerWidth, containerHeight)
            dpad.setPosition(x, y)
        }
        
        virtualButtons.forEach { (buttonId, button) ->
            val (x, y) = manager.getButtonPosition(buttonId, containerWidth, containerHeight)
            button.setPosition(x, y)
        }
        
        virtualCombinations.forEach { (combinationId, combination) ->
            val (x, y) = manager.getCombinationPosition(combinationId, containerWidth, containerHeight)
            combination.setPosition(x, y)
        }
    }
    
    private fun createEditButtons(editModeContainer: FrameLayout) {
    this.editModeContainer = editModeContainer
    
    // 创建圆角矩形背景的方法
    fun createRoundedRectDrawable(color: Int, cornerRadius: Float): Drawable {
        val shape = GradientDrawable()
        shape.shape = GradientDrawable.RECTANGLE
        shape.cornerRadius = cornerRadius
        shape.setColor(color)
        return shape
    }
    
    // 创建水平布局容器
    buttonLayout = LinearLayout(editModeContainer.context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = android.view.Gravity.CENTER
        
        // 创建保存按钮 - 圆角矩形背景
        saveButton = Button(editModeContainer.context).apply {
            text = "保存布局"
            background = createRoundedRectDrawable(
                Color.argb(200, 0, 150, 0), 
                dpToPx(12).toFloat() // 12dp圆角
            )
            setTextColor(Color.WHITE)
            textSize = 14f
            setOnClickListener {
                saveLayout()
                setEditingMode(false)
            }
            
            val params = LinearLayout.LayoutParams(
                dpToPx(120),
                dpToPx(60)
            ).apply {
                marginEnd = dpToPx(20)
            }
            layoutParams = params
        }
        
        // 创建取消按钮 - 圆角矩形背景
        cancelButton = Button(editModeContainer.context).apply {
            text = "取消"
            background = createRoundedRectDrawable(
                Color.argb(200, 200, 0, 0), 
                dpToPx(12).toFloat() // 12dp圆角
            )
            setTextColor(Color.WHITE)
            textSize = 14f
            setOnClickListener {
                setEditingMode(false)
                refreshControlPositions()
            }
            
            val params = LinearLayout.LayoutParams(
                dpToPx(120),
                dpToPx(60)
            ).apply {
                marginStart = dpToPx(20)
            }
            layoutParams = params
        }
        
        addView(saveButton)
        addView(cancelButton)
    }
    
    val containerParams = FrameLayout.LayoutParams(
        FrameLayout.LayoutParams.WRAP_CONTENT,
        FrameLayout.LayoutParams.WRAP_CONTENT
    ).apply {
        gravity = android.view.Gravity.CENTER
    }
    buttonLayout?.layoutParams = containerParams
    editModeContainer.addView(buttonLayout)
    
    editModeContainer.setBackgroundColor(Color.argb(100, 0, 0, 0)) // 调整为更亮的背景
    editModeContainer.isVisible = false
}
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            activity.resources.displayMetrics
        ).toInt()
    }
    
    // 保留原有的拖拽事件处理方法，用于编辑模式
    private fun handleJoystickDragEvent(event: MotionEvent, joystickId: Int): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {}
            MotionEvent.ACTION_MOVE -> {
                virtualJoysticks[joystickId]?.let { joystick ->
                    val parent = joystick.parent as? ViewGroup ?: return@let
                    val x = event.rawX.toInt() - parent.left
                    val y = event.rawY.toInt() - parent.top
                    
                    val clampedX = MathUtils.clamp(x, 0, parent.width)
                    val clampedY = MathUtils.clamp(y, 0, parent.height)
                    
                    joystick.setPosition(clampedX, clampedY)
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {}
        }
        return true
    }
    
    private fun handleDpadDragEvent(event: MotionEvent): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {}
            MotionEvent.ACTION_MOVE -> {
                dpadView?.let { dpad ->
                    val parent = dpad.parent as? ViewGroup ?: return@let
                    val x = event.rawX.toInt() - parent.left
                    val y = event.rawY.toInt() - parent.top
                    
                    val clampedX = MathUtils.clamp(x, 0, parent.width)
                    val clampedY = MathUtils.clamp(y, 0, parent.height)
                    
                    dpad.setPosition(clampedX, clampedY)
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {}
        }
        return true
    }
    
    private fun handleButtonDragEvent(event: MotionEvent, buttonId: Int): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {}
            MotionEvent.ACTION_MOVE -> {
                virtualButtons[buttonId]?.let { button ->
                    val parent = button.parent as? ViewGroup ?: return@let
                    val x = event.rawX.toInt() - parent.left
                    val y = event.rawY.toInt() - parent.top
                    
                    val clampedX = MathUtils.clamp(x, 0, parent.width)
                    val clampedY = MathUtils.clamp(y, 0, parent.height)
                    
                    button.setPosition(clampedX, clampedY)
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {}
        }
        return true
    }
    
    // 新增方法：处理组合按键拖拽事件
    private fun handleCombinationDragEvent(event: MotionEvent, combinationId: Int): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {}
            MotionEvent.ACTION_MOVE -> {
                virtualCombinations[combinationId]?.let { combination ->
                    val parent = combination.parent as? ViewGroup ?: return@let
                    val x = event.rawX.toInt() - parent.left
                    val y = event.rawY.toInt() - parent.top
                    
                    val clampedX = MathUtils.clamp(x, 0, parent.width)
                    val clampedY = MathUtils.clamp(y, 0, parent.height)
                    
                    combination.setPosition(clampedX, clampedY)
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {}
        }
        return true
    }
    
    // 方向键方向处理
    private fun handleDpadDirection(direction: DpadOverlayView.DpadDirection, pressed: Boolean) {
        when (direction) {
            DpadOverlayView.DpadDirection.UP -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                }
            }
            DpadOverlayView.DpadDirection.DOWN -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                }
            }
            DpadOverlayView.DpadDirection.LEFT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                }
            }
            DpadOverlayView.DpadDirection.RIGHT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                }
            }
            DpadOverlayView.DpadDirection.UP_LEFT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                }
            }
            DpadOverlayView.DpadDirection.UP_RIGHT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                }
            }
            DpadOverlayView.DpadDirection.DOWN_LEFT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                }
            }
            DpadOverlayView.DpadDirection.DOWN_RIGHT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                }
            }
            else -> {
                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controllerId)
            }
        }
    }
    
    fun setEditingMode(editing: Boolean) {
        isEditing = editing
        editModeContainer?.isVisible = editing
        
        virtualButtons.values.forEach { button ->
            button.setPressedState(false)
        }
        virtualJoysticks.values.forEach { joystick ->
            joystick.updateStickPosition(0f, 0f, false)
        }
        virtualCombinations.values.forEach { combination ->
            combination.setPressedState(false)
        }
        dpadView?.currentDirection = DpadOverlayView.DpadDirection.NONE
        dpadView?.updateDirection(DpadOverlayView.DpadDirection.NONE)
    }
    
    fun saveLayout() {
        val manager = buttonLayoutManager ?: return
        val buttonContainer = this.buttonContainer ?: return
        
        val containerWidth = buttonContainer.width
        val containerHeight = buttonContainer.height
        
        if (containerWidth <= 0 || containerHeight <= 0) return
        
        virtualButtons.forEach { (buttonId, button) ->
            val (x, y) = button.getPosition()
            manager.saveButtonPosition(buttonId, x, y, containerWidth, containerHeight)
        }
        
        virtualJoysticks.forEach { (joystickId, joystick) ->
            val (x, y) = joystick.getPosition()
            manager.saveJoystickPosition(joystickId, x, y, containerWidth, containerHeight)
        }
        
        virtualCombinations.forEach { (combinationId, combination) ->
            val (x, y) = combination.getPosition()
            manager.saveCombinationPosition(combinationId, x, y, containerWidth, containerHeight)
        }
        
        dpadView?.let { dpad ->
            val (x, y) = dpad.getPosition()
            manager.saveDpadPosition(x, y, containerWidth, containerHeight)
        }
    }

    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible
            
            // 确保所有控件的可见性正确设置，考虑其启用状态
            val manager = buttonLayoutManager
            virtualButtons.values.forEach { button ->
                val isButtonEnabled = manager?.isButtonEnabled(button.buttonId) ?: true
                button.isVisible = isVisible && isButtonEnabled
            }
            virtualJoysticks.values.forEach { joystick ->
                val isJoystickEnabled = manager?.isJoystickEnabled(joystick.stickId) ?: true
                joystick.isVisible = isVisible && isJoystickEnabled
            }
            virtualCombinations.values.forEach { combination ->
                val isCombinationEnabled = manager?.isCombinationEnabled(combination.combinationId) ?: true
                combination.isVisible = isVisible && isCombinationEnabled
            }
            dpadView?.let { dpad ->
                val isDpadEnabled = manager?.isDpadEnabled() ?: true
                dpad.isVisible = isVisible && isDpadEnabled
            }

            if (isVisible)
                connect()
        }
    }

    fun connect() {
        if (controllerId == -1)
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
    }
}
