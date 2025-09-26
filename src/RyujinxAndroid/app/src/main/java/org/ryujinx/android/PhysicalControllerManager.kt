package org.ryujinx.android

import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import org.ryujinx.android.viewmodels.QuickSettings
import org.ryujinx.android.viewmodels.SettingsViewModel

class PhysicalControllerManager(val activity: MainActivity) {
    private var controllerId: Int = -1
    private var currentControllerType: ControllerType = ControllerType.PRO_CONTROLLER
    private var currentPlayerIndex: Int = 0 // 新增：当前玩家索引，默认为0（玩家1）
    private var settingsViewModel: SettingsViewModel? = null
    
    // 设置SettingsViewModel引用
    fun setSettingsViewModel(viewModel: SettingsViewModel) {
        this.settingsViewModel = viewModel
    }

    // 新增方法：从设置更新玩家索引
    fun updatePlayerIndexFromSettings() {
        // 检查掌机模式是否启用
        val handheldSetting = settingsViewModel?.getPlayerSetting(8)
        val isHandheldConnected = handheldSetting?.isConnected ?: false
        
        // 检查玩家1是否启用
        val player1Setting = settingsViewModel?.getPlayerSetting(0)
        val isPlayer1Connected = player1Setting?.isConnected ?: true // 默认启用玩家1
        
        // 决定使用哪个玩家索引
        val newPlayerIndex = if (isHandheldConnected) {
            8 // 掌机模式
        } else if (isPlayer1Connected) {
            0 // 玩家1
        } else {
            // 如果两者都没启用，默认使用玩家1
            0
        }
        
        if (currentPlayerIndex != newPlayerIndex) {
            currentPlayerIndex = newPlayerIndex
            android.util.Log.d("PhysicalControllerManager", "Updated player index to: $currentPlayerIndex (Handheld: $isHandheldConnected, Player1: $isPlayer1Connected)")
            
            // 如果控制器已连接，重新连接以应用新的玩家索引
            if (controllerId != -1) {
                disconnect()
                connect()
            }
        }
    }

    // 新增方法：更新控制器类型
    fun updateControllerType(controllerType: ControllerType) {
        if (currentControllerType == controllerType) {
            return // 类型相同，不需要更新
        }
        
        currentControllerType = controllerType
        
        // 如果控制器已连接，立即应用新的控制器类型
        if (controllerId != -1) {
            // 将控制器类型转换为位掩码值
            val controllerTypeBitmask = controllerTypeToBitmask(controllerType)
            RyujinxNative.jnaInstance.setControllerType(currentPlayerIndex, controllerTypeBitmask)
        }
        
        // 同时更新ControllerManager中的控制器类型
        val deviceId = "physical_controller_$controllerId"
        ControllerManager.updateControllerType(activity, deviceId, controllerType)
        
        // 更新设置中的控制器类型（针对当前玩家索引）
        settingsViewModel?.getPlayerSetting(currentPlayerIndex)?.let { playerSetting ->
            val newType = when (controllerType) {
                ControllerType.PRO_CONTROLLER -> 0
                ControllerType.JOYCON_LEFT -> 1
                ControllerType.JOYCON_RIGHT -> 2
                ControllerType.JOYCON_PAIR -> 3
                ControllerType.HANDHELD -> 4
            }
            val updatedSetting = playerSetting.copy(controllerType = newType)
            settingsViewModel?.updatePlayerSetting(updatedSetting)
        }
        
        android.util.Log.d("PhysicalControllerManager", "Controller type updated to: $controllerType for player index: $currentPlayerIndex")
    }
    
    // 新增方法：将控制器类型转换为位掩码值
    private fun controllerTypeToBitmask(controllerType: ControllerType): Int {
        return when (controllerType) {
            ControllerType.PRO_CONTROLLER -> 1  // 1 << 0
            ControllerType.JOYCON_LEFT -> 8     // 1 << 3
            ControllerType.JOYCON_RIGHT -> 16   // 1 << 4
            ControllerType.JOYCON_PAIR -> 4     // 1 << 2
            ControllerType.HANDHELD -> 2        // 1 << 1
        }
    }
    
    // 新增方法：从设置加载控制器类型
    fun loadControllerTypeFromSettings() {
        settingsViewModel?.getPlayerSetting(currentPlayerIndex)?.let { playerSetting ->
            if (playerSetting.isConnected) {
                val controllerType = when (playerSetting.controllerType) {
                    0 -> ControllerType.PRO_CONTROLLER
                    1 -> ControllerType.JOYCON_LEFT
                    2 -> ControllerType.JOYCON_RIGHT
                    3 -> ControllerType.JOYCON_PAIR
                    4 -> ControllerType.HANDHELD
                    else -> ControllerType.PRO_CONTROLLER
                }
                updateControllerType(controllerType)
            }
        }
    }

    fun onKeyEvent(event: KeyEvent): Boolean {
        val id = getGamePadButtonInputId(event.keyCode)
        if (id != GamePadButtonInputId.None) {
            val isNotFallback = (event.flags and KeyEvent.FLAG_FALLBACK) == 0
            if (controllerId != -1 && isNotFallback) {
                when (event.action) {
                    KeyEvent.ACTION_UP -> {
                        RyujinxNative.jnaInstance.inputSetButtonReleased(id.ordinal, currentPlayerIndex)
                    }

                    KeyEvent.ACTION_DOWN -> {
                        RyujinxNative.jnaInstance.inputSetButtonPressed(id.ordinal, currentPlayerIndex)
                    }
                }
                return true
            } else if (!isNotFallback) {
                return true
            }
        }

        return false
    }

    fun onMotionEvent(ev: MotionEvent) {
        if (controllerId != -1) {
            if (ev.action == MotionEvent.ACTION_MOVE) {
                val leftStickX = ev.getAxisValue(MotionEvent.AXIS_X)
                val leftStickY = ev.getAxisValue(MotionEvent.AXIS_Y)
                val rightStickX = ev.getAxisValue(MotionEvent.AXIS_Z)
                val rightStickY = ev.getAxisValue(MotionEvent.AXIS_RZ)
                
                val quickSettings = QuickSettings(activity)
                val sensitivity = quickSettings.controllerStickSensitivity
                
                RyujinxNative.jnaInstance.inputSetStickAxis(
                    1,
                    leftStickX * sensitivity,
                    -leftStickY * sensitivity,
                    currentPlayerIndex
                )
                RyujinxNative.jnaInstance.inputSetStickAxis(
                    2,
                    rightStickX * sensitivity,
                    -rightStickY * sensitivity,
                    currentPlayerIndex
                )

                ev.device?.apply {
                    if (sources and InputDevice.SOURCE_DPAD != InputDevice.SOURCE_DPAD) {
                        // Controller uses HAT
                        val dPadHor = ev.getAxisValue(MotionEvent.AXIS_HAT_X)
                        val dPadVert = ev.getAxisValue(MotionEvent.AXIS_HAT_Y)
                        if (dPadVert == 0.0f) {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadUp.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadDown.ordinal,
                                currentPlayerIndex
                            )
                        }
                        if (dPadHor == 0.0f) {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadRight.ordinal,
                                currentPlayerIndex
                            )
                        }

                        if (dPadVert < 0.0f) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadUp.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadDown.ordinal,
                                currentPlayerIndex
                            )
                        }
                        if (dPadHor < 0.0f) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadRight.ordinal,
                                currentPlayerIndex
                            )
                        }

                        if (dPadVert > 0.0f) {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadUp.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadDown.ordinal,
                                currentPlayerIndex
                            )
                        }
                        if (dPadHor > 0.0f) {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadRight.ordinal,
                                currentPlayerIndex
                            )
                        }
                    }
                }
            }
        }
    }

    fun connect(): Int {
        // 首先从设置更新玩家索引
        updatePlayerIndexFromSettings()
        
        // 使用当前玩家索引连接游戏手柄
        controllerId = currentPlayerIndex
        
        // 从设置加载控制器类型
        loadControllerTypeFromSettings()
        
        // 注册物理控制器到ControllerManager
        val deviceName = "Physical Controller" // 这里可以根据实际情况获取设备名称
        val deviceId = "physical_controller_$controllerId"
        
        val physicalController = Controller(
            id = deviceId,
            name = deviceName,
            controllerType = currentControllerType,
            isVirtual = false
        )
        ControllerManager.addController(activity, physicalController)
        
        android.util.Log.d("PhysicalControllerManager", "Connected physical controller with player index: $controllerId, type: $currentControllerType")
        
        return controllerId
    }

    fun disconnect() {
        if (controllerId != -1) {
            // 从ControllerManager移除控制器
            val deviceId = "physical_controller_$controllerId"
            ControllerManager.removeController(deviceId)
            
            android.util.Log.d("PhysicalControllerManager", "Disconnected physical controller with player index: $controllerId")
            controllerId = -1
        }
    }

    private fun getGamePadButtonInputId(keycode: Int): GamePadButtonInputId {
        val quickSettings = QuickSettings(activity)
        return when (keycode) {
            KeyEvent.KEYCODE_BUTTON_A -> if (!quickSettings.useSwitchLayout) GamePadButtonInputId.A else GamePadButtonInputId.B
            KeyEvent.KEYCODE_BUTTON_B -> if (!quickSettings.useSwitchLayout) GamePadButtonInputId.B else GamePadButtonInputId.A
            KeyEvent.KEYCODE_BUTTON_X -> if (!quickSettings.useSwitchLayout) GamePadButtonInputId.X else GamePadButtonInputId.Y
            KeyEvent.KEYCODE_BUTTON_Y -> if (!quickSettings.useSwitchLayout) GamePadButtonInputId.Y else GamePadButtonInputId.X
            KeyEvent.KEYCODE_BUTTON_L1 -> GamePadButtonInputId.LeftShoulder
            KeyEvent.KEYCODE_BUTTON_L2 -> GamePadButtonInputId.LeftTrigger
            KeyEvent.KEYCODE_BUTTON_R1 -> GamePadButtonInputId.RightShoulder
            KeyEvent.KEYCODE_BUTTON_R2 -> GamePadButtonInputId.RightTrigger
            KeyEvent.KEYCODE_BUTTON_THUMBL -> GamePadButtonInputId.LeftStick
            KeyEvent.KEYCODE_BUTTON_THUMBR -> GamePadButtonInputId.RightStick
            KeyEvent.KEYCODE_DPAD_UP -> GamePadButtonInputId.DpadUp
            KeyEvent.KEYCODE_DPAD_DOWN -> GamePadButtonInputId.DpadDown
            KeyEvent.KEYCODE_DPAD_LEFT -> GamePadButtonInputId.DpadLeft
            KeyEvent.KEYCODE_DPAD_RIGHT -> GamePadButtonInputId.DpadRight
            KeyEvent.KEYCODE_BUTTON_START -> GamePadButtonInputId.Plus
            KeyEvent.KEYCODE_BUTTON_SELECT -> GamePadButtonInputId.Minus
            else -> GamePadButtonInputId.None
        }
    }
    
    // 新增方法：获取控制器显示名称
    fun getControllerDisplayName(): String {
        return when (currentControllerType) {
            ControllerType.PRO_CONTROLLER -> "Pro Controller"
            ControllerType.JOYCON_LEFT -> "Joy-Con (L)"
            ControllerType.JOYCON_RIGHT -> "Joy-Con (R)"
            ControllerType.JOYCON_PAIR -> "Joy-Con Pair"
            ControllerType.HANDHELD -> "Handheld"
        }
    }
    
    // 新增方法：检查控制器是否连接
    fun isConnected(): Boolean {
        return controllerId != -1
    }
    
    // 新增方法：获取控制器ID
    fun getControllerId(): Int {
        return controllerId
    }
    
    // 新增方法：获取当前控制器类型
    fun getCurrentControllerType(): ControllerType {
        return currentControllerType
    }
    
    // 新增方法：获取当前玩家索引
    fun getCurrentPlayerIndex(): Int {
        return currentPlayerIndex
    }
    
    // 新增方法：获取当前玩家显示名称
    fun getCurrentPlayerDisplayName(): String {
        return when (currentPlayerIndex) {
            in 0..7 -> "Player ${currentPlayerIndex + 1}"
            8 -> "Handheld"
            else -> "Unknown Player"
        }
    }
    
    // 新增方法：检查是否为掌机模式
    fun isHandheldMode(): Boolean {
        return currentPlayerIndex == 8
    }
    
    // 新增方法：重新连接控制器（用于设置更改后重新应用）
    fun reconnect() {
        if (isConnected()) {
            disconnect()
            connect()
        }
    }
    
    // 新增方法：完全刷新设置（玩家索引和控制器类型）
    fun refreshSettings() {
        updatePlayerIndexFromSettings()
        loadControllerTypeFromSettings()
        reconnect()
    }
    
    // 新增方法：设置玩家索引（用于测试）
    fun setPlayerIndexForTesting(playerIndex: Int) {
        if (playerIndex in 0..8) {
            currentPlayerIndex = playerIndex
            android.util.Log.d("PhysicalControllerManager", "Player index set to: $playerIndex for testing")
        }
    }
    
    // 新增方法：处理设置变化
    fun onSettingsChanged() {
        // 当设置变化时，刷新物理控制器的配置
        refreshSettings()
    }
    
    // 新增方法：应用当前设置到Native层
    fun applyCurrentSettings() {
        if (isConnected()) {
            // 应用控制器类型
            val controllerTypeBitmask = controllerTypeToBitmask(currentControllerType)
            RyujinxNative.jnaInstance.setControllerType(currentPlayerIndex, controllerTypeBitmask)
            
            android.util.Log.d("PhysicalControllerManager", "Applied current settings: playerIndex=$currentPlayerIndex, controllerType=$currentControllerType")
        }
    }
    
    // 新增方法：检查玩家索引是否有效
    fun isValidPlayerIndex(playerIndex: Int): Boolean {
        return playerIndex in 0..8
    }
    
    // 新增方法：获取所有支持的玩家索引
    fun getSupportedPlayerIndices(): List<Int> {
        return listOf(0, 1, 2, 3, 4, 5, 6, 7, 8) // 玩家1-8 + 掌机模式
    }
    
    // 新增方法：获取玩家索引描述
    fun getPlayerIndexDescription(playerIndex: Int): String {
        return when (playerIndex) {
            in 0..7 -> "Player ${playerIndex + 1}"
            8 -> "Handheld Mode"
            else -> "Unknown"
        }
    }
    
    // 新增方法：设置玩家索引
    fun setPlayerIndex(playerIndex: Int): Boolean {
        if (isValidPlayerIndex(playerIndex)) {
            val oldIndex = currentPlayerIndex
            currentPlayerIndex = playerIndex
            
            // 如果控制器已连接，重新连接以应用新的玩家索引
            if (isConnected()) {
                disconnect()
                connect()
            }
            
            android.util.Log.d("PhysicalControllerManager", "Player index changed from $oldIndex to $playerIndex")
            return true
        }
        return false
    }
    
    // 新增方法：设置控制器类型通过索引
    fun setControllerTypeByIndex(controllerTypeIndex: Int) {
        val controllerType = when (controllerTypeIndex) {
            0 -> ControllerType.PRO_CONTROLLER
            1 -> ControllerType.JOYCON_LEFT
            2 -> ControllerType.JOYCON_RIGHT
            3 -> ControllerType.JOYCON_PAIR
            4 -> ControllerType.HANDHELD
            else -> ControllerType.PRO_CONTROLLER
        }
        updateControllerType(controllerType)
    }
    
    // 新增方法：获取控制器类型索引
    fun getControllerTypeIndex(): Int {
        return when (currentControllerType) {
            ControllerType.PRO_CONTROLLER -> 0
            ControllerType.JOYCON_LEFT -> 1
            ControllerType.JOYCON_RIGHT -> 2
            ControllerType.JOYCON_PAIR -> 3
            ControllerType.HANDHELD -> 4
        }
    }
    
    // 新增方法：保存当前设置到SettingsViewModel
    fun saveCurrentSettings() {
        settingsViewModel?.getPlayerSetting(currentPlayerIndex)?.let { playerSetting ->
            val controllerTypeIndex = getControllerTypeIndex()
            val updatedSetting = playerSetting.copy(
                isConnected = isConnected(),
                controllerType = controllerTypeIndex
            )
            settingsViewModel?.updatePlayerSetting(updatedSetting)
        }
    }
    
    // 新增方法：加载设置从SettingsViewModel
    fun loadSettingsFromViewModel() {
        updatePlayerIndexFromSettings()
        loadControllerTypeFromSettings()
    }
}
