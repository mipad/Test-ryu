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
import org.json.JSONObject
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings

typealias GamePad = RadialGamePad
typealias GamePadConfig = RadialGamePadConfig

// 控制器布局数据类
data class ControllerLayout(
    val scale: Float = 1.0f,
    val alpha: Float = 1.0f,
    val positionX: Int = 0,
    val positionY: Int = 0
)

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
    
    // 编辑模式属性
    var editMode = false
    var buttonScale = 1.0f
    var buttonAlpha = 1.0f
    
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
        val outerRadius = (width.coerceAtMost(height) / 2f) * 0.9f * buttonScale
        val innerRadius = outerRadius * 0.6f
        
        // 设置透明度
        outerCirclePaint.alpha = (255 * buttonAlpha).toInt()
        innerCirclePaint.alpha = (128 * buttonAlpha).toInt()
        textPaint.alpha = (255 * buttonAlpha).toInt()
        
        // 绘制外圈
        canvas.drawCircle(centerX, centerY, outerRadius, outerCirclePaint)
        
        // 绘制内圈
        canvas.drawCircle(centerX, centerY, innerRadius, innerCirclePaint)
        
        // 绘制文字
        val textY = centerY - (textPaint.descent() + textPaint.ascent()) / 2
        canvas.drawText(buttonText, centerX, textY, textPaint)
        
        // 编辑模式下显示边框
        if (editMode) {
            val editPaint = Paint().apply {
                color = Color.YELLOW
                style = Paint.Style.STROKE
                strokeWidth = 3f
                isAntiAlias = true
            }
            canvas.drawRect(0f, 0f, width.toFloat(), height.toFloat(), editPaint)
        }
    }
    
    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val baseSize = dpToPx(70) 
        val scaledSize = (baseSize * buttonScale).toInt()
        setMeasuredDimension(scaledSize, scaledSize)
    }
    
    private fun dpToPx(dp: Int): Int {
        return TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, 
            dp.toFloat(), 
            resources.displayMetrics
        ).toInt()
    }
    
    fun setScale(scale: Float) {
        buttonScale = scale
        requestLayout()
        invalidate()
    }
    
    fun setAlpha(alpha: Float) {
        buttonAlpha = alpha
        invalidate()
    }
}

class GameController(var activity: Activity) {

    companion object {
        private fun Create(context: Context, controller: GameController, editMode: Boolean): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            
            // 设置编辑模式
            controller.editMode = editMode
            controller.leftGamePad.setEditMode(editMode)
            controller.rightGamePad.setEditMode(editMode)
            
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(controller.leftGamePad)
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(controller.rightGamePad)

            // 添加L3按钮
            val l3Button = DoubleCircleButtonView(context, "L3", GamePadButtonInputId.LeftStickButton.ordinal).apply {
                editMode = editMode
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            if (!editMode) {
                                setPressedState(true)
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.LeftStickButton.ordinal,
                                    controller.controllerId
                                )
                            }
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            if (!editMode) {
                                setPressedState(false)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.LeftStickButton.ordinal,
                                    controller.controllerId
                                )
                            }
                            true
                        }
                        else -> false
                    }
                }
                
                // 编辑模式下的触摸监听
                if (editMode) {
                    setOnTouchListener { _, event ->
                        when (event.action) {
                            KeyEvent.ACTION_DOWN -> {
                                controller.selectedButton = this
                                controller.showButtonEditor = true
                                true
                            }
                            else -> false
                        }
                    }
                }
            }
            
            // 添加R3按钮
            val r3Button = DoubleCircleButtonView(context, "R3", GamePadButtonInputId.RightStickButton.ordinal).apply {
                editMode = editMode
                setOnTouchListener { _, event ->
                    when (event.action) {
                        KeyEvent.ACTION_DOWN -> {
                            if (!editMode) {
                                setPressedState(true)
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.RightStickButton.ordinal,
                                    controller.controllerId
                                )
                            }
                            true
                        }
                        KeyEvent.ACTION_UP -> {
                            if (!editMode) {
                                setPressedState(false)
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.RightStickButton.ordinal,
                                    controller.controllerId
                                )
                            }
                            true
                        }
                        else -> false
                    }
                }
                
                // 编辑模式下的触摸监听
                if (editMode) {
                    setOnTouchListener { _, event ->
                        when (event.action) {
                            KeyEvent.ACTION_DOWN -> {
                                controller.selectedButton = this
                                controller.showButtonEditor = true
                                true
                            }
                            else -> false
                        }
                    }
                }
            }
            
            // 设置L3和R3按钮的布局参数
            val l3LayoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.START
                topMargin = dpToPx(context, 30) // 距离顶部30dp，靠近边缘
                leftMargin = dpToPx(context, 280) // 距离左侧280dp，位于L键右边
            }
            l3Button.layoutParams = l3LayoutParams
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(l3Button)
            controller.l3Button = l3Button

            val r3LayoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                gravity = android.view.Gravity.TOP or android.view.Gravity.END
                topMargin = dpToPx(context, 30) // 距离顶部30dp，靠近边缘
                rightMargin = dpToPx(context, 280) // 距离右侧280dp，位于R键左边
            }
            r3Button.layoutParams = r3LayoutParams
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(r3Button)
            controller.r3Button = r3Button
            
            // 加载保存的布局
            controller.loadControllerLayout()

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
        fun Compose(viewModel: MainViewModel, editMode: Boolean = false): Unit {
            AndroidView(
                modifier = Modifier.fillMaxSize(), factory = { context ->
                    val controller = GameController(viewModel.activity)
                    val c = Create(context, controller, editMode)
                    if (!editMode) {
                        viewModel.activity.lifecycleScope.apply {
                            viewModel.activity.lifecycleScope.launch {
                                val events = merge(
                                    controller.leftGamePad.events(),
                                    controller.rightGamePad.events()
                                )
                                    .shareIn(viewModel.activity.lifecycleScope, SharingStarted.Lazily)
                                events.safeCollect {
                                    controller.handleEvent(it)
                                }
                            }
                        }
                    }
                    controller.controllerView = c
                    viewModel.setGameController(controller)
                    controller.setVisible(QuickSettings(viewModel.activity).useVirtualController)
                    c
                })
        }
    }

    private var controllerView: View? = null
    var leftGamePad: GamePad
    var rightGamePad: GamePad
    var l3Button: DoubleCircleButtonView? = null
    var r3Button: DoubleCircleButtonView? = null
    var controllerId: Int = -1
    var editMode = false
    var selectedButton: DoubleCircleButtonView? = null
    var showButtonEditor = false
    
    // 控制器布局数据
    private var controllerLayout = mutableMapOf<String, ControllerLayout>()
    
    val isVisible: Boolean
        get() {
            controllerView?.apply {
                return this.isVisible
            }
            return false
        }

    init {
        leftGamePad = GamePad(generateConfig(true), 16f, activity)
        rightGamePad = GamePad(generateConfig(false), 16f, activity)

        leftGamePad.primaryDialMaxSizeDp = 200f
        rightGamePad.primaryDialMaxSizeDp = 200f

        leftGamePad.gravityX = -1f
        leftGamePad.gravityY = 1f
        rightGamePad.gravityX = 1f
        rightGamePad.gravityY = 1f
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
    
    // 保存控制器布局
    fun saveControllerLayout() {
        val prefs = activity.getSharedPreferences("controller_layout", Context.MODE_PRIVATE)
        val editor = prefs.edit()
        
        // 获取当前游戏ID，如果没有则使用"default"
        val gameId = getCurrentGameId() ?: "default"
        
        // 构建布局数据
        val layoutData = JSONObject()
        val buttonsData = JSONObject()
        
        // 保存L3按钮布局
        l3Button?.let { button ->
            val layout = JSONObject()
            layout.put("scale", button.buttonScale)
            layout.put("alpha", button.buttonAlpha)
            layout.put("x", (button.layoutParams as? FrameLayout.LayoutParams)?.leftMargin ?: 0)
            layout.put("y", (button.layoutParams as? FrameLayout.LayoutParams)?.topMargin ?: 0)
            buttonsData.put("l3", layout)
        }
        
        // 保存R3按钮布局
        r3Button?.let { button ->
            val layout = JSONObject()
            layout.put("scale", button.buttonScale)
            layout.put("alpha", button.buttonAlpha)
            layout.put("x", (button.layoutParams as? FrameLayout.LayoutParams)?.rightMargin ?: 0)
            layout.put("y", (button.layoutParams as? FrameLayout.LayoutParams)?.topMargin ?: 0)
            buttonsData.put("r3", layout)
        }
        
        layoutData.put("buttons", buttonsData)
        layoutData.put("gameId", gameId)
        
        editor.putString("layout_$gameId", layoutData.toString())
        editor.apply()
    }
    
    // 加载控制器布局
    fun loadControllerLayout() {
        val prefs = activity.getSharedPreferences("controller_layout", Context.MODE_PRIVATE)
        val gameId = getCurrentGameId() ?: "default"
        
        // 先尝试加载当前游戏的布局
        var layoutJson = prefs.getString("layout_$gameId", null)
        
        // 如果没有当前游戏的布局，则加载默认布局
        if (layoutJson == null) {
            layoutJson = prefs.getString("layout_default", null)
        }
        
        layoutJson?.let { jsonString ->
            try {
                val layoutData = JSONObject(jsonString)
                val buttonsData = layoutData.getJSONObject("buttons")
                
                // 加载L3按钮布局
                if (buttonsData.has("l3")) {
                    val l3Layout = buttonsData.getJSONObject("l3")
                    l3Button?.let { button ->
                        button.setScale(l3Layout.getDouble("scale").toFloat())
                        button.setAlpha(l3Layout.getDouble("alpha").toFloat())
                        
                        val params = button.layoutParams as? FrameLayout.LayoutParams
                        params?.leftMargin = l3Layout.getInt("x")
                        params?.topMargin = l3Layout.getInt("y")
                        button.layoutParams = params
                    }
                }
                
                // 加载R3按钮布局
                if (buttonsData.has("r3")) {
                    val r3Layout = buttonsData.getJSONObject("r3")
                    r3Button?.let { button ->
                        button.setScale(r3Layout.getDouble("scale").toFloat())
                        button.setAlpha(r3Layout.getDouble("alpha").toFloat())
                        
                        val params = button.layoutParams as? FrameLayout.LayoutParams
                        params?.rightMargin = r3Layout.getInt("x")
                        params?.topMargin = r3Layout.getInt("y")
                        button.layoutParams = params
                    }
                }
            } catch (e: Exception) {
                e.printStackTrace()
            }
        }
    }
    
    // 获取当前游戏ID（需要根据您的游戏识别逻辑实现）
    private fun getCurrentGameId(): String? {
        // 这里需要根据您的游戏识别逻辑返回游戏ID
        // 例如从MainViewModel或RyujinxNative获取
        return try {
            // 假设有一个获取当前游戏标题或ID的方法
            // 返回游戏标题的hashCode作为ID，或者使用其他唯一标识
            "game_${System.currentTimeMillis()}" // 临时实现
        } catch (e: Exception) {
            null
        }
    }

    private fun handleEvent(ev: Event) {
        if (editMode) return
        
        if (controllerId == -1)
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)

        controllerId.apply {
            when (ev) {
                is Event.Button -> {
                    val action = ev.action
                    when (action) {
                        KeyEvent.ACTION_UP -> {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(ev.id, this)
                        }

                        KeyEvent.ACTION_DOWN -> {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(ev.id, this)
                        }
                    }
                }

                is Event.Direction -> {
                    val direction = ev.id

                    when (direction) {
                        GamePadButtonInputId.DpadUp.ordinal -> {
                            if (ev.xAxis > 0) {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.DpadRight.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadLeft.ordinal,
                                    this
                                )
                            } else if (ev.xAxis < 0) {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.DpadLeft.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadRight.ordinal,
                                    this
                                )
                            } else {
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadLeft.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadRight.ordinal,
                                    this
                                )
                            }
                            if (ev.yAxis < 0) {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.DpadUp.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadDown.ordinal,
                                    this
                                )
                            } else if (ev.yAxis > 0) {
                                RyujinxNative.jnaInstance.inputSetButtonPressed(
                                    GamePadButtonInputId.DpadDown.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadUp.ordinal,
                                    this
                                )
                            } else {
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadDown.ordinal,
                                    this
                                )
                                RyujinxNative.jnaInstance.inputSetButtonReleased(
                                    GamePadButtonInputId.DpadUp.ordinal,
                                    this
                                )
                            }
                        }

                        GamePadButtonInputId.LeftStick.ordinal -> {
                            val setting = QuickSettings(activity)
                            val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                            val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                            RyujinxNative.jnaInstance.inputSetStickAxis(
                                1,
                                x,
                                -y,
                                this
                            )
                        }

                        GamePadButtonInputId.RightStick.ordinal -> {
                            val setting = QuickSettings(activity)
                            val x = MathUtils.clamp(ev.xAxis * setting.controllerStickSensitivity, -1f, 1f)
                            val y = MathUtils.clamp(ev.yAxis * setting.controllerStickSensitivity, -1f, 1f)
                            RyujinxNative.jnaInstance.inputSetStickAxis(
                                2,
                                x,
                                -y,
                                this
                            )
                        }
                    }
                }
            }
        }
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

private fun generateConfig(isLeft: Boolean): GamePadConfig {
    val distance = 0.3f
    val buttonScale = 1f

    if (isLeft) {
        return GamePadConfig(
            12,
            PrimaryDialConfig.Stick(
                GamePadButtonInputId.LeftStick.ordinal,
                GamePadButtonInputId.LeftStickButton.ordinal,
                setOf(),
                "LeftStick",
                null
            ),
            listOf(
                SecondaryDialConfig.Cross(
                    10,
                    3,
                    2.5f,
                    distance,
                    CrossConfig(
                        GamePadButtonInputId.DpadUp.ordinal,
                        CrossConfig.Shape.STANDARD,
                        null,
                        setOf(),
                        CrossContentDescription(),
                        true,
                        null
                    ),
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.SingleButton(
                    1,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.Minus.ordinal,
                        "-",
                        true,
                        null,
                        "Minus",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.DoubleButton(
                    2,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.LeftShoulder.ordinal,
                        "L",
                        true,
                        null,
                        "LeftBumper",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.SingleButton(
                    9,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.LeftTrigger.ordinal,
                        "ZL",
                        true,
                        null,
                        "LeftTrigger",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
            )
        )
    } else {
        return GamePadConfig(
            12,
            PrimaryDialConfig.PrimaryButtons(
                listOf(
                    ButtonConfig(
                        GamePadButtonInputId.A.ordinal,
                        "A",
                        true,
                        null,
                        "A",
                        setOf(),
                        true,
                        null
                    ),
                    ButtonConfig(
                        GamePadButtonInputId.X.ordinal,
                        "X",
                        true,
                        null,
                        "X",
                        setOf(),
                        true,
                        null
                    ),
                    ButtonConfig(
                        GamePadButtonInputId.Y.ordinal,
                        "Y",
                        true,
                        null,
                        "Y",
                        setOf(),
                        true,
                        null
                    ),
                    ButtonConfig(
                        GamePadButtonInputId.B.ordinal,
                        "B",
                        true,
                        null,
                        "B",
                        setOf(),
                        true,
                        null
                    )
                ),
                null,
                0f,
                true,
                null
            ),
            listOf(
                SecondaryDialConfig.Stick(
                    7,
                    2,
                    2f,
                    distance,
                    GamePadButtonInputId.RightStick.ordinal,
                    GamePadButtonInputId.RightStickButton.ordinal,
                    null,
                    setOf(),
                    "RightStick",
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.SingleButton(
                    6,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.Plus.ordinal,
                        "+",
                        true,
                        null,
                        "Plus",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.DoubleButton(
                    3,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.RightShoulder.ordinal,
                        "R",
                        true,
                        null,
                        "RightBumper",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                ),
                SecondaryDialConfig.SingleButton(
                    9,
                    buttonScale,
                    distance,
                    ButtonConfig(
                        GamePadButtonInputId.RightTrigger.ordinal,
                        "ZR",
                        true,
                        null,
                        "RightTrigger",
                        setOf(),
                        true,
                        null
                    ),
                    null,
                    SecondaryDialConfig.RotationProcessor()
                )
            )
        )
    }
}
