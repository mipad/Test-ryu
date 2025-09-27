package org.ryujinx.android

import android.app.Activity
import android.content.Context
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.View
import android.widget.FrameLayout
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.math.MathUtils
import androidx.core.view.isVisible
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
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
import kotlinx.coroutines.flow.collect
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.QuickSettings
import org.ryujinx.android.ControllerManager
import org.ryujinx.android.Controller
import org.ryujinx.android.ControllerType
import org.ryujinx.android.viewmodels.SettingsViewModel

typealias GamePad = RadialGamePad
typealias GamePadConfig = RadialGamePadConfig

class GameController(var activity: Activity, var mainViewModel: MainViewModel) {

    companion object {
        private fun Create(context: Context, controller: GameController): View {
            val inflator = LayoutInflater.from(context)
            val view = inflator.inflate(R.layout.game_layout, null)
            view.findViewById<FrameLayout>(R.id.leftcontainer)!!.addView(controller.leftGamePad)
            view.findViewById<FrameLayout>(R.id.rightcontainer)!!.addView(controller.rightGamePad)

            return view
        }

        @Composable
        fun Compose(viewModel: MainViewModel): Unit {
            AndroidView(
                modifier = Modifier.fillMaxSize(), factory = { context ->
                    val controller = GameController(viewModel.activity, viewModel)
                    val c = Create(context, controller)
                    
                    // 使用全局的 CoroutineScope
                    CoroutineScope(Dispatchers.Main).launch {
                        val leftEvents = controller.leftGamePad.events()
                        val rightEvents = controller.rightGamePad.events()
                        
                        launch {
                            leftEvents.collect { event ->
                                controller.handleEvent(event)
                            }
                        }
                        
                        launch {
                            rightEvents.collect { event ->
                                controller.handleEvent(event)
                            }
                        }
                    }
                    
                    controller.controllerView = c
                    viewModel.setGameController(controller)
                    
                    // 从设置中获取虚拟控制器是否启用
                    val quickSettings = QuickSettings(viewModel.activity)
                    controller.setVisible(quickSettings.useVirtualController)
                    
                    // 初始化时设置控制器类型
                    controller.updateControllerTypeFromSettings()
                    
                    c
                })
        }
    }

    private var controllerView: View? = null
    var leftGamePad: GamePad
    var rightGamePad: GamePad
    var controllerId: Int = -1
    
    // 修改：使用自定义getter替代单独的方法
    var currentControllerType: ControllerType = ControllerType.PRO_CONTROLLER
        get() = field
        private set
    
    var currentPlayerIndex: Int = 0
        get() = field
        private set
    
    // 新增：支持多个虚拟控制器（玩家0和玩家8）
    private var virtualControllers: MutableMap<Int, Controller> = mutableMapOf()
    private var currentActivePlayerIndex: Int = 0
    
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
        
        // 修改：初始化时创建所有需要的虚拟控制器
        initializeVirtualControllers()
        
        // 初始化时从设置获取玩家索引
        updatePlayerIndexFromSettings()
        
        // 设置初始激活玩家
        currentActivePlayerIndex = calculateCurrentActivePlayerIndex()
        connectVirtualController(currentActivePlayerIndex)
        
        android.util.Log.d("GameController", "Initialized with active player: $currentActivePlayerIndex")
    }

    // 新增方法：初始化虚拟控制器
    private fun initializeVirtualControllers() {
        // 为玩家1创建虚拟控制器
        val player1Controller = Controller(
            id = "virtual_controller_0",
            name = "Ryujinx Touch Controller - Player 1",
            controllerType = getControllerTypeForPlayer(0),
            isVirtual = true
        )
        virtualControllers[0] = player1Controller
        ControllerManager.addController(activity, player1Controller)
        
        // 为掌机模式创建虚拟控制器
        val handheldController = Controller(
            id = "virtual_controller_8", 
            name = "Ryujinx Touch Controller - Handheld",
            controllerType = ControllerType.HANDHELD,
            isVirtual = true
        )
        virtualControllers[8] = handheldController
        ControllerManager.addController(activity, handheldController)
        
        android.util.Log.d("GameController", "Initialized virtual controllers for player 0 and player 8")
    }

    // 修改方法：重命名以避免重载冲突
    private fun calculateCurrentActivePlayerIndex(): Int {
        // 优先检查掌机模式是否连接
        val handheldSetting = mainViewModel.getPlayerSetting(8)
        val isHandheldConnected = handheldSetting?.isConnected ?: false
        
        // 检查玩家1是否连接
        val player1Setting = mainViewModel.getPlayerSetting(0)
        val isPlayer1Connected = player1Setting?.isConnected ?: true
        
        return when {
            isHandheldConnected -> 8
            isPlayer1Connected -> 0
            else -> 0 // 默认回退到玩家1
        }
    }

    // 新增方法：根据玩家索引获取控制器类型
    private fun getControllerTypeForPlayer(playerIndex: Int): ControllerType {
        val playerSetting = mainViewModel.getPlayerSetting(playerIndex)
        return when (playerSetting?.controllerType ?: 0) {
            0 -> ControllerType.PRO_CONTROLLER
            1 -> ControllerType.JOYCON_LEFT
            2 -> ControllerType.JOYCON_RIGHT
            3 -> ControllerType.JOYCON_PAIR
            4 -> ControllerType.HANDHELD
            else -> ControllerType.PRO_CONTROLLER
        }
    }

    // 新增方法：更新当前激活的玩家
    fun updateActivePlayer() {
        val newActivePlayer = calculateCurrentActivePlayerIndex()
        if (currentActivePlayerIndex != newActivePlayer) {
            android.util.Log.d("GameController", "Switching active player from $currentActivePlayerIndex to $newActivePlayer")
            
            // 断开当前玩家的连接
            disconnectVirtualController(currentActivePlayerIndex)
            
            // 连接新玩家
            currentActivePlayerIndex = newActivePlayer
            connectVirtualController(currentActivePlayerIndex)
            
            // 更新控制器类型
            updateControllerTypeForActivePlayer()
        }
    }

    // 新增方法：连接指定玩家的虚拟控制器
    private fun connectVirtualController(playerIndex: Int) {
        if (controllerId != -1) {
            disconnect()
        }
        
        controllerId = playerIndex
        currentPlayerIndex = playerIndex
        
        val controllerTypeInt = controllerTypeToInt(getControllerTypeForPlayer(playerIndex))
        RyujinxNative.setControllerType(playerIndex, controllerTypeInt)
        
        ControllerManager.updateControllerId(activity, "virtual_controller_$playerIndex", controllerId)
        
        android.util.Log.d("GameController", "Connected virtual controller for player $playerIndex")
    }

    // 新增方法：断开指定玩家的虚拟控制器
    private fun disconnectVirtualController(playerIndex: Int) {
        RyujinxNative.setControllerType(playerIndex, 0) // 设置为无控制器
        ControllerManager.updateControllerId(activity, "virtual_controller_$playerIndex", -1)
        
        android.util.Log.d("GameController", "Disconnected virtual controller for player $playerIndex")
    }

    // 新增方法：为激活玩家更新控制器类型
    private fun updateControllerTypeForActivePlayer() {
        val newType = getControllerTypeForPlayer(currentActivePlayerIndex)
        updateControllerType(newType)
    }

    // 修改方法：从设置更新玩家索引
    fun updatePlayerIndexFromSettings() {
        // 检查掌机模式是否启用
        val handheldSetting = mainViewModel.getPlayerSetting(8)
        val isHandheldConnected = handheldSetting?.isConnected ?: false
        
        // 检查玩家1是否启用
        val player1Setting = mainViewModel.getPlayerSetting(0)
        val isPlayer1Connected = player1Setting?.isConnected ?: true
        
        // 决定使用哪个玩家索引
        currentPlayerIndex = if (isHandheldConnected) {
            8
        } else if (isPlayer1Connected) {
            0
        } else {
            0
        }
        
        android.util.Log.d("GameController", "Updated player index to: $currentPlayerIndex (Handheld: $isHandheldConnected, Player1: $isPlayer1Connected)")
        
        // 更新激活玩家
        updateActivePlayer()
    }

    // 修改方法：从设置更新控制器类型
    fun updateControllerTypeFromSettings() {
        updateControllerTypeForActivePlayer()
    }

    // 修改方法：更新控制器类型
    fun updateControllerType(newType: ControllerType) {
        if (currentControllerType == newType) {
            return
        }
        
        currentControllerType = newType
        
        ControllerManager.updateControllerType(activity, "virtual_controller_$currentActivePlayerIndex", newType)
        
        if (controllerId != -1) {
            val controllerTypeInt = controllerTypeToInt(newType)
            RyujinxNative.setControllerType(currentActivePlayerIndex, controllerTypeInt)
        }
        
        updateVirtualLayoutForControllerType(newType)
    }

    // 修改方法：根据控制器类型更新虚拟按键布局
    private fun updateVirtualLayoutForControllerType(controllerType: ControllerType) {
        when (controllerType) {
            ControllerType.PRO_CONTROLLER -> {
                setVirtualControllerVisibility(true, true)
                recreateGamePads(true, true, false)
            }
            ControllerType.JOYCON_LEFT -> {
                setVirtualControllerVisibility(true, false)
                recreateGamePads(true, false, true)
            }
            ControllerType.JOYCON_RIGHT -> {
                setVirtualControllerVisibility(false, true)
                recreateGamePads(false, true, true)
            }
            ControllerType.JOYCON_PAIR -> {
                setVirtualControllerVisibility(true, true)
                recreateGamePads(true, true, false)
            }
            ControllerType.HANDHELD -> {
                setVirtualControllerVisibility(true, true)
                recreateGamePads(true, true, false)
            }
        }
        
        refreshVirtualControllerView()
    }

    // 修改方法：重新创建游戏手柄
    private fun recreateGamePads(createLeft: Boolean, createRight: Boolean, isJoyCon: Boolean) {
        controllerView?.apply {
            val leftContainer = findViewById<FrameLayout>(R.id.leftcontainer)
            val rightContainer = findViewById<FrameLayout>(R.id.rightcontainer)
            
            if (createLeft) {
                leftContainer?.removeAllViews()
                val newLeftPad = if (isJoyCon && currentControllerType == ControllerType.JOYCON_LEFT) {
                    GamePad(generateJoyConLeftConfig(), 16f, activity)
                } else {
                    GamePad(generateConfig(true), 16f, activity)
                }
                newLeftPad.primaryDialMaxSizeDp = 200f
                newLeftPad.gravityX = -1f
                newLeftPad.gravityY = 1f
                leftContainer?.addView(newLeftPad)
                leftGamePad = newLeftPad
                
                CoroutineScope(Dispatchers.Main).launch {
                    newLeftPad.events().collect { event ->
                        handleEvent(event)
                    }
                }
            }
            
            if (createRight) {
                rightContainer?.removeAllViews()
                val newRightPad = if (isJoyCon && currentControllerType == ControllerType.JOYCON_RIGHT) {
                    GamePad(generateJoyConRightConfig(), 16f, activity)
                } else {
                    GamePad(generateConfig(false), 16f, activity)
                }
                newRightPad.primaryDialMaxSizeDp = 200f
                newRightPad.gravityX = 1f
                newRightPad.gravityY = 1f
                rightContainer?.addView(newRightPad)
                rightGamePad = newRightPad
                
                CoroutineScope(Dispatchers.Main).launch {
                    newRightPad.events().collect { event ->
                        handleEvent(event)
                    }
                }
            }
        }
    }

    // 修改方法：设置虚拟控制器可见性
    private fun setVirtualControllerVisibility(showLeft: Boolean, showRight: Boolean) {
        controllerView?.apply {
            findViewById<FrameLayout>(R.id.leftcontainer)?.isVisible = showLeft
            findViewById<FrameLayout>(R.id.rightcontainer)?.isVisible = showRight
        }
    }

    // 修改方法：刷新虚拟控制器视图
    private fun refreshVirtualControllerView() {
        controllerView?.apply {
            requestLayout()
            invalidate()
        }
        leftGamePad.invalidate()
        rightGamePad.invalidate()
    }

    // 修改方法：生成Joy-Con左柄专用配置
    private fun generateJoyConLeftConfig(): GamePadConfig {
        val distance = 0.3f
        val buttonScale = 1f

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
                )
            )
        )
    }

    // 修改方法：生成Joy-Con右柄专用配置
    private fun generateJoyConRightConfig(): GamePadConfig {
        val distance = 0.3f
        val buttonScale = 1f

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
                        GamePadButtonInputId.B.ordinal,
                        "B",
                        true,
                        null,
                        "B",
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

    // 修改方法：将ControllerType转换为整数
    private fun controllerTypeToInt(controllerType: ControllerType): Int {
        return when (controllerType) {
            ControllerType.PRO_CONTROLLER -> 1
            ControllerType.JOYCON_LEFT -> 8
            ControllerType.JOYCON_RIGHT -> 16
            ControllerType.JOYCON_PAIR -> 4
            ControllerType.HANDHELD -> 2
        }
    }

    // 修改方法：设置可见性
    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible

            if (isVisible) {
                connectVirtualController(currentActivePlayerIndex)
                updateVirtualLayoutForControllerType(currentControllerType)
            } else {
                disconnectVirtualController(currentActivePlayerIndex)
            }
        }
    }

    // 修改方法：连接（现在使用connectVirtualController）
    fun connect() {
        connectVirtualController(currentActivePlayerIndex)
    }

    // 修改方法：断开连接（现在使用disconnectVirtualController）
    fun disconnect() {
        disconnectVirtualController(currentActivePlayerIndex)
    }

    // 修改方法：重新连接
    fun reconnect() {
        if (isVisible) {
            disconnectVirtualController(currentActivePlayerIndex)
            connectVirtualController(currentActivePlayerIndex)
        }
    }

    // 修改方法：完全刷新设置
    fun refreshSettings() {
        updateActivePlayer()
        updateControllerTypeFromSettings()
        reconnect()
    }

    // 修改方法：处理输入事件时发送到当前激活的玩家
    private fun handleEvent(ev: Event) {
        // 确保使用当前激活的玩家索引
        val targetPlayerIndex = currentActivePlayerIndex
        
        if (controllerId == -1) {
            connectVirtualController(targetPlayerIndex)
        }

        if (controllerId == -1) {
            return
        }

        when (ev) {
            is Event.Button -> {
                val action = ev.action
                when (action) {
                    KeyEvent.ACTION_UP -> {
                        RyujinxNative.jnaInstance.inputSetButtonReleased(ev.id, targetPlayerIndex)
                    }
                    KeyEvent.ACTION_DOWN -> {
                        RyujinxNative.jnaInstance.inputSetButtonPressed(ev.id, targetPlayerIndex)
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
                                targetPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                targetPlayerIndex
                            )
                        } else if (ev.xAxis < 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                targetPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadRight.ordinal,
                                targetPlayerIndex
                            )
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                targetPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadRight.ordinal,
                                targetPlayerIndex
                            )
                        }
                        if (ev.yAxis < 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadUp.ordinal,
                                targetPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadDown.ordinal,
                                targetPlayerIndex
                            )
                        } else if (ev.yAxis > 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadDown.ordinal,
                                targetPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadUp.ordinal,
                                targetPlayerIndex
                            )
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadDown.ordinal,
                                targetPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadUp.ordinal,
                                targetPlayerIndex
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
                            targetPlayerIndex
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
                            targetPlayerIndex
                        )
                    }
                }
            }
        }
    }
    
    // 新增方法：获取当前玩家索引的显示名称
    fun getCurrentPlayerDisplayName(): String {
        return when (currentActivePlayerIndex) {
            in 0..7 -> "Player ${currentActivePlayerIndex + 1}"
            8 -> "Handheld"
            else -> "Unknown Player"
        }
    }
    
    // 新增方法：检查是否为掌机模式
    fun isHandheldMode(): Boolean {
        return currentActivePlayerIndex == 8
    }
    
    // 修改方法：重命名以避免重载冲突
    fun getCurrentActivePlayerIndex(): Int {
        return currentActivePlayerIndex
    }
    
    // 新增方法：设置玩家索引（用于测试）
    fun setPlayerIndexForTesting(playerIndex: Int) {
        if (playerIndex in 0..8) {
            currentActivePlayerIndex = playerIndex
            android.util.Log.d("GameController", "Player index set to: $playerIndex for testing")
        }
    }
}

// 生成标准配置
private fun generateConfig(isLeft: Boolean): GamePadConfig {
    val distance = 0.3f
    val buttonScale = 1f

    return if (isLeft) {
        // 左侧配置
        GamePadConfig(
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
                )
            )
        )
    } else {
        // 右侧配置
        GamePadConfig(
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
                        GamePadButtonInputId.B.ordinal,
                        "B",
                        true,
                        null,
                        "B",
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
