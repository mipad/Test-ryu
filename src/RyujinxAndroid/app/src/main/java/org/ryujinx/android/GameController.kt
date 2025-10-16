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
import android.view.animation.Animation
import android.view.animation.ScaleAnimation
import android.widget.FrameLayout
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
import java.util.*

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
        color = Color.argb(180, 255, 255, 255)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val innerCirclePaint = Paint().apply {
        color = Color.argb(128, 100, 100, 255)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val pressedPaint = Paint().apply {
        color = Color.argb(200, 255, 100, 100)
        style = Paint.Style.FILL
        isAntiAlias = true
    }
    
    private val textPaint = Paint().apply {
        color = Color.WHITE
        textSize = 18f
        textAlign = Paint.Align.CENTER
        typeface = Typeface.DEFAULT_BOLD
        isAntiAlias = true
    }
    
    private var lastTouchX = 0f
    private var lastTouchY = 0f
    private var isDragging = false
    
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        
        val centerX = width / 2f
        val centerY = height / 2f
        val radius = (width.coerceAtMost(height) / 2f) * 0.8f
        
        // 绘制外圈
        canvas.drawCircle(centerX, centerY, radius, outerCirclePaint)
        
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

// 按键管理器
class ButtonLayoutManager(private val context: Context) {
    private val prefs = context.getSharedPreferences("virtual_buttons", Context.MODE_PRIVATE)
    
    private val buttonConfigs = listOf(
        ButtonConfig(1, "A", 0.8f, 0.7f, GamePadButtonInputId.A.ordinal),
        ButtonConfig(2, "B", 0.9f, 0.6f, GamePadButtonInputId.B.ordinal),
        ButtonConfig(3, "X", 0.7f, 0.6f, GamePadButtonInputId.X.ordinal),
        ButtonConfig(4, "Y", 0.8f, 0.5f, GamePadButtonInputId.Y.ordinal),
        ButtonConfig(5, "L", 0.1f, 0.2f, GamePadButtonInputId.LeftShoulder.ordinal),
        ButtonConfig(6, "R", 0.9f, 0.2f, GamePadButtonInputId.RightShoulder.ordinal),
        ButtonConfig(7, "ZL", 0.1f, 0.1f, GamePadButtonInputId.LeftTrigger.ordinal),
        ButtonConfig(8, "ZR", 0.9f, 0.1f, GamePadButtonInputId.RightTrigger.ordinal),
        ButtonConfig(9, "L3", 0.2f, 0.8f, GamePadButtonInputId.LeftStickButton.ordinal),
        ButtonConfig(10, "R3", 0.8f, 0.8f, GamePadButtonInputId.RightStickButton.ordinal),
        ButtonConfig(11, "+", 0.5f, 0.2f, GamePadButtonInputId.Plus.ordinal),
        ButtonConfig(12, "-", 0.5f, 0.3f, GamePadButtonInputId.Minus.ordinal)
    )
    
    fun getButtonPosition(buttonId: Int, containerWidth: Int, containerHeight: Int): Pair<Int, Int> {
        val xPref = prefs.getFloat("button_${buttonId}_x", -1f)
        val yPref = prefs.getFloat("button_${buttonId}_y", -1f)
        
        val config = buttonConfigs.find { it.id == buttonId } ?: return Pair(0, 0)
        
        val x = if (xPref != -1f) (xPref * containerWidth) else (config.defaultX * containerWidth)
        val y = if (yPref != -1f) (yPref * containerHeight) else (config.defaultY * containerHeight)
        
        return Pair(x.toInt(), y.toInt())
    }
    
    fun saveButtonPosition(buttonId: Int, x: Int, y: Int, containerWidth: Int, containerHeight: Int) {
        val xNormalized = x.toFloat() / containerWidth
        val yNormalized = y.toFloat() / containerHeight
        
        prefs.edit()
            .putFloat("button_${buttonId}_x", xNormalized)
            .putFloat("button_${buttonId}_y", yNormalized)
            .apply()
    }
    
    fun getAllButtonConfigs(): List<ButtonConfig> = buttonConfigs
}

class GameController(var activity: Activity) {

    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            
            // 获取按钮容器
            val buttonContainer = view.findViewById<FrameLayout>(R.id.buttonContainer)!!
            
            // 初始化按钮管理器
            controller.buttonLayoutManager = ButtonLayoutManager(context)
            
            // 创建所有虚拟按钮
            controller.createVirtualButtons(buttonContainer)
            
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
            
            AndroidView(
                modifier = Modifier.fillMaxSize(), 
                factory = { context ->
                    val controller = GameController(viewModel.activity)
                    val c = Create(context, controller)
                    controller.controllerView = c
                    controller.setEditingMode(false)
                    viewModel.setGameController(controller)
                    controller.setVisible(QuickSettings(viewModel.activity).useVirtualController)
                    c
                }
            )
            
            // 编辑模式下的保存按钮
            if (isEditing) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(ComposeColor.Black.copy(alpha = 0.3f)),
                    contentAlignment = Alignment.Center
                ) {
                    Surface(
                        shape = RoundedCornerShape(16.dp),
                        color = MaterialTheme.colorScheme.surface,
                        modifier = Modifier.padding(16.dp)
                    ) {
                        Column(
                            modifier = Modifier.padding(24.dp),
                            horizontalAlignment = Alignment.CenterHorizontally
                        ) {
                            Text(
                                text = "编辑模式",
                                style = MaterialTheme.typography.headlineSmall,
                                modifier = Modifier.padding(bottom = 16.dp)
                            )
                            Text(
                                text = "拖动按钮到合适位置，然后点击保存",
                                style = MaterialTheme.typography.bodyMedium,
                                modifier = Modifier.padding(bottom = 24.dp)
                            )
                            Button(
                                onClick = {
                                    isEditing = false
                                    viewModel.controller?.setEditingMode(false)
                                    viewModel.controller?.saveButtonLayout()
                                }
                            ) {
                                Text(text = "保存布局")
                            }
                        }
                    }
                }
            }
        }
    }

    private var controllerView: View? = null
    var buttonLayoutManager: ButtonLayoutManager? = null
    private val virtualButtons = mutableMapOf<Int, DraggableButtonView>()
    var controllerId: Int = -1
    private var isEditing = false
    private var containerWidth = 0
    private var containerHeight = 0

    val isVisible: Boolean
        get() {
            controllerView?.apply {
                return this.isVisible
            }
            return false
        }

    init {
        // 移除原有的 RadialGamePad 相关代码
    }

    private fun createVirtualButtons(buttonContainer: FrameLayout) {
        val manager = buttonLayoutManager ?: return
        
        // 测量容器尺寸
        buttonContainer.post {
            containerWidth = buttonContainer.width
            containerHeight = buttonContainer.height
            
            manager.getAllButtonConfigs().forEach { config ->
                val button = DraggableButtonView(buttonContainer.context).apply {
                    buttonId = config.id
                    buttonText = config.text
                    
                    // 设置初始位置
                    val (x, y) = manager.getButtonPosition(config.id, containerWidth, containerHeight)
                    setPosition(x, y)
                    
                    // 设置触摸监听器
                    setOnTouchListener { _, event ->
                        if (isEditing) {
                            // 编辑模式：可拖拽
                            handleDragEvent(event, config.id)
                        } else {
                            // 游戏模式：发送按键事件
                            handleButtonEvent(event, config.keyCode)
                        }
                        true
                    }
                }
                
                // 添加到全屏容器
                buttonContainer.addView(button)
                
                virtualButtons[config.id] = button
            }
        }
    }
    
    private fun handleDragEvent(event: MotionEvent, buttonId: Int): Boolean {
        when (event.action) {
            MotionEvent.ACTION_DOWN -> {
                // 开始拖动
                virtualButtons[buttonId]?.let { button ->
                    button.buttonPressed = true
                }
            }
            MotionEvent.ACTION_MOVE -> {
                // 更新按钮位置
                virtualButtons[buttonId]?.let { button ->
                    val parent = button.parent as? ViewGroup ?: return@let
                    val x = event.rawX - parent.left
                    val y = event.rawY - parent.top
                    
                    // 限制在屏幕范围内
                    val clampedX = MathUtils.clamp(x, 0f, parent.width.toFloat()).toInt()
                    val clampedY = MathUtils.clamp(y, 0f, parent.height.toFloat()).toInt()
                    
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
        virtualButtons.values.forEach { button ->
            button.buttonPressed = false
        }
    }
    
    fun saveButtonLayout() {
        val manager = buttonLayoutManager ?: return
        virtualButtons.forEach { (buttonId, button) ->
            val (x, y) = button.getPosition()
            manager.saveButtonPosition(buttonId, x, y, containerWidth, containerHeight)
        }
    }

    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible
            virtualButtons.values.forEach { it.isVisible = isVisible }

            if (isVisible)
                connect()
        }
    }

    fun connect() {
        if (controllerId == -1)
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
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
