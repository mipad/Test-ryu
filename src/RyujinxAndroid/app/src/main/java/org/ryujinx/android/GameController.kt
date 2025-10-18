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
import android.widget.ImageView
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

// 借鉴yuzu的绘制方式 - 使用Canvas直接绘制
class JoystickOverlayView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    
    var stickId: Int = 0
    var isLeftStick: Boolean = true
    var stickX: Float = 0f
    var stickY: Float = 0f
    private var isTouching: Boolean = false
    
    // 借鉴yuzu的摇杆范围控制
    private var outerRect: Rect = Rect()
    private var innerRect: Rect = Rect()
    private var centerX: Float = 0f
    private var centerY: Float = 0f
    
    // 位图资源
    private var outerBitmap: Bitmap? = null
    private var innerDefaultBitmap: Bitmap? = null
    private var innerPressedBitmap: Bitmap? = null
    
    // 移动范围 - 借鉴yuzu的计算方式
    private var movementRadius: Float = 0f
    
    init {
        // 移除背景
        setBackgroundResource(0)
        loadBitmaps()
    }
    
    private fun loadBitmaps() {
        // 使用矢量图资源，借鉴yuzu的位图加载方式
        outerBitmap = getBitmapFromVectorDrawable(R.drawable.joystick_range, 0.4f)
        innerDefaultBitmap = getBitmapFromVectorDrawable(R.drawable.joystick, 0.35f)
        innerPressedBitmap = getBitmapFromVectorDrawable(R.drawable.joystick_depressed, 0.35f)
    }
    
    private fun getBitmapFromVectorDrawable(drawableId: Int, scale: Float): Bitmap {
        val drawable = ContextCompat.getDrawable(context, drawableId) ?: 
            throw IllegalArgumentException("Drawable not found: $drawableId")
        
        val width = (drawable.intrinsicWidth * scale).toInt().takeIf { it > 0 } ?: 100
        val height = (drawable.intrinsicHeight * scale).toInt().takeIf { it > 0 } ?: 100
        
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        
        val canvas = Canvas(bitmap)
        drawable.setBounds(0, 0, width, height)
        drawable.draw(canvas)
        return bitmap
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(160)
        setMeasuredDimension(size, size)
    }
    
    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        
        centerX = w / 2f
        centerY = h / 2f
        
        // 设置外圈矩形 - 整个View大小
        outerRect.set(0, 0, w, h)
        
        // 设置内圈矩形 - 借鉴yuzu的比例计算
        val outerScale = 2.0f
        val innerSize = (w / outerScale).toInt()
        innerRect.set(0, 0, innerSize, innerSize)
        
        // 计算移动半径
        movementRadius = (w - innerSize) / 3f
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
        
        // 绘制外圈
        outerBitmap?.let { bitmap ->
            val outerLeft = (width - bitmap.width) / 2f
            val outerTop = (height - bitmap.height) / 2f
            canvas.drawBitmap(bitmap, outerLeft, outerTop, null)
        }
        
        // 计算内圈位置
        val innerBitmap = if (isTouching) innerPressedBitmap else innerDefaultBitmap
        innerBitmap?.let { bitmap ->
            val innerDrawWidth = bitmap.width
            val innerDrawHeight = bitmap.height
            
            val maxMoveX = (width - innerDrawWidth) / 2f
            val maxMoveY = (height - innerDrawHeight) / 2f
            
            val innerX = centerX + stickX * maxMoveX - innerDrawWidth / 2
            val innerY = centerY + stickY * maxMoveY - innerDrawHeight / 2
            
            val clampedX = MathUtils.clamp(innerX, 0f, (width - innerDrawWidth).toFloat())
            val clampedY = MathUtils.clamp(innerY, 0f, (height - innerDrawHeight).toFloat())
            
            canvas.drawBitmap(bitmap, clampedX, clampedY, null)
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

// 简化的方向键视图 - 使用Canvas绘制
class DpadOverlayView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    
    var currentDirection: DpadDirection = DpadDirection.NONE
    
    enum class DpadDirection {
        NONE, UP, DOWN, LEFT, RIGHT, UP_LEFT, UP_RIGHT, DOWN_LEFT, DOWN_RIGHT
    }
    
    private var defaultBitmap: Bitmap? = null
    private var pressedOneDirectionBitmap: Bitmap? = null
    private var pressedTwoDirectionsBitmap: Bitmap? = null
    
    init {
        setBackgroundResource(0)
        loadBitmaps()
    }
    
    private fun loadBitmaps() {
        defaultBitmap = getBitmapFromVectorDrawable(R.drawable.dpad_standard, 0.35f)
        pressedOneDirectionBitmap = getBitmapFromVectorDrawable(R.drawable.dpad_standard_cardinal_depressed, 0.35f)
        pressedTwoDirectionsBitmap = getBitmapFromVectorDrawable(R.drawable.dpad_standard_diagonal_depressed, 0.35f)
    }
    
    private fun getBitmapFromVectorDrawable(drawableId: Int, scale: Float): Bitmap {
        val drawable = ContextCompat.getDrawable(context, drawableId) ?: 
            throw IllegalArgumentException("Drawable not found: $drawableId")
        
        val width = (drawable.intrinsicWidth * scale).toInt().takeIf { it > 0 } ?: 120
        val height = (drawable.intrinsicHeight * scale).toInt().takeIf { it > 0 } ?: 120
        
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        
        val canvas = Canvas(bitmap)
        drawable.setBounds(0, 0, width, height)
        drawable.draw(canvas)
        return bitmap
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(150)
        setMeasuredDimension(size, size)
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
        
        return when {
            relY < -0.3 && relX < -0.3 -> DpadDirection.UP_LEFT
            relY < -0.3 && relX > 0.3 -> DpadDirection.UP_RIGHT
            relY > 0.3 && relX < -0.3 -> DpadDirection.DOWN_LEFT
            relY > 0.3 && relX > 0.3 -> DpadDirection.DOWN_RIGHT
            relY < -0.3 -> DpadDirection.UP
            relY > 0.3 -> DpadDirection.DOWN
            relX < -0.3 -> DpadDirection.LEFT
            relX > 0.3 -> DpadDirection.RIGHT
            else -> DpadDirection.NONE
        }
    }
    
    fun updateDirection(direction: DpadDirection) {
        currentDirection = direction
        invalidate()
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val bitmap = when (currentDirection) {
            DpadDirection.UP, DpadDirection.DOWN, DpadDirection.LEFT, DpadDirection.RIGHT -> {
                pressedOneDirectionBitmap?.also { 
                    canvas.save()
                    when (currentDirection) {
                        DpadDirection.UP -> canvas.rotate(0f, width / 2f, height / 2f)
                        DpadDirection.DOWN -> canvas.rotate(180f, width / 2f, height / 2f)
                        DpadDirection.LEFT -> canvas.rotate(270f, width / 2f, height / 2f)
                        DpadDirection.RIGHT -> canvas.rotate(90f, width / 2f, height / 2f)
                        else -> {}
                    }
                }
            }
            DpadDirection.UP_LEFT, DpadDirection.UP_RIGHT, 
            DpadDirection.DOWN_LEFT, DpadDirection.DOWN_RIGHT -> {
                pressedTwoDirectionsBitmap?.also {
                    canvas.save()
                    when (currentDirection) {
                        DpadDirection.UP_LEFT -> canvas.rotate(0f, width / 2f, height / 2f)
                        DpadDirection.UP_RIGHT -> canvas.rotate(90f, width / 2f, height / 2f)
                        DpadDirection.DOWN_RIGHT -> canvas.rotate(180f, width / 2f, height / 2f)
                        DpadDirection.DOWN_LEFT -> canvas.rotate(270f, width / 2f, height / 2f)
                        else -> {}
                    }
                }
            }
            else -> defaultBitmap
        }
        
        bitmap?.let {
            val left = (width - it.width) / 2f
            val top = (height - it.height) / 2f
            canvas.drawBitmap(it, left, top, null)
        }
        
        if (currentDirection != DpadDirection.NONE) {
            canvas.restore()
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

// 简化的按钮视图 - 使用Canvas绘制
class ButtonOverlayView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    
    var buttonId: Int = 0
    var buttonText: String = ""
    var buttonPressed: Boolean = false
    
    private var defaultBitmap: Bitmap? = null
    private var pressedBitmap: Bitmap? = null
    
    init {
        setBackgroundResource(0)
    }
    
    fun setBitmaps(defaultResId: Int, pressedResId: Int) {
        defaultBitmap = getBitmapFromVectorDrawable(defaultResId, getScaleForButton())
        pressedBitmap = getBitmapFromVectorDrawable(pressedResId, getScaleForButton())
    }
    
    private fun getScaleForButton(): Float {
        return when (buttonId) {
            5, 6, 7, 8 -> 1f // L, R, ZL, ZR
            9, 10 -> 1f // +, - - 大幅增加菜单按钮大小
            11, 12 -> 1f // L3, R3 - 大幅增加摇杆按钮大小
            else -> 1f // 其他按钮 (ABXY) - 大幅增加主要按钮大小
        }
    }
    
    private fun getBitmapFromVectorDrawable(drawableId: Int, scale: Float): Bitmap {
        val drawable = ContextCompat.getDrawable(context, drawableId) ?: 
            throw IllegalArgumentException("Drawable not found: $drawableId")
        
        val width = (drawable.intrinsicWidth * scale).toInt().takeIf { it > 0 } ?: 100
        val height = (drawable.intrinsicHeight * scale).toInt().takeIf { it > 0 } ?: 100
        
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        
        val canvas = Canvas(bitmap)
        drawable.setBounds(0, 0, width, height)
        drawable.draw(canvas)
        return bitmap
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = when (buttonId) {
            5, 6, 7, 8 -> dpToPx(80) // 肩键和扳机键
            9, 10 -> dpToPx(60) // +和-按钮 - 大幅增加
            11, 12 -> dpToPx(80) // L3和R3按钮 - 大幅增加
            else -> dpToPx(70) // 主要按钮 (A, B, X, Y) - 大幅增加
        }
        setMeasuredDimension(size, size)
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
        
        val bitmap = if (buttonPressed) pressedBitmap else defaultBitmap
        bitmap?.let {
            val left = (width - it.width) / 2f
            val top = (height - it.height) / 2f
            canvas.drawBitmap(it, left, top, null)
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

// 按键配置数据类
data class ButtonConfig(
    val id: Int,
    val text: String,
    val defaultX: Float,
    val defaultY: Float,
    val keyCode: Int
)

// 摇杆配置数据类
data class JoystickConfig(
    val id: Int,
    val isLeft: Boolean,
    val defaultX: Float,
    val defaultY: Float
)

// 方向键配置数据类
data class DpadConfig(
    val id: Int,
    val defaultX: Float,
    val defaultY: Float
)

// 按键管理器
class ButtonLayoutManager(private val context: Context) {
    private val prefs = context.getSharedPreferences("virtual_controls", Context.MODE_PRIVATE)
    
    private val buttonConfigs = listOf(
        ButtonConfig(1, "A", 0.85f, 0.7f, GamePadButtonInputId.A.ordinal),
        ButtonConfig(2, "B", 0.92f, 0.6f, GamePadButtonInputId.B.ordinal),
        ButtonConfig(3, "X", 0.78f, 0.6f, GamePadButtonInputId.X.ordinal),
        ButtonConfig(4, "Y", 0.85f, 0.5f, GamePadButtonInputId.Y.ordinal),
        ButtonConfig(5, "L", 0.1f, 0.2f, GamePadButtonInputId.LeftShoulder.ordinal),
        ButtonConfig(6, "R", 0.9f, 0.2f, GamePadButtonInputId.RightShoulder.ordinal),
        ButtonConfig(7, "ZL", 0.1f, 0.1f, GamePadButtonInputId.LeftTrigger.ordinal),
        ButtonConfig(8, "ZR", 0.9f, 0.1f, GamePadButtonInputId.RightTrigger.ordinal),
        ButtonConfig(9, "+", 0.8f, 0.1f, GamePadButtonInputId.Plus.ordinal),
        ButtonConfig(10, "-", 0.2f, 0.1f, GamePadButtonInputId.Minus.ordinal),
        ButtonConfig(11, "L3", 0.2f, 0.8f, GamePadButtonInputId.LeftStickButton.ordinal),
        ButtonConfig(12, "R3", 0.7f, 0.8f, GamePadButtonInputId.RightStickButton.ordinal)
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
    
    fun getAllButtonConfigs(): List<ButtonConfig> = buttonConfigs
    fun getAllJoystickConfigs(): List<JoystickConfig> = joystickConfigs
    fun getDpadConfig(): DpadConfig = dpadConfig
}

class GameController(var activity: Activity) {

    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            
            val buttonContainer = view.findViewById<FrameLayout>(R.id.buttonContainer)!!
            val editModeContainer = view.findViewById<FrameLayout>(R.id.editModeContainer)!!
            
            controller.buttonLayoutManager = ButtonLayoutManager(context)
            controller.createVirtualControls(buttonContainer)
            controller.createSaveButton(editModeContainer)
            
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
        
        val effectiveWidth = if (containerWidth > 0) containerWidth else activity.resources.displayMetrics.widthPixels
        val effectiveHeight = if (containerHeight > 0) containerHeight else activity.resources.displayMetrics.heightPixels
        
        // 创建摇杆
        manager.getAllJoystickConfigs().forEach { config ->
            val joystick = JoystickOverlayView(buttonContainer.context).apply {
                stickId = config.id
                isLeftStick = config.isLeft
                
                val (x, y) = manager.getJoystickPosition(config.id, effectiveWidth, effectiveHeight)
                setPosition(x, y)
                
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
        
        // 创建方向键
        val (dpadX, dpadY) = manager.getDpadPosition(effectiveWidth, effectiveHeight)
        dpadView = DpadOverlayView(buttonContainer.context).apply {
            setPosition(dpadX, dpadY)
            
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
        
        // 创建按钮
        manager.getAllButtonConfigs().forEach { config ->
            val button = ButtonOverlayView(buttonContainer.context).apply {
                buttonId = config.id
                buttonText = config.text
                
                // 设置按钮位图
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
                
                val (x, y) = manager.getButtonPosition(config.id, effectiveWidth, effectiveHeight)
                setPosition(x, y)
                
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
        
        if (containerWidth <= 0 || containerHeight <= 0) {
            buttonContainer.post {
                refreshControlPositions()
            }
        }
    }
    
    private fun refreshControlPositions() {
        val manager = buttonLayoutManager ?: return
        val buttonContainer = this.buttonContainer ?: return
        
        val containerWidth = buttonContainer.width
        val containerHeight = buttonContainer.height
        
        if (containerWidth <= 0 || containerHeight <= 0) return
        
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
    
    private fun createSaveButton(editModeContainer: FrameLayout) {
        this.editModeContainer = editModeContainer
        
        saveButton = Button(editModeContainer.context).apply {
            text = "保存布局"
            setBackgroundColor(android.graphics.Color.argb(150, 0, 100, 200))
            setTextColor(android.graphics.Color.WHITE)
            textSize = 12f
            setOnClickListener {
                saveLayout()
                setEditingMode(false)
            }
            
            val params = FrameLayout.LayoutParams(
                dpToPx(200),
                dpToPx(60)
            ).apply {
                gravity = android.view.Gravity.CENTER
            }
            layoutParams = params
        }
        
        editModeContainer.addView(saveButton)
        editModeContainer.setBackgroundColor(android.graphics.Color.argb(150, 0, 0, 0))
        editModeContainer.isVisible = false
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            activity.resources.displayMetrics
        ).toInt()
    }
    
    private fun handleJoystickDragEvent(event: MotionEvent, joystickId: Int): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {
                virtualJoysticks[joystickId]?.updateStickPosition(0f, 0f, true)
            }
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
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                virtualJoysticks[joystickId]?.updateStickPosition(0f, 0f, false)
            }
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
            MotionEvent.ACTION_DOWN -> {
                // 开始拖动
            }
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
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                // 结束拖动
            }
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
            MotionEvent.ACTION_DOWN -> {
                virtualButtons[buttonId]?.setPressedState(true)
            }
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
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                virtualButtons[buttonId]?.setPressedState(false)
            }
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
