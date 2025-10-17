package org.ryujinx.android

import android.app.Activity
import android.content.Context
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
import androidx.core.math.MathUtils
import androidx.core.view.isVisible
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings

// 摇杆视图 - 使用矢量图资源
class JoystickView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : ImageView(context, attrs, defStyleAttr) {
    
    var stickId: Int = 0
    var isLeftStick: Boolean = true
    var stickX: Float = 0f
    var stickY: Float = 0f
    
    init {
        setImageResource(R.drawable.joystick)
        scaleType = ScaleType.FIT_CENTER
        // 移除任何可能的背景
        setBackgroundResource(0)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(70)
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
        
        // 根据摇杆位置更新视觉反馈
        val maxOffset = width * 0.25f
        translationX = stickX * maxOffset
        translationY = stickY * maxOffset
        
        // 按压状态改变图片 - 确保使用矢量图资源
        if (stickX != 0f || stickY != 0f) {
            setImageResource(R.drawable.joystick_depressed)
        } else {
            setImageResource(R.drawable.joystick)
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

// 方向键视图 - 使用矢量图资源
class DpadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : ImageView(context, attrs, defStyleAttr) {
    
    var currentDirection: DpadDirection = DpadDirection.NONE
    
    enum class DpadDirection {
        NONE, UP, DOWN, LEFT, RIGHT, UP_LEFT, UP_RIGHT, DOWN_LEFT, DOWN_RIGHT
    }
    
    init {
        setImageResource(R.drawable.dpad_standard)
        scaleType = ScaleType.FIT_CENTER
        // 移除任何可能的背景
        setBackgroundResource(0)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(135) // 增大到135dp
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
        
        // 根据方向更新图片资源 - 确保使用矢量图资源
        when (direction) {
            DpadDirection.UP, DpadDirection.DOWN, DpadDirection.LEFT, DpadDirection.RIGHT ->
                setImageResource(R.drawable.dpad_standard_cardinal_depressed)
            DpadDirection.UP_LEFT, DpadDirection.UP_RIGHT, DpadDirection.DOWN_LEFT, DpadDirection.DOWN_RIGHT ->
                setImageResource(R.drawable.dpad_standard_diagonal_depressed)
            else ->
                setImageResource(R.drawable.dpad_standard)
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

// 自定义可拖拽按钮 - 使用矢量图资源
class DraggableButtonView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : ImageView(context, attrs, defStyleAttr) {
    
    var buttonId: Int = 0
    var buttonText: String = ""
    var buttonPressed: Boolean = false
        set(value) {
            field = value
            updateButtonAppearance()
        }
    
    init {
        scaleType = ScaleType.FIT_CENTER
        // 移除任何可能的背景
        setBackgroundResource(0)
        updateButtonAppearance()
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = when (buttonId) {
            5, 6, 7, 8 -> dpToPx(90) // L, R, ZL, ZR 按钮增大到90dp
            9, 10 -> dpToPx(30) // +, - 按钮减小到30dp
            11, 12 -> dpToPx(45) // L3, R3 按钮调整为45dp
            else -> dpToPx(50) // 其他按钮保持50dp
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
    
    private fun updateButtonAppearance() {
        // 根据按钮ID和按压状态设置不同的矢量图资源
        when (buttonId) {
            1 -> { // A按钮
                setImageResource(if (buttonPressed) R.drawable.facebutton_a_depressed else R.drawable.facebutton_a)
            }
            2 -> { // B按钮
                setImageResource(if (buttonPressed) R.drawable.facebutton_b_depressed else R.drawable.facebutton_b)
            }
            3 -> { // X按钮
                setImageResource(if (buttonPressed) R.drawable.facebutton_x_depressed else R.drawable.facebutton_x)
            }
            4 -> { // Y按钮
                setImageResource(if (buttonPressed) R.drawable.facebutton_y_depressed else R.drawable.facebutton_y)
            }
            5 -> { // L按钮
                setImageResource(if (buttonPressed) R.drawable.l_shoulder_depressed else R.drawable.l_shoulder)
            }
            6 -> { // R按钮
                setImageResource(if (buttonPressed) R.drawable.r_shoulder_depressed else R.drawable.r_shoulder)
            }
            7 -> { // ZL按钮
                setImageResource(if (buttonPressed) R.drawable.zl_trigger_depressed else R.drawable.zl_trigger)
            }
            8 -> { // ZR按钮
                setImageResource(if (buttonPressed) R.drawable.zr_trigger_depressed else R.drawable.zr_trigger)
            }
            9 -> { // +按钮
                setImageResource(if (buttonPressed) R.drawable.facebutton_plus_depressed else R.drawable.facebutton_plus)
            }
            10 -> { // -按钮
                setImageResource(if (buttonPressed) R.drawable.facebutton_minus_depressed else R.drawable.facebutton_minus)
            }
            11 -> { // L3按钮
                setImageResource(if (buttonPressed) R.drawable.button_l3_depressed else R.drawable.button_l3)
            }
            12 -> { // R3按钮
                setImageResource(if (buttonPressed) R.drawable.button_r3_depressed else R.drawable.button_r3)
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

// 摇杆范围视图
class JoystickRangeView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : ImageView(context, attrs, defStyleAttr) {
    
    init {
        setImageResource(R.drawable.joystick_range)
        scaleType = ScaleType.FIT_CENTER
        // 移除任何可能的背景
        setBackgroundResource(0)
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val size = dpToPx(140)
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
    private val virtualJoystickRanges = mutableMapOf<Int, JoystickRangeView>()
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
        
        // 创建摇杆范围
        manager.getAllJoystickConfigs().forEach { config ->
            val joystickRange = JoystickRangeView(buttonContainer.context).apply {
                // 设置初始位置
                val (x, y) = manager.getJoystickPosition(config.id, effectiveWidth, effectiveHeight)
                setPosition(x, y)
            }
            
            buttonContainer.addView(joystickRange)
            virtualJoystickRanges[config.id] = joystickRange
        }
        
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
        
        // 创建方向键
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
        
        // 刷新摇杆范围位置
        virtualJoystickRanges.forEach { (joystickId, joystickRange) ->
            val (x, y) = manager.getJoystickPosition(joystickId, containerWidth, containerHeight)
            joystickRange.setPosition(x, y)
        }
        
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
        
        // 创建保存按钮 - 设置为半透明
        saveButton = Button(editModeContainer.context).apply {
            text = "保存布局"
            setBackgroundColor(android.graphics.Color.argb(150, 0, 100, 200)) // 半透明背景
            setTextColor(android.graphics.Color.WHITE)
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
                    virtualJoystickRanges[joystickId]?.setPosition(clampedX, clampedY)
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
                        dpad.updateDirection(direction)
                        handleDpadDirection(direction, true)
                    }
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    handleDpadDirection(dpad.currentDirection, false)
                    dpad.currentDirection = DpadView.DpadDirection.NONE
                    dpad.updateDirection(DpadView.DpadDirection.NONE)
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
        dpadView?.updateDirection(DpadView.DpadDirection.NONE)
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
            virtualJoystickRanges.values.forEach { it.isVisible = isVisible }
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
