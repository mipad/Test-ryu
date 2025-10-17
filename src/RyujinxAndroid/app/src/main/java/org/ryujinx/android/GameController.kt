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

// 摇杆视图
class JoystickView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    
    var stickId: Int = 0
    var isLeftStick: Boolean = true
    var stickX: Float = 0f
    var stickY: Float = 0f
    
    private val basePaint = Paint().apply {
        color = Color.argb(76, 80, 80, 80) // 0.3透明度
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val baseBorderPaint = Paint().apply {
        color = Color.argb(76, 200, 200, 200) // 0.3透明度
        style = Paint.Style.STROKE
        strokeWidth = 4f
        isAntiAlias = true
    }
    
    private val stickPaint = Paint().apply {
        color = Color.argb(76, 240, 240, 240) // 0.3透明度
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val stickBorderPaint = Paint().apply {
        color = Color.argb(76, 180, 180, 180) // 0.3透明度
        style = Paint.Style.STROKE
        strokeWidth = 3f
        isAntiAlias = true
    }
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val baseRadius = (width.coerceAtMost(height) / 2f) * 0.8f
        
        // 绘制摇杆底座
        canvas.drawCircle(centerX, centerY, baseRadius, basePaint)
        canvas.drawCircle(centerX, centerY, baseRadius, baseBorderPaint)
        
        // 绘制摇杆
        val stickRadius = baseRadius * 0.5f
        val stickPosX = centerX + stickX * baseRadius * 0.7f
        val stickPosY = centerY + stickY * baseRadius * 0.7f
        canvas.drawCircle(stickPosX, stickPosY, stickRadius, stickPaint)
        canvas.drawCircle(stickPosX, stickPosY, stickRadius, stickBorderPaint)
        
        // 绘制中心点
        val centerDotPaint = Paint().apply {
            color = Color.argb(76, 100, 100, 100)
            style = Paint.Style.FILL
            isAntiAlias = true
        }
        canvas.drawCircle(centerX, centerY, 5f, centerDotPaint)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(120)
        setMeasuredDimension(size, size)
    }
    
    fun setAbsolutePosition(x: Int, y: Int) {
        val params = layoutParams as? FrameLayout.LayoutParams ?: FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        )
        params.leftMargin = x - width / 2
        params.topMargin = y - height / 2
        layoutParams = params
    }
    
    fun getAbsolutePosition(): Pair<Int, Int> {
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

// 方向键视图
class DpadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {
    
    private val dpadBasePaint = Paint().apply {
        color = Color.argb(76, 60, 60, 60)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val dpadBorderPaint = Paint().apply {
        color = Color.argb(76, 180, 180, 180)
        style = Paint.Style.STROKE
        strokeWidth = 3f
        isAntiAlias = true
    }
    
    private val dpadPressedPaint = Paint().apply {
        color = Color.argb(76, 80, 160, 255)
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
        
        // 绘制方向键臂
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
    
    fun setAbsolutePosition(x: Int, y: Int) {
        val params = layoutParams as? FrameLayout.LayoutParams ?: FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        )
        params.leftMargin = x - width / 2
        params.topMargin = y - height / 2
        layoutParams = params
    }
    
    fun getAbsolutePosition(): Pair<Int, Int> {
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
        color = Color.argb(76, 255, 255, 255)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val outerBorderPaint = Paint().apply {
        color = Color.argb(76, 180, 180, 180)
        style = Paint.Style.STROKE
        strokeWidth = 3f
        isAntiAlias = true
    }
    
    private val innerCirclePaint = Paint().apply {
        color = Color.argb(76, 100, 100, 100)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val pressedPaint = Paint().apply {
        color = Color.argb(76, 255, 100, 100)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val textPaint = Paint().apply {
        color = Color.argb(76, 255, 255, 255)
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
    
    fun setAbsolutePosition(x: Int, y: Int) {
        val params = layoutParams as? FrameLayout.LayoutParams ?: FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        )
        params.leftMargin = x - width / 2
        params.topMargin = y - height / 2
        layoutParams = params
    }
    
    fun getAbsolutePosition(): Pair<Int, Int> {
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
    val defaultX: Int, // 改为绝对坐标
    val defaultY: Int, // 改为绝对坐标
    val keyCode: Int
)

// 摇杆配置数据类
data class JoystickConfig(
    val id: Int,
    val isLeft: Boolean,
    val defaultX: Int, // 改为绝对坐标
    val defaultY: Int  // 改为绝对坐标
)

// 方向键配置数据类
data class DpadConfig(
    val id: Int,
    val defaultX: Int, // 改为绝对坐标
    val defaultY: Int  // 改为绝对坐标
)

// 按键管理器
class ButtonLayoutManager(private val context: Context) {
    private val prefs = context.getSharedPreferences("virtual_controls", Context.MODE_PRIVATE)
    
    // 使用绝对坐标（基于横屏1280x720的默认位置，会在运行时根据实际屏幕调整）
    private val buttonConfigs = listOf(
        ButtonConfig(1, "A", 1088, 504, GamePadButtonInputId.A.ordinal),      // 0.85f, 0.7f
        ButtonConfig(2, "B", 1178, 432, GamePadButtonInputId.B.ordinal),      // 0.92f, 0.6f
        ButtonConfig(3, "X", 998, 432, GamePadButtonInputId.X.ordinal),       // 0.78f, 0.6f
        ButtonConfig(4, "Y", 1088, 360, GamePadButtonInputId.Y.ordinal),      // 0.85f, 0.5f
        ButtonConfig(5, "L", 128, 144, GamePadButtonInputId.LeftShoulder.ordinal),   // 0.1f, 0.2f
        ButtonConfig(6, "R", 1152, 144, GamePadButtonInputId.RightShoulder.ordinal), // 0.9f, 0.2f
        ButtonConfig(7, "ZL", 128, 72, GamePadButtonInputId.LeftTrigger.ordinal),    // 0.1f, 0.1f
        ButtonConfig(8, "ZR", 1152, 72, GamePadButtonInputId.RightTrigger.ordinal),  // 0.9f, 0.1f
        ButtonConfig(9, "+", 1024, 72, GamePadButtonInputId.Plus.ordinal),    // 0.8f, 0.1f
        ButtonConfig(10, "-", 256, 72, GamePadButtonInputId.Minus.ordinal),   // 0.2f, 0.1f
        ButtonConfig(11, "L3", 256, 576, GamePadButtonInputId.LeftStick.ordinal),    // 0.2f, 0.8f
        ButtonConfig(12, "R3", 896, 576, GamePadButtonInputId.RightStick.ordinal)    // 0.7f, 0.8f
    )
    
    private val joystickConfigs = listOf(
        JoystickConfig(101, true, 256, 504),   // 0.2f, 0.7f
        JoystickConfig(102, false, 896, 504)   // 0.7f, 0.7f
    )
    
    private val dpadConfig = DpadConfig(201, 128, 360)  // 0.1f, 0.5f
    
    // 获取屏幕尺寸
    private fun getScreenWidth(): Int {
        return context.resources.displayMetrics.widthPixels
    }
    
    private fun getScreenHeight(): Int {
        return context.resources.displayMetrics.heightPixels
    }
    
    // 调整默认位置到当前屏幕尺寸
    private fun adjustDefaultPosition(defaultX: Int, defaultY: Int): Pair<Int, Int> {
        val screenWidth = getScreenWidth()
        val screenHeight = getScreenHeight()
        
        // 基于1280x720的默认位置进行缩放
        val scaleX = screenWidth / 1280f
        val scaleY = screenHeight / 720f
        
        val adjustedX = (defaultX * scaleX).toInt()
        val adjustedY = (defaultY * scaleY).toInt()
        
        return Pair(adjustedX, adjustedY)
    }
    
    fun getButtonPosition(buttonId: Int): Pair<Int, Int> {
        val xPref = prefs.getInt("button_${buttonId}_x", -1)
        val yPref = prefs.getInt("button_${buttonId}_y", -1)
        
        val config = buttonConfigs.find { it.id == buttonId } ?: return Pair(0, 0)
        
        // 使用保存的绝对位置或调整后的默认位置
        return if (xPref != -1 && yPref != -1) {
            Pair(xPref, yPref)
        } else {
            adjustDefaultPosition(config.defaultX, config.defaultY)
        }
    }
    
    fun getJoystickPosition(joystickId: Int): Pair<Int, Int> {
        val xPref = prefs.getInt("joystick_${joystickId}_x", -1)
        val yPref = prefs.getInt("joystick_${joystickId}_y", -1)
        
        val config = joystickConfigs.find { it.id == joystickId } ?: return Pair(0, 0)
        
        return if (xPref != -1 && yPref != -1) {
            Pair(xPref, yPref)
        } else {
            adjustDefaultPosition(config.defaultX, config.defaultY)
        }
    }
    
    fun getDpadPosition(): Pair<Int, Int> {
        val xPref = prefs.getInt("dpad_x", -1)
        val yPref = prefs.getInt("dpad_y", -1)
        
        return if (xPref != -1 && yPref != -1) {
            Pair(xPref, yPref)
        } else {
            adjustDefaultPosition(dpadConfig.defaultX, dpadConfig.defaultY)
        }
    }
    
    fun saveButtonPosition(buttonId: Int, x: Int, y: Int) {
        prefs.edit()
            .putInt("button_${buttonId}_x", x)
            .putInt("button_${buttonId}_y", y)
            .apply()
    }
    
    fun saveJoystickPosition(joystickId: Int, x: Int, y: Int) {
        prefs.edit()
            .putInt("joystick_${joystickId}_x", x)
            .putInt("joystick_${joystickId}_y", y)
            .apply()
    }
    
    fun saveDpadPosition(x: Int, y: Int) {
        prefs.edit()
            .putInt("dpad_x", x)
            .putInt("dpad_y", y)
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
        
        // 使用post确保在布局完成后执行
        buttonContainer.post {
            createControls()
        }
    }
    
    private fun createControls() {
        val manager = buttonLayoutManager ?: return
        val buttonContainer = this.buttonContainer ?: return
        
        // 创建摇杆
        manager.getAllJoystickConfigs().forEach { config ->
            val joystick = JoystickView(buttonContainer.context).apply {
                stickId = config.id
                isLeftStick = config.isLeft
                
                // 设置初始位置
                val (x, y) = manager.getJoystickPosition(config.id)
                setAbsolutePosition(x, y)
                
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
        
        // 创建方向键
        val (dpadX, dpadY) = manager.getDpadPosition()
        dpadView = DpadView(buttonContainer.context).apply {
            setAbsolutePosition(dpadX, dpadY)
            
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
                val (x, y) = manager.getButtonPosition(config.id)
                setAbsolutePosition(x, y)
                
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
    
    private fun abs(value: Int): Int = if (value < 0) -value else value
    
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
                    
                    joystick.setAbsolutePosition(clampedX, clampedY)
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
                    
                    dpad.setAbsolutePosition(clampedX, clampedY)
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
                    
                    button.setAbsolutePosition(clampedX, clampedY)
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
        
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {
                virtualButtons.values.find { it.buttonId == keyCode }?.buttonPressed = true
                RyujinxNative.jnaInstance.inputSetButtonPressed(keyCode, controllerId)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                virtualButtons.values.find { it.buttonId == keyCode }?.buttonPressed = false
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
        
        // 保存按钮位置
        virtualButtons.forEach { (buttonId, button) ->
            val (x, y) = button.getAbsolutePosition()
            manager.saveButtonPosition(buttonId, x, y)
        }
        
        // 保存摇杆位置
        virtualJoysticks.forEach { (joystickId, joystick) ->
            val (x, y) = joystick.getAbsolutePosition()
            manager.saveJoystickPosition(joystickId, x, y)
        }
        
        // 保存方向键位置
        dpadView?.let { dpad ->
            val (x, y) = dpad.getAbsolutePosition()
            manager.saveDpadPosition(x, y)
        }
    }
    
    private fun clearSavedLayout() {
        val prefs = activity.getSharedPreferences("virtual_controls", Context.MODE_PRIVATE)
        prefs.edit().clear().apply()
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
