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
import android.widget.FrameLayout
import android.widget.Button
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color as ComposeColor
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.math.MathUtils
import androidx.core.view.isVisible
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings

// 摇杆视图 - 根据截图样式重新设计
class JoystickView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    
    var stickId: Int = 0
    var isLeftStick: Boolean = true
    var stickX: Float = 0f
    var stickY: Float = 0f
    
    // 根据截图样式：深灰色底座，中心有凸起圆形
    private val basePaint = Paint().apply {
        color = Color.argb(200, 60, 60, 60)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val baseBorderPaint = Paint().apply {
        color = Color.argb(200, 30, 30, 30)
        style = Paint.Style.STROKE
        strokeWidth = 4f
        isAntiAlias = true
    }
    
    private val stickPaint = Paint().apply {
        color = Color.argb(200, 80, 80, 80)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val stickBorderPaint = Paint().apply {
        color = Color.argb(200, 40, 40, 40)
        style = Paint.Style.STROKE
        strokeWidth = 3f
        isAntiAlias = true
    }
    
    // 凹槽效果
    private val groovePaint = Paint().apply {
        color = Color.argb(200, 40, 40, 40)
        style = Paint.Style.STROKE
        strokeWidth = 2f
        isAntiAlias = true
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val baseRadius = (width.coerceAtMost(height) / 2f) * 0.9f
        
        // 绘制摇杆底座 - 简单的深灰色圆形
        canvas.drawCircle(centerX, centerY, baseRadius, basePaint)
        canvas.drawCircle(centerX, centerY, baseRadius, baseBorderPaint)
        
        // 绘制环形凹槽
        val grooveRadius = baseRadius * 0.7f
        canvas.drawCircle(centerX, centerY, grooveRadius, groovePaint)
        
        // 绘制摇杆
        val stickRadius = baseRadius * 0.4f
        val stickPosX = centerX + stickX * baseRadius * 0.5f
        val stickPosY = centerY + stickY * baseRadius * 0.5f
        
        // 摇杆阴影
        val shadowPaint = Paint().apply {
            color = Color.argb(100, 0, 0, 0)
            style = Paint.Style.FILL
            isAntiAlias = true
        }
        canvas.drawCircle(stickPosX + 2, stickPosY + 2, stickRadius, shadowPaint)
        
        // 摇杆主体
        canvas.drawCircle(stickPosX, stickPosY, stickRadius, stickPaint)
        canvas.drawCircle(stickPosX, stickPosY, stickRadius, stickBorderPaint)
        
        // 中心点标识
        val centerDotPaint = Paint().apply {
            color = Color.argb(200, 120, 120, 120)
            style = Paint.Style.FILL
            isAntiAlias = true
        }
        canvas.drawCircle(centerX, centerY, 4f, centerDotPaint)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(100)
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
    
    fun updateStickPosition(x: Float, y: Float) {
        stickX = MathUtils.clamp(x, -1f, 1f)
        stickY = MathUtils.clamp(y, -1f, 1f)
        invalidate()
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
}

// 方向键视图 - 根据截图样式重新设计
class DpadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    
    // 根据截图：简单的十字形，深灰色
    private val dpadBasePaint = Paint().apply {
        color = Color.argb(200, 60, 60, 60)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val dpadBorderPaint = Paint().apply {
        color = Color.argb(200, 30, 30, 30)
        style = Paint.Style.STROKE
        strokeWidth = 3f
        isAntiAlias = true
    }
    
    private val dpadPressedPaint = Paint().apply {
        color = Color.argb(200, 100, 150, 255)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    var currentDirection: DpadDirection = DpadDirection.NONE
    
    enum class DpadDirection {
        NONE, UP, DOWN, LEFT, RIGHT, UP_LEFT, UP_RIGHT, DOWN_LEFT, DOWN_RIGHT
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val size = (width.coerceAtMost(height) / 2f) * 0.8f
        val armWidth = size / 2f
        val armLength = size
        
        // 绘制方向键臂 - 简单的矩形
        // 上臂
        if (currentDirection == DpadDirection.UP || 
            currentDirection == DpadDirection.UP_LEFT || 
            currentDirection == DpadDirection.UP_RIGHT) {
            canvas.drawRect(
                centerX - armWidth/2, 
                centerY - armLength, 
                centerX + armWidth/2, 
                centerY - armWidth/2, 
                dpadPressedPaint
            )
        } else {
            canvas.drawRect(
                centerX - armWidth/2, 
                centerY - armLength, 
                centerX + armWidth/2, 
                centerY - armWidth/2, 
                dpadBasePaint
            )
        }
        
        // 下臂
        if (currentDirection == DpadDirection.DOWN || 
            currentDirection == DpadDirection.DOWN_LEFT || 
            currentDirection == DpadDirection.DOWN_RIGHT) {
            canvas.drawRect(
                centerX - armWidth/2, 
                centerY + armWidth/2, 
                centerX + armWidth/2, 
                centerY + armLength, 
                dpadPressedPaint
            )
        } else {
            canvas.drawRect(
                centerX - armWidth/2, 
                centerY + armWidth/2, 
                centerX + armWidth/2, 
                centerY + armLength, 
                dpadBasePaint
            )
        }
        
        // 左臂
        if (currentDirection == DpadDirection.LEFT || 
            currentDirection == DpadDirection.UP_LEFT || 
            currentDirection == DpadDirection.DOWN_LEFT) {
            canvas.drawRect(
                centerX - armLength, 
                centerY - armWidth/2, 
                centerX - armWidth/2, 
                centerY + armWidth/2, 
                dpadPressedPaint
            )
        } else {
            canvas.drawRect(
                centerX - armLength, 
                centerY - armWidth/2, 
                centerX - armWidth/2, 
                centerY + armWidth/2, 
                dpadBasePaint
            )
        }
        
        // 右臂
        if (currentDirection == DpadDirection.RIGHT || 
            currentDirection == DpadDirection.UP_RIGHT || 
            currentDirection == DpadDirection.DOWN_RIGHT) {
            canvas.drawRect(
                centerX + armWidth/2, 
                centerY - armWidth/2, 
                centerX + armLength, 
                centerY + armWidth/2, 
                dpadPressedPaint
            )
        } else {
            canvas.drawRect(
                centerX + armWidth/2, 
                centerY - armWidth/2, 
                centerX + armLength, 
                centerY + armWidth/2, 
                dpadBasePaint
            )
        }
        
        // 绘制中心方块
        canvas.drawRect(
            centerX - armWidth/2, 
            centerY - armWidth/2, 
            centerX + armWidth/2, 
            centerY + armWidth/2, 
            dpadBasePaint
        )
        
        // 绘制边框
        canvas.drawRect(
            centerX - armWidth/2, 
            centerY - armLength, 
            centerX + armWidth/2, 
            centerY - armWidth/2, 
            dpadBorderPaint
        )
        canvas.drawRect(
            centerX - armWidth/2, 
            centerY + armWidth/2, 
            centerX + armWidth/2, 
            centerY + armLength, 
            dpadBorderPaint
        )
        canvas.drawRect(
            centerX - armLength, 
            centerY - armWidth/2, 
            centerX - armWidth/2, 
            centerY + armWidth/2, 
            dpadBorderPaint
        )
        canvas.drawRect(
            centerX + armWidth/2, 
            centerY - armWidth/2, 
            centerX + armLength, 
            centerY + armWidth/2, 
            dpadBorderPaint
        )
        canvas.drawRect(
            centerX - armWidth/2, 
            centerY - armWidth/2, 
            centerX + armWidth/2, 
            centerY + armWidth/2, 
            dpadBorderPaint
        )
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(100)
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
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
}

// 自定义可拖拽按钮
class DraggableButtonView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    
    var buttonId: Int = 0
    var buttonText: String = ""
    var buttonPressed: Boolean = false
        set(value) {
            field = value
            invalidate()
        }
    
    private val outerCirclePaint = Paint().apply {
        color = Color.argb(200, 60, 60, 60)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val outerBorderPaint = Paint().apply {
        color = Color.argb(200, 30, 30, 30)
        style = Paint.Style.STROKE
        strokeWidth = 3f
        isAntiAlias = true
    }
    
    private val innerCirclePaint = Paint().apply {
        color = Color.argb(200, 80, 80, 80)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val pressedPaint = Paint().apply {
        color = Color.argb(200, 100, 150, 255)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val textPaint = Paint().apply {
        color = Color.argb(200, 255, 255, 255)
        textSize = 18f
        textAlign = Paint.Align.CENTER
        typeface = Typeface.DEFAULT_BOLD
        isAntiAlias = true
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val radius = (width.coerceAtMost(height) / 2f) * 0.8f
        
        // 绘制外圈
        canvas.drawCircle(centerX, centerY, radius, outerCirclePaint)
        canvas.drawCircle(centerX, centerY, radius, outerBorderPaint)
        
        // 绘制内圈（按压时变色）
        val fillPaint = if (buttonPressed) pressedPaint else innerCirclePaint
        canvas.drawCircle(centerX, centerY, radius * 0.7f, fillPaint)
        
        // 绘制文字
        val textY = centerY - (textPaint.descent() + textPaint.ascent()) / 2
        canvas.drawText(buttonText, centerX, textY, textPaint)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(60)
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
        ButtonConfig(11, "L3", 0.2f, 0.8f, GamePadButtonInputId.LeftStick.ordinal),
        ButtonConfig(12, "R3", 0.7f, 0.8f, GamePadButtonInputId.RightStick.ordinal)
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
        
        // 使用保存的位置或默认位置
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
    
    // 新增方法：通过keyCode获取按钮配置
    fun getButtonConfigByKeyCode(keyCode: Int): ButtonConfig? {
        return buttonConfigs.find { it.keyCode == keyCode }
    }
}

class GameController(var activity: Activity) {

    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            
            // 获取按钮容器
            val buttonContainer = view.findViewById<FrameLayout>(R.id.buttonContainer)!!
            val editModeContainer = view.findViewById<FrameLayout>(R.id.editModeContainer)!!
            
            // 初始化按钮管理器
            controller.buttonLayoutManager = ButtonLayoutManager(context)
            
            // 创建所有虚拟控件
            controller.createVirtualControls(buttonContainer)
            
            // 创建保存按钮
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
            
            // 监听编辑模式变化
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
    private val virtualButtons = mutableMapOf<Int, DraggableButtonView>()
    private val virtualJoysticks = mutableMapOf<Int, JoystickView>()
    private var dpadView: DpadView? = null
    var controllerId: Int = -1
    private var isEditing = false

    val isVisible: Boolean
        get() {
            controllerView?.apply {
                return this.isVisible
            }
            return false
        }

    init {
        // 初始化控制器
    }

    private fun createVirtualControls(buttonContainer: FrameLayout) {
        this.buttonContainer = buttonContainer
        val manager = buttonLayoutManager ?: return
        
        // 直接创建控件，不等待布局完成
        createControlsImmediately(buttonContainer, manager)
    }
    
    private fun createControlsImmediately(buttonContainer: FrameLayout, manager: ButtonLayoutManager) {
        // 获取容器尺寸
        val containerWidth = buttonContainer.width
        val containerHeight = buttonContainer.height
        
        // 如果容器尺寸为0，使用屏幕尺寸作为后备
        val effectiveWidth = if (containerWidth > 0) containerWidth else activity.resources.displayMetrics.widthPixels
        val effectiveHeight = if (containerHeight > 0) containerHeight else activity.resources.displayMetrics.heightPixels
        
        // 创建摇杆
        manager.getAllJoystickConfigs().forEach { config ->
            val joystick = JoystickView(buttonContainer.context).apply {
                stickId = config.id
                isLeftStick = config.isLeft
                
                // 设置初始位置
                val (x, y) = manager.getJoystickPosition(config.id, effectiveWidth, effectiveHeight)
                setPosition(x, y)
                
                // 设置触摸监听器
                setOnTouchListener { _, event ->
                    if (isEditing) {
                        // 编辑模式：可拖拽
                        handleJoystickDragEvent(event, config.id)
                    } else {
                        // 游戏模式：发送摇杆事件
                        handleJoystickEvent(event, config.isLeft)
                    }
                    true
                }
            }
            
            buttonContainer.addView(joystick)
            virtualJoysticks[config.id] = joystick
        }
        
        // 创建方向键 - 使用新的DpadView
        val (dpadX, dpadY) = manager.getDpadPosition(effectiveWidth, effectiveHeight)
        dpadView = DpadView(buttonContainer.context).apply {
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
            val button = DraggableButtonView(buttonContainer.context).apply {
                buttonId = config.id
                buttonText = config.text
                
                // 设置初始位置
                val (x, y) = manager.getButtonPosition(config.id, effectiveWidth, effectiveHeight)
                setPosition(x, y)
                
                // 设置触摸监听器
                setOnTouchListener { _, event ->
                    if (isEditing) {
                        // 编辑模式：可拖拽
                        handleButtonDragEvent(event, config.id)
                    } else {
                        // 游戏模式：发送按键事件
                        handleButtonEvent(event, config.keyCode)
                    }
                    true
                }
            }
            
            buttonContainer.addView(button)
            virtualButtons[config.id] = button
        }
        
        // 如果容器尺寸为0，延迟刷新位置
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
        
        // 刷新摇杆位置
        virtualJoysticks.forEach { (joystickId, joystick) ->
            val (x, y) = manager.getJoystickPosition(joystickId, containerWidth, containerHeight)
            joystick.setPosition(x, y)
        }
        
        // 刷新方向键位置
        dpadView?.let { dpad ->
            val (x, y) = manager.getDpadPosition(containerWidth, containerHeight)
            dpad.setPosition(x, y)
        }
        
        // 刷新按钮位置
        virtualButtons.forEach { (buttonId, button) ->
            val (x, y) = manager.getButtonPosition(buttonId, containerWidth, containerHeight)
            button.setPosition(x, y)
        }
    }
    
    private fun createSaveButton(editModeContainer: FrameLayout) {
        this.editModeContainer = editModeContainer
        
        // 创建保存按钮
        saveButton = Button(editModeContainer.context).apply {
            text = "保存布局"
            setBackgroundColor(Color.argb(200, 0, 100, 200))
            setTextColor(Color.WHITE)
            textSize = 18f
            setOnClickListener {
                saveLayout()
                setEditingMode(false)
            }
            
            // 设置按钮位置在屏幕中央
            val params = FrameLayout.LayoutParams(
                dpToPx(200),
                dpToPx(60)
            ).apply {
                gravity = android.view.Gravity.CENTER
            }
            layoutParams = params
        }
        
        editModeContainer.addView(saveButton)
        editModeContainer.setBackgroundColor(Color.argb(150, 0, 0, 0))
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
                // 开始拖动
                virtualJoysticks[joystickId]?.let { joystick ->
                    joystick.updateStickPosition(0f, 0f)
                }
            }
            MotionEvent.ACTION_MOVE -> {
                // 更新摇杆位置
                virtualJoysticks[joystickId]?.let { joystick ->
                    val parent = joystick.parent as? ViewGroup ?: return@let
                    val x = event.rawX.toInt() - parent.left
                    val y = event.rawY.toInt() - parent.top
                    
                    // 限制在屏幕范围内
                    val clampedX = MathUtils.clamp(x, 0, parent.width)
                    val clampedY = MathUtils.clamp(y, 0, parent.height)
                    
                    joystick.setPosition(clampedX, clampedY)
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                // 结束拖动
                virtualJoysticks[joystickId]?.updateStickPosition(0f, 0f)
            }
        }
        return true
    }
    
    private fun handleJoystickEvent(event: MotionEvent, isLeftStick: Boolean): Boolean {
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        }
        
        virtualJoysticks.values.find { it.isLeftStick == isLeftStick }?.let { joystick ->
            val centerX = joystick.width / 2f
            val centerY = joystick.height / 2f
            val maxDistance = centerX * 0.7f
            
            when (event.action) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                    val x = event.x - centerX
                    val y = event.y - centerY
                    
                    // 计算摇杆偏移量（归一化到 -1 到 1）
                    val normalizedX = MathUtils.clamp(x / maxDistance, -1f, 1f)
                    val normalizedY = MathUtils.clamp(y / maxDistance, -1f, 1f)
                    
                    joystick.updateStickPosition(normalizedX, normalizedY)
                    
                    // 发送摇杆数据
                    val setting = QuickSettings(activity)
                    val sensitivity = setting.controllerStickSensitivity
                    
                    if (isLeftStick) {
                        RyujinxNative.jnaInstance.inputSetStickAxis(
                            1,
                            normalizedX * sensitivity,
                            -normalizedY * sensitivity,
                            controllerId
                        )
                    } else {
                        RyujinxNative.jnaInstance.inputSetStickAxis(
                            2,
                            normalizedX * sensitivity,
                            -normalizedY * sensitivity,
                            controllerId
                        )
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    joystick.updateStickPosition(0f, 0f)
                    
                    // 重置摇杆位置
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
                // 更新方向键位置
                dpadView?.let { dpad ->
                    val parent = dpad.parent as? ViewGroup ?: return@let
                    val x = event.rawX.toInt() - parent.left
                    val y = event.rawY.toInt() - parent.top
                    
                    // 限制在屏幕范围内
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
                        // 方向改变，先释放旧方向
                        handleDpadDirection(dpad.currentDirection, false)
                        // 设置新方向
                        dpad.currentDirection = direction
                        handleDpadDirection(direction, true)
                        dpad.invalidate()
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    handleDpadDirection(dpad.currentDirection, false)
                    dpad.currentDirection = DpadView.DpadDirection.NONE
                    dpad.invalidate()
                }
            }
        }
        return true
    }
    
    private fun handleDpadDirection(direction: DpadView.DpadDirection, pressed: Boolean) {
        when (direction) {
            DpadView.DpadDirection.UP -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                }
            }
            DpadView.DpadDirection.DOWN -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                }
            }
            DpadView.DpadDirection.LEFT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                }
            }
            DpadView.DpadDirection.RIGHT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                }
            }
            DpadView.DpadDirection.UP_LEFT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                }
            }
            DpadView.DpadDirection.UP_RIGHT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadUp.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                }
            }
            DpadView.DpadDirection.DOWN_LEFT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadLeft.ordinal, controllerId)
                }
            }
            DpadView.DpadDirection.DOWN_RIGHT -> {
                if (pressed) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                } else {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadDown.ordinal, controllerId)
                    RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.DpadRight.ordinal, controllerId)
                }
            }
            else -> {
                // 释放所有方向键
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
                // 开始拖动
                virtualButtons[buttonId]?.buttonPressed = true
            }
            MotionEvent.ACTION_MOVE -> {
                // 更新按钮位置
                virtualButtons[buttonId]?.let { button ->
                    val parent = button.parent as? ViewGroup ?: return@let
                    val x = event.rawX.toInt() - parent.left
                    val y = event.rawY.toInt() - parent.top
                    
                    // 限制在屏幕范围内
                    val clampedX = MathUtils.clamp(x, 0, parent.width)
                    val clampedY = MathUtils.clamp(y, 0, parent.height)
                    
                    button.setPosition(clampedX, clampedY)
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                // 结束拖动
                virtualButtons[buttonId]?.buttonPressed = false
            }
        }
        return true
    }
    
    private fun handleButtonEvent(event: MotionEvent, keyCode: Int): Boolean {
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        }
        
        // 修正：通过keyCode找到对应的按钮配置，然后通过按钮id找到正确的按钮视图
        val config = buttonLayoutManager?.getButtonConfigByKeyCode(keyCode)
        val buttonId = config?.id ?: -1
        
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {
                // 修正：使用正确的按钮id来设置按压状态
                virtualButtons[buttonId]?.buttonPressed = true
                RyujinxNative.jnaInstance.inputSetButtonPressed(keyCode, controllerId)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                // 修正：使用正确的按钮id来设置按压状态
                virtualButtons[buttonId]?.buttonPressed = false
                RyujinxNative.jnaInstance.inputSetButtonReleased(keyCode, controllerId)
            }
        }
        return true
    }
    
    fun setEditingMode(editing: Boolean) {
        isEditing = editing
        editModeContainer?.isVisible = editing
        
        virtualButtons.values.forEach { button ->
            button.buttonPressed = false
        }
        virtualJoysticks.values.forEach { joystick ->
            joystick.updateStickPosition(0f, 0f)
        }
        dpadView?.currentDirection = DpadView.DpadDirection.NONE
        dpadView?.invalidate()
    }
    
    fun saveLayout() {
        val manager = buttonLayoutManager ?: return
        val buttonContainer = this.buttonContainer ?: return
        
        val containerWidth = buttonContainer.width
        val containerHeight = buttonContainer.height
        
        if (containerWidth <= 0 || containerHeight <= 0) return
        
        // 保存按钮位置
        virtualButtons.forEach { (buttonId, button) ->
            val (x, y) = button.getPosition()
            manager.saveButtonPosition(buttonId, x, y, containerWidth, containerHeight)
        }
        
        // 保存摇杆位置
        virtualJoysticks.forEach { (joystickId, joystick) ->
            val (x, y) = joystick.getPosition()
            manager.saveJoystickPosition(joystickId, x, y, containerWidth, containerHeight)
        }
        
        // 保存方向键位置
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
