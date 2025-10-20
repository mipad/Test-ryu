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

// 数据类保持不变...
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

// 按键管理器 - 修改按钮初始位置
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
    
    fun isButtonEnabled(buttonId: Int): Boolean {
        return prefs.getBoolean("button_${buttonId}_enabled", true)
    }
    
    fun isJoystickEnabled(joystickId: Int): Boolean {
        return prefs.getBoolean("joystick_${joystickId}_enabled", true)
    }
    
    fun isDpadEnabled(): Boolean {
        return prefs.getBoolean("dpad_enabled", true)
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
    
    fun getButtonScale(buttonId: Int): Int {
        return prefs.getInt("button_${buttonId}_scale", 50)
    }
    
    fun getJoystickScale(joystickId: Int): Int {
        return prefs.getInt("joystick_${joystickId}_scale", 50)
    }
    
    fun getDpadScale(): Int {
        return prefs.getInt("dpad_scale", 50)
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
    
    fun setButtonEnabled(buttonId: Int, enabled: Boolean) {
        prefs.edit().putBoolean("button_${buttonId}_enabled", enabled).apply()
    }
    
    fun setJoystickEnabled(joystickId: Int, enabled: Boolean) {
        prefs.edit().putBoolean("joystick_${joystickId}_enabled", enabled).apply()
    }
    
    fun setDpadEnabled(enabled: Boolean) {
        prefs.edit().putBoolean("dpad_enabled", enabled).apply()
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
    
    fun setButtonScale(buttonId: Int, scale: Int) {
        prefs.edit().putInt("button_${buttonId}_scale", scale.coerceIn(10, 200)).apply()
    }
    
    fun setJoystickScale(joystickId: Int, scale: Int) {
        prefs.edit().putInt("joystick_${joystickId}_scale", scale.coerceIn(10, 200)).apply()
    }
    
    fun setDpadScale(scale: Int) {
        prefs.edit().putInt("dpad_scale", scale.coerceIn(10, 200)).apply()
    }
    
    fun getAllButtonConfigs(): List<ButtonConfig> = buttonConfigs
    fun getAllJoystickConfigs(): List<JoystickConfig> = joystickConfigs
    fun getDpadConfig(): DpadConfig = dpadConfig
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
    private var dpadView: DpadOverlayView? = null
    var controllerId: Int = -1
    private var isEditing = false

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
    }
    
    private fun createControlsImmediately(buttonContainer: FrameLayout, manager: ButtonLayoutManager) {
        val containerWidth = buttonContainer.width
        val containerHeight = buttonContainer.height
        
        // 创建摇杆 - 传递 individualScale 参数
        manager.getAllJoystickConfigs().forEach { config ->
            if (!manager.isJoystickEnabled(config.id)) return@forEach
            
            val joystick = JoystickOverlayView(
                buttonContainer.context,
                individualScale = manager.getJoystickScale(config.id) // 传递 individualScale
            ).apply {
                stickId = config.id
                isLeftStick = config.isLeft
                opacity = (manager.getJoystickOpacity(config.id) * 255 / 100)
                
                // 不在这里设置位置，统一在 refreshControlPositions 中设置
                
                setOnTouchListener { _, event ->
                    if (isEditing) {
                        handleJoystickDragEvent(event, config.id)
                    } else {
                        handleJoystickEvent(event, config.id, config.isLeft)
                    }
                    true
                }
            }
            
            buttonContainer.addView(joystick)
            virtualJoysticks[config.id] = joystick
        }
        
        // 创建方向键 - 传递 individualScale 参数
        if (manager.isDpadEnabled()) {
            dpadView = DpadOverlayView(
                buttonContainer.context,
                individualScale = manager.getDpadScale() // 传递 individualScale
            ).apply {
                opacity = (manager.getDpadOpacity() * 255 / 100)
                
                // 不在这里设置位置，统一在 refreshControlPositions 中设置
                
                setOnTouchListener { _, event ->
                    if (isEditing) {
                        handleDpadDragEvent(event)
                    } else {
                        handleDpadEvent(event)
                    }
                    true
                }
            }
            buttonContainer.addView(dpadView)
        }
        
        // 创建按钮 - 传递 individualScale 参数
        manager.getAllButtonConfigs().forEach { config ->
            if (!manager.isButtonEnabled(config.id)) return@forEach
            
            val button = ButtonOverlayView(
                buttonContainer.context,
                individualScale = manager.getButtonScale(config.id) // 传递 individualScale
            ).apply {
                buttonId = config.id
                buttonText = config.text
                opacity = (manager.getButtonOpacity(config.id) * 255 / 100)
                
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
                
                setOnTouchListener { _, event ->
                    if (isEditing) {
                        handleButtonDragEvent(event, config.id)
                    } else {
                        handleButtonEvent(event, config.keyCode, config.id)
                    }
                    true
                }
            }
            
            buttonContainer.addView(button)
            virtualButtons[config.id] = button
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
        dpadView?.let { buttonContainer.removeView(it) }
        
        virtualButtons.clear()
        virtualJoysticks.clear()
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
    
    // 新增方法：应用所有控件的设置（用于调整模式的确定操作）
    fun applyAllControlSettings() {
        val manager = buttonLayoutManager ?: return
        
        // 应用所有按钮的设置
        manager.getAllButtonConfigs().forEach { config ->
            val button = virtualButtons[config.id]
            if (button != null) {
                // 更新启用状态
                button.isVisible = manager.isButtonEnabled(config.id)
                
                // 更新透明度
                button.opacity = (manager.getButtonOpacity(config.id) * 255 / 100)
                
                // 更新缩放
                button.individualScale = manager.getButtonScale(config.id)
                
                // 重新加载位图
                when (config.id) {
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
                
                button.requestLayout()
                button.invalidate()
            }
        }
        
        // 应用所有摇杆的设置
        manager.getAllJoystickConfigs().forEach { config ->
            val joystick = virtualJoysticks[config.id]
            if (joystick != null) {
                // 更新启用状态
                joystick.isVisible = manager.isJoystickEnabled(config.id)
                
                // 更新透明度
                joystick.opacity = (manager.getJoystickOpacity(config.id) * 255 / 100)
                
                // 更新缩放
                joystick.individualScale = manager.getJoystickScale(config.id)
                
                // 重新加载位图
                joystick.loadBitmaps()
                
                joystick.requestLayout()
                joystick.invalidate()
            }
        }
        
        // 应用方向键的设置
        val dpad = dpadView
        if (dpad != null) {
            // 更新启用状态
            dpad.isVisible = manager.isDpadEnabled()
            
            // 更新透明度
            dpad.opacity = (manager.getDpadOpacity() * 255 / 100)
            
            // 更新缩放
            dpad.individualScale = manager.getDpadScale()
            
            // 重新加载位图
            dpad.loadBitmaps()
            
            dpad.requestLayout()
            dpad.invalidate()
        }
        
        // 刷新位置确保控件正确布局
        refreshControlPositions()
    }
    
    // 新增方法：重置所有控件到默认设置（用于调整模式的全局重置）
    fun resetAllControlsToDefault() {
        val manager = buttonLayoutManager ?: return
        
        // 重置所有按钮的设置
        manager.getAllButtonConfigs().forEach { config ->
            // 重置启用状态
            manager.setButtonEnabled(config.id, true)
            
            // 重置透明度
            manager.setButtonOpacity(config.id, 100)
            
            // 重置缩放
            manager.setButtonScale(config.id, 50)
        }
        
        // 重置所有摇杆的设置
        manager.getAllJoystickConfigs().forEach { config ->
            // 重置启用状态
            manager.setJoystickEnabled(config.id, true)
            
            // 重置透明度
            manager.setJoystickOpacity(config.id, 100)
            
            // 重置缩放
            manager.setJoystickScale(config.id, 50)
        }
        
        // 重置方向键的设置
        manager.setDpadEnabled(true)
        manager.setDpadOpacity(100)
        manager.setDpadScale(50)
        
        // 应用所有设置
        applyAllControlSettings()
    }
    
    fun getControlScale(controlId: Int): Int {
        return when {
            controlId in 1..12 -> buttonLayoutManager?.getButtonScale(controlId) ?: 50
            controlId in 101..102 -> buttonLayoutManager?.getJoystickScale(controlId) ?: 50
            controlId == 201 -> buttonLayoutManager?.getDpadScale() ?: 50
            else -> 50
        }
    }
    
    fun getControlOpacity(controlId: Int): Int {
        return when {
            controlId in 1..12 -> buttonLayoutManager?.getButtonOpacity(controlId) ?: 100
            controlId in 101..102 -> buttonLayoutManager?.getJoystickOpacity(controlId) ?: 100
            controlId == 201 -> buttonLayoutManager?.getDpadOpacity() ?: 100
            else -> 100
        }
    }
    
    fun isControlEnabled(controlId: Int): Boolean {
        return when {
            controlId in 1..12 -> buttonLayoutManager?.isButtonEnabled(controlId) ?: true
            controlId in 101..102 -> buttonLayoutManager?.isJoystickEnabled(controlId) ?: true
            controlId == 201 -> buttonLayoutManager?.isDpadEnabled() ?: true
            else -> true
        }
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
    
    // 事件处理方法保持不变...
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
    
    private fun handleJoystickEvent(event: MotionEvent, joystickId: Int, isLeftStick: Boolean): Boolean {
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        }
        
        virtualJoysticks[joystickId]?.let { joystick ->
            val centerX = joystick.width / 2f
            val centerY = joystick.height / 2f
            
            when (event.action) {
                MotionEvent.ACTION_DOWN -> {
                    joystick.updateStickPosition(0f, 0f, true)
                }
                MotionEvent.ACTION_MOVE -> {
                    val x = event.x - centerX
                    val y = event.y - centerY
                    
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
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    joystick.updateStickPosition(0f, 0f, false)
                    
                    if (isLeftStick) {
                        RyujinxNative.jnaInstance.inputSetStickAxis(1, 0f, 0f, controllerId)
                    } else {
                        RyujinxNative.jnaInstance.inputSetStickAxis(2, 0f, 0f, controllerId)
                    }
                }
            }
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
    
    private fun handleDpadEvent(event: MotionEvent): Boolean {
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        }
        
        dpadView?.let { dpad ->
            when (event.action) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                    val direction = dpad.getDirectionFromTouch(event.x, event.y)
                    if (dpad.currentDirection != direction) {
                        handleDpadDirection(dpad.currentDirection, false)
                        dpad.currentDirection = direction
                        dpad.updateDirection(direction)
                        handleDpadDirection(direction, true)
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    handleDpadDirection(dpad.currentDirection, false)
                    dpad.currentDirection = DpadOverlayView.DpadDirection.NONE
                    dpad.updateDirection(DpadOverlayView.DpadDirection.NONE)
                }
            }
        }
        return true
    }
    
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
    
    private fun handleButtonEvent(event: MotionEvent, keyCode: Int, buttonId: Int): Boolean {
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        }
        
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {
                virtualButtons[buttonId]?.setPressedState(true)
                RyujinxNative.jnaInstance.inputSetButtonPressed(keyCode, controllerId)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                virtualButtons[buttonId]?.setPressedState(false)
                RyujinxNative.jnaInstance.inputSetButtonReleased(keyCode, controllerId)
            }
        }
        return true
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
        
        dpadView?.let { dpad ->
            val (x, y) = dpad.getPosition()
            manager.saveDpadPosition(x, y, containerWidth, containerHeight)
        }
    }

    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible
            virtualButtons.values.forEach { it.isVisible = isVisible }
            virtualJoysticks.values.forEach { it.isVisible = isVisible }
            dpadView?.isVisible = isVisible

            if (isVisible)
                connect()
        }
    }

    fun connect() {
        if (controllerId == -1)
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
    }
}
