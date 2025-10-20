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

// ... 其他类保持不变 ...

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
        
        // 重新创建控件，但不设置位置
        createControlsWithoutPositions(buttonContainer, manager)
        
        // 使用与 refreshControlPositions 相同的逻辑设置位置
        refreshControlPositions()
    }
    
    private fun createControlsWithoutPositions(buttonContainer: FrameLayout, manager: ButtonLayoutManager) {
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
    }
    
    fun setControlEnabled(controlId: Int, enabled: Boolean) {
        when {
            controlId in 1..12 -> buttonLayoutManager?.setButtonEnabled(controlId, enabled)
            controlId in 101..102 -> buttonLayoutManager?.setJoystickEnabled(controlId, enabled)
            controlId == 201 -> buttonLayoutManager?.setDpadEnabled(enabled)
        }
        refreshControls()
    }
    
    fun setControlOpacity(controlId: Int, opacity: Int) {
        when {
            controlId in 1..12 -> {
                buttonLayoutManager?.setButtonOpacity(controlId, opacity)
                virtualButtons[controlId]?.opacity = (opacity * 255 / 100)
            }
            controlId in 101..102 -> {
                buttonLayoutManager?.setJoystickOpacity(controlId, opacity)
                virtualJoysticks[controlId]?.opacity = (opacity * 255 / 100)
            }
            controlId == 201 -> {
                buttonLayoutManager?.setDpadOpacity(opacity)
                dpadView?.opacity = (opacity * 255 / 100)
            }
        }
    }
    
    fun setControlScale(controlId: Int, scale: Int) {
        when {
            controlId in 1..12 -> buttonLayoutManager?.setButtonScale(controlId, scale)
            controlId in 101..102 -> buttonLayoutManager?.setJoystickScale(controlId, scale)
            controlId == 201 -> buttonLayoutManager?.setDpadScale(scale)
        }
        refreshControls()
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
    
    // ... 其他方法保持不变 ...
}
