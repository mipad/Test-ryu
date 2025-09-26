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
        
        // 初始化时注册虚拟控制器到ControllerManager
        val virtualController = Controller(
            id = "virtual_controller_0",
            name = "MeloNX Touch Controller",
            controllerType = getInitialControllerType(),
            isVirtual = true
        )
        ControllerManager.addController(activity, virtualController)
        
        // 初始化时从设置获取玩家索引
        updatePlayerIndexFromSettings()
    }

    // 新增方法：从设置更新玩家索引
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
        
        if (controllerId != -1) {
            disconnect()
            connect()
        }
    }

    // 新增方法：获取初始控制器类型
    private fun getInitialControllerType(): ControllerType {
        val playerSetting = mainViewModel.getPlayerSetting(currentPlayerIndex)
        return when (playerSetting?.controllerType ?: 0) {
            0 -> ControllerType.PRO_CONTROLLER
            1 -> ControllerType.JOYCON_LEFT
            2 -> ControllerType.JOYCON_RIGHT
            3 -> ControllerType.JOYCON_PAIR
            4 -> ControllerType.HANDHELD
            else -> ControllerType.PRO_CONTROLLER
        }
    }

    // 新增方法：从设置更新控制器类型
    fun updateControllerTypeFromSettings() {
        val playerSetting = mainViewModel.getPlayerSetting(currentPlayerIndex)
        val newType = when (playerSetting?.controllerType ?: 0) {
            0 -> ControllerType.PRO_CONTROLLER
            1 -> ControllerType.JOYCON_LEFT
            2 -> ControllerType.JOYCON_RIGHT
            3 -> ControllerType.JOYCON_PAIR
            4 -> ControllerType.HANDHELD
            else -> ControllerType.PRO_CONTROLLER
        }
        
        updateControllerType(newType)
    }

    // 新增方法：更新控制器类型
    fun updateControllerType(newType: ControllerType) {
        if (currentControllerType == newType) {
            return
        }
        
        currentControllerType = newType
        
        ControllerManager.updateControllerType(activity, "virtual_controller_$currentPlayerIndex", newType)
        
        if (controllerId != -1) {
            val controllerTypeInt = controllerTypeToInt(newType)
            RyujinxNative.setControllerType(currentPlayerIndex, controllerTypeInt)
        }
        
        updateVirtualLayoutForControllerType(newType)
    }

    // 新增方法：根据控制器类型更新虚拟按键布局
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

    // 新增方法：重新创建游戏手柄
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

    // 新增方法：设置虚拟控制器可见性
    private fun setVirtualControllerVisibility(showLeft: Boolean, showRight: Boolean) {
        controllerView?.apply {
            findViewById<FrameLayout>(R.id.leftcontainer)?.isVisible = showLeft
            findViewById<FrameLayout>(R.id.rightcontainer)?.isVisible = showRight
        }
    }

    // 新增方法：刷新虚拟控制器视图
    private fun refreshVirtualControllerView() {
        controllerView?.apply {
            requestLayout()
            invalidate()
        }
        leftGamePad.invalidate()
        rightGamePad.invalidate()
    }

    // 新增方法：生成Joy-Con左柄专用配置
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

    // 新增方法：生成Joy-Con右柄专用配置
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

    fun setVisible(isVisible: Boolean) {
        controllerView?.apply {
            this.isVisible = isVisible

            if (isVisible) {
                connect()
                updateVirtualLayoutForControllerType(currentControllerType)
            } else {
                disconnect()
            }
        }
    }

    fun connect() {
        if (controllerId == -1) {
            controllerId = currentPlayerIndex
            
            val controllerTypeInt = controllerTypeToInt(currentControllerType)
            RyujinxNative.setControllerType(currentPlayerIndex, controllerTypeInt)
            
            ControllerManager.updateControllerId(activity, "virtual_controller_$currentPlayerIndex", controllerId)
            
            android.util.Log.d("GameController", "Connected virtual controller with player index: $currentPlayerIndex, type: $currentControllerType")
        }
    }

    fun disconnect() {
        if (controllerId != -1) {
            RyujinxNative.setControllerType(currentPlayerIndex, 1)
            controllerId = -1
            
            ControllerManager.updateControllerId(activity, "virtual_controller_$currentPlayerIndex", -1)
            
            android.util.Log.d("GameController", "Disconnected virtual controller with player index: $currentPlayerIndex")
        }
    }

    // 新增方法：重新连接
    fun reconnect() {
        if (isVisible) {
            disconnect()
            connect()
        }
    }

    // 新增方法：完全刷新设置
    fun refreshSettings() {
        updatePlayerIndexFromSettings()
        updateControllerTypeFromSettings()
        reconnect()
    }

    private fun handleEvent(ev: Event) {
        if (controllerId == -1) {
            connect()
        }

        if (controllerId == -1) {
            return
        }

        when (ev) {
            is Event.Button -> {
                val action = ev.action
                when (action) {
                    KeyEvent.ACTION_UP -> {
                        RyujinxNative.jnaInstance.inputSetButtonReleased(ev.id, currentPlayerIndex)
                    }
                    KeyEvent.ACTION_DOWN -> {
                        RyujinxNative.jnaInstance.inputSetButtonPressed(ev.id, currentPlayerIndex)
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
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                currentPlayerIndex
                            )
                        } else if (ev.xAxis < 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadRight.ordinal,
                                currentPlayerIndex
                            )
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadLeft.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadRight.ordinal,
                                currentPlayerIndex
                            )
                        }
                        if (ev.yAxis < 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadUp.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadDown.ordinal,
                                currentPlayerIndex
                            )
                        } else if (ev.yAxis > 0) {
                            RyujinxNative.jnaInstance.inputSetButtonPressed(
                                GamePadButtonInputId.DpadDown.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadUp.ordinal,
                                currentPlayerIndex
                            )
                        } else {
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadDown.ordinal,
                                currentPlayerIndex
                            )
                            RyujinxNative.jnaInstance.inputSetButtonReleased(
                                GamePadButtonInputId.DpadUp.ordinal,
                                currentPlayerIndex
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
                            currentPlayerIndex
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
                            currentPlayerIndex
                        )
                    }
                }
            }
        }
    }
    
    // 新增方法：获取当前玩家索引的显示名称
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
    
    // 修改：删除以下冲突的方法，直接使用属性
    // fun getCurrentPlayerIndex(): Int
    // fun getCurrentControllerType(): ControllerType
    
    // 新增方法：设置玩家索引（用于测试）
    fun setPlayerIndexForTesting(playerIndex: Int) {
        if (playerIndex in 0..8) {
            currentPlayerIndex = playerIndex
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
