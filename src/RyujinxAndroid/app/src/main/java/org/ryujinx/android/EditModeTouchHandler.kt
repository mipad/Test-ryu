// SPDX-FileCopyrightText: Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

package org.ryujinx.android

import android.view.MotionEvent
import android.view.ViewGroup
import androidx.core.math.MathUtils

/**
 * 编辑模式触摸处理器 - 专门处理编辑模式下的控件拖拽
 */
class EditModeTouchHandler(private val gameController: GameController) {
    
    private var draggingView: Any? = null
    private var dragPointerId = -1
    
    /**
     * 处理编辑模式下的触摸事件
     */
    fun handleEditModeTouch(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                return handleEditModeTouchDown(event, 0)
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                val pointerIndex = event.actionIndex
                return handleEditModeTouchDown(event, pointerIndex)
            }
            MotionEvent.ACTION_MOVE -> {
                return handleEditModeTouchMove(event)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                return handleEditModeTouchUp(event)
            }
            MotionEvent.ACTION_CANCEL -> {
                return handleEditModeTouchCancel()
            }
        }
        return false
    }
    
    /**
     * 处理编辑模式下的触摸按下事件
     */
    private fun handleEditModeTouchDown(event: MotionEvent, pointerIndex: Int): Boolean {
        if (draggingView != null) return false // 已经在拖拽中
        
        val pointerId = event.getPointerId(pointerIndex)
        val x = event.getX(pointerIndex)
        val y = event.getY(pointerIndex)
        
        // 检查所有控件，找出被触摸的控件
        gameController.virtualButtons.values.forEach { button ->
            if (isPointInView(x, y, button) && button.isVisible) {
                draggingView = button
                dragPointerId = pointerId
                // 开始拖拽按钮
                return true
            }
        }
        
        gameController.virtualJoysticks.values.forEach { joystick ->
            if (isPointInView(x, y, joystick) && joystick.isVisible) {
                draggingView = joystick
                dragPointerId = pointerId
                // 开始拖拽摇杆
                return true
            }
        }
        
        gameController.virtualCombinations.values.forEach { combination ->
            if (isPointInView(x, y, combination) && combination.isVisible) {
                draggingView = combination
                dragPointerId = pointerId
                // 开始拖拽组合按键
                return true
            }
        }
        
        gameController.dpadView?.let { dpad ->
            if (isPointInView(x, y, dpad) && dpad.isVisible) {
                draggingView = dpad
                dragPointerId = pointerId
                // 开始拖拽方向键
                return true
            }
        }
        
        return false
    }
    
    /**
     * 处理编辑模式下的触摸移动事件
     */
    private fun handleEditModeTouchMove(event: MotionEvent): Boolean {
        if (draggingView == null) return false
        
        val pointerIndex = event.findPointerIndex(dragPointerId)
        if (pointerIndex == -1) return false
        
        val x = event.getX(pointerIndex)
        val y = event.getY(pointerIndex)
        
        val parent = gameController.buttonContainer ?: return false
        
        // 转换为容器坐标
        val containerX = x.toInt()
        val containerY = y.toInt()
        
        // 根据拖拽的控件类型执行相应的拖拽逻辑
        when (val view = draggingView) {
            is ButtonOverlayView -> {
                handleButtonDrag(view, containerX, containerY, parent)
            }
            is JoystickOverlayView -> {
                handleJoystickDrag(view, containerX, containerY, parent)
            }
            is CombinationOverlayView -> {
                handleCombinationDrag(view, containerX, containerY, parent)
            }
            is DpadOverlayView -> {
                handleDpadDrag(view, containerX, containerY, parent)
            }
        }
        
        return true
    }
    
    /**
     * 处理编辑模式下的触摸抬起事件
     */
    private fun handleEditModeTouchUp(event: MotionEvent): Boolean {
        val wasDragging = draggingView != null
        draggingView = null
        dragPointerId = -1
        return wasDragging
    }
    
    /**
     * 处理编辑模式下的触摸取消事件
     */
    private fun handleEditModeTouchCancel(): Boolean {
        val wasDragging = draggingView != null
        draggingView = null
        dragPointerId = -1
        return wasDragging
    }
    
    /**
     * 处理按钮拖拽
     */
    private fun handleButtonDrag(button: ButtonOverlayView, x: Int, y: Int, parent: ViewGroup) {
        val clampedX = MathUtils.clamp(x, 0, parent.width)
        val clampedY = MathUtils.clamp(y, 0, parent.height)
        button.setPosition(clampedX, clampedY)
    }
    
    /**
     * 处理摇杆拖拽
     */
    private fun handleJoystickDrag(joystick: JoystickOverlayView, x: Int, y: Int, parent: ViewGroup) {
        val clampedX = MathUtils.clamp(x, 0, parent.width)
        val clampedY = MathUtils.clamp(y, 0, parent.height)
        joystick.setPosition(clampedX, clampedY)
    }
    
    /**
     * 处理组合按键拖拽
     */
    private fun handleCombinationDrag(combination: CombinationOverlayView, x: Int, y: Int, parent: ViewGroup) {
        val clampedX = MathUtils.clamp(x, 0, parent.width)
        val clampedY = MathUtils.clamp(y, 0, parent.height)
        combination.setPosition(clampedX, clampedY)
    }
    
    /**
     * 处理方向键拖拽
     */
    private fun handleDpadDrag(dpad: DpadOverlayView, x: Int, y: Int, parent: ViewGroup) {
        val clampedX = MathUtils.clamp(x, 0, parent.width)
        val clampedY = MathUtils.clamp(y, 0, parent.height)
        dpad.setPosition(clampedX, clampedY)
    }
    
    /**
     * 判断触摸点是否在视图内
     */
    private fun isPointInView(x: Float, y: Float, view: android.view.View): Boolean {
        val location = IntArray(2)
        view.getLocationOnScreen(location)
        val left = location[0]
        val top = location[1]
        val right = left + view.width
        val bottom = top + view.height
        
        val containerLocation = IntArray(2)
        gameController.buttonContainer?.getLocationOnScreen(containerLocation)
        val screenX = x + containerLocation[0]
        val screenY = y + containerLocation[1]
        
        return screenX >= left && screenX <= right && screenY >= top && screenY <= bottom
    }
    
    /**
     * 重置拖拽状态
     */
    fun reset() {
        draggingView = null
        dragPointerId = -1
    }
}