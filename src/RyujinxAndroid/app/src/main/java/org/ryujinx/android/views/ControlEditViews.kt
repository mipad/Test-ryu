package org.ryujinx.android.views

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.background
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import org.ryujinx.android.MainActivity
import org.ryujinx.android.viewmodels.MainViewModel

/**
 * 控件编辑相关视图 - 专门处理虚拟按键的调整和设置
 */
class ControlEditViews {
    companion object {
        @Composable
        fun AdjustControlsDialog(
            mainViewModel: MainViewModel,
            onDismiss: () -> Unit
        ) {
            val selectedControl = remember { mutableStateOf<ControlItem?>(null) }
            val showCreateCombination = remember { mutableStateOf(false) }

            // 如果选择了具体控件，显示控件调整对话框
            if (selectedControl.value != null) {
                ControlAdjustmentDialog(
                    control = selectedControl.value!!,
                    onDismiss = { selectedControl.value = null },
                    mainViewModel = mainViewModel
                )
                return
            }

            // 创建组合按键对话框
            if (showCreateCombination.value) {
                CreateCombinationDialog(
                    mainViewModel = mainViewModel,
                    onDismiss = { showCreateCombination.value = false },
                    onCombinationCreated = {
                        showCreateCombination.value = false
                        // 刷新控件列表以显示新创建的组合按键
                    }
                )
                return
            }

            // 主调整对话框 - 使用固定dp尺寸
            Dialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .width(600.dp)  // 固定宽度
                        .height(500.dp), // 固定高度
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Row(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(16.dp)
                    ) {
                        // 左侧：按键列表
                        Column(
                            modifier = Modifier
                                .weight(1f)
                                .fillMaxHeight()
                        ) {
                            // 顶部按钮行 - 去掉标题，全部重置在左，确定在右
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                // 左侧：全部重置按钮
                                TextButton(
                                    onClick = {
                                        // 重置所有单独设置
                                        getControlItems().forEach { control ->
                                            when (control.type) {
                                                ControlType.BUTTON -> {
                                                    mainViewModel.controller?.setControlScale(control.id, 50)
                                                    mainViewModel.controller?.setControlOpacity(control.id, 100)
                                                    mainViewModel.controller?.setControlEnabled(control.id, true)
                                                }
                                                ControlType.JOYSTICK -> {
                                                    mainViewModel.controller?.setControlScale(control.id, 50)
                                                    mainViewModel.controller?.setControlOpacity(control.id, 100)
                                                    mainViewModel.controller?.setControlEnabled(control.id, true)
                                                }
                                                ControlType.DPAD -> {
                                                    mainViewModel.controller?.setControlScale(control.id, 50)
                                                    mainViewModel.controller?.setControlOpacity(control.id, 100)
                                                    mainViewModel.controller?.setControlEnabled(control.id, true)
                                                }
                                                ControlType.COMBINATION -> {
                                                    // 不重置组合按键
                                                }
                                            }
                                        }
                                    }
                                ) {
                                    Text(text = "全部重置")
                                }
                                
                                // 右侧：确定按钮
                                TextButton(
                                    onClick = onDismiss
                                ) {
                                    Text(text = "确定")
                                }
                            }

                            Spacer(modifier = Modifier.height(12.dp))

                            // 创建组合按键按钮
                            Button(
                                onClick = { showCreateCombination.value = true },
                                colors = ButtonDefaults.buttonColors(
                                    containerColor = MaterialTheme.colorScheme.primary,
                                    contentColor = Color.White
                                ),
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(vertical = 8.dp)
                            ) {
                                Row(
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "➕", fontSize = 18.sp, modifier = Modifier.padding(end = 8.dp))
                                    Text(text = "创建组合按键")
                                }
                            }

                            Spacer(modifier = Modifier.height(12.dp))

                            Text(
                                text = "单个按键设置",
                                style = MaterialTheme.typography.titleMedium,
                                modifier = Modifier.padding(vertical = 4.dp)
                            )

                            // 按键列表
                            LazyColumn(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .weight(1f)
                            ) {
                                itemsIndexed(getControlItems()) { index, control ->
                                    ControlListItem(
                                        control = control,
                                        mainViewModel = mainViewModel,
                                        onClick = { selectedControl.value = control }
                                    )
                                    if (index < getControlItems().size - 1) {
                                        HorizontalDivider(
                                            modifier = Modifier.padding(horizontal = 4.dp),
                                            thickness = 0.5.dp,
                                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.1f)
                                        )
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        @Composable
        fun ControlListItem(
            control: ControlItem,
            mainViewModel: MainViewModel,
            onClick: () -> Unit
        ) {
            val scale = remember { 
                mutableStateOf(mainViewModel.controller?.getControlScale(control.id) ?: 50)
            }
            val opacity = remember { 
                mutableStateOf(mainViewModel.controller?.getControlOpacity(control.id) ?: 100)
            }
            val enabled = remember { 
                mutableStateOf(mainViewModel.controller?.isControlEnabled(control.id) ?: true)
            }

            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 2.dp),
                color = Color.Transparent,
                onClick = onClick
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(12.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            text = control.emoji,
                            fontSize = 18.sp,
                            modifier = Modifier.padding(end = 8.dp)
                        )
                        Column {
                            Text(
                                text = control.name,
                                style = MaterialTheme.typography.bodyMedium,
                                fontSize = 14.sp
                            )
                            Text(
                                text = control.description,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                fontSize = 11.sp
                            )
                        }
                    }
                    
                    // 显示当前设置状态
                    Column(
                        horizontalAlignment = Alignment.End
                    ) {
                        Text(
                            text = if (enabled.value) "显示中" else "已隐藏",
                            color = if (enabled.value) Color.Green else Color.Red,
                            fontSize = 10.sp
                        )
                        Text(
                            text = "大小:${scale.value}% 透明:${opacity.value}%",
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                            fontSize = 9.sp
                        )
                    }
                }
            }
        }

        @Composable
        fun ControlAdjustmentDialog(
            control: ControlItem,
            onDismiss: () -> Unit,
            mainViewModel: MainViewModel
        ) {
            val scale = remember { 
                mutableStateOf(mainViewModel.controller?.getControlScale(control.id) ?: 50)
            }
            val opacity = remember { 
                mutableStateOf(mainViewModel.controller?.getControlOpacity(control.id) ?: 100)
            }
            val enabled = remember { 
                mutableStateOf(mainViewModel.controller?.isControlEnabled(control.id) ?: true)
            }

            // 控件调整对话框 - 使用固定dp尺寸
            Dialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .width(500.dp)  // 固定宽度
                        .height(400.dp), // 固定高度
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Row(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(16.dp)
                    ) {
                        // 左侧：控件信息和设置
                        Column(
                            modifier = Modifier
                                .weight(1f)
                                .fillMaxHeight()
                        ) {
                            // 顶部按钮行 - 重置在左，确定在右
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                // 左侧：重置或删除按钮
                                if (control.type == ControlType.COMBINATION) {
                                    TextButton(
                                        onClick = {
                                            mainViewModel.controller?.deleteCombination(control.id)
                                            onDismiss()
                                        },
                                        colors = ButtonDefaults.textButtonColors(
                                            contentColor = Color.Red
                                        )
                                    ) {
                                        Text(text = "🗑️ 删除")
                                    }
                                } else {
                                    TextButton(
                                        onClick = {
                                            scale.value = 50
                                            opacity.value = 100
                                            enabled.value = true
                                            mainViewModel.controller?.setControlScale(control.id, 50)
                                            mainViewModel.controller?.setControlOpacity(control.id, 100)
                                            mainViewModel.controller?.setControlEnabled(control.id, true)
                                        }
                                    ) {
                                        Text(text = "重置")
                                    }
                                }

                                // 右侧：确定按钮
                                TextButton(
                                    onClick = onDismiss
                                ) {
                                    Text(text = "确定")
                                }
                            }

                            Spacer(modifier = Modifier.height(16.dp))

                            // 控件信息
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier.padding(bottom = 16.dp)
                            ) {
                                Text(
                                    text = control.emoji,
                                    fontSize = 22.sp,
                                    modifier = Modifier.padding(end = 12.dp)
                                )
                                Column {
                                    Text(
                                        text = control.name,
                                        style = MaterialTheme.typography.titleMedium
                                    )
                                    Text(
                                        text = control.description,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                                    )
                                }
                            }

                            // 大小设置
                            Text(
                                text = "按键大小",
                                style = MaterialTheme.typography.bodyMedium,
                                modifier = Modifier.padding(bottom = 8.dp)
                            )
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "大小")
                                Text(text = "${scale.value}%")
                            }
                            Slider(
                                value = scale.value.toFloat(),
                                onValueChange = { 
                                    scale.value = it.toInt()
                                    mainViewModel.controller?.setControlScale(control.id, scale.value)
                                },
                                valueRange = 10f..200f,
                                modifier = Modifier.fillMaxWidth()
                            )

                            Spacer(modifier = Modifier.height(16.dp))

                            // 透明度设置
                            Text(
                                text = "按键透明度",
                                style = MaterialTheme.typography.bodyMedium,
                                modifier = Modifier.padding(bottom = 8.dp)
                            )
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "透明度")
                                Text(text = "${opacity.value}%")
                            }
                            Slider(
                                value = opacity.value.toFloat(),
                                onValueChange = { 
                                    opacity.value = it.toInt()
                                    mainViewModel.controller?.setControlOpacity(control.id, opacity.value)
                                },
                                valueRange = 0f..100f,
                                modifier = Modifier.fillMaxWidth()
                            )

                            Spacer(modifier = Modifier.height(16.dp))

                            // 显示开关
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(text = "显示按键")
                                Switch(
                                    checked = enabled.value,
                                    onCheckedChange = { 
                                        enabled.value = it
                                        mainViewModel.controller?.setControlEnabled(control.id, enabled.value)
                                    }
                                )
                            }
                        }
                    }
                }
            }
        }

        @Composable
        fun CreateCombinationDialog(
            mainViewModel: MainViewModel,
            onDismiss: () -> Unit,
            onCombinationCreated: () -> Unit
        ) {
            val combinationName = remember { mutableStateOf("") }
            val selectedKeys = remember { mutableStateOf<List<Int>>(emptyList()) }
            val showKeySelection = remember { mutableStateOf(false) }

            // 按键选择对话框
            if (showKeySelection.value) {
                KeySelectionDialog(
                    initialSelectedKeys = selectedKeys.value,
                    onKeysSelected = { keys ->
                        selectedKeys.value = keys
                        showKeySelection.value = false
                    },
                    onDismiss = { showKeySelection.value = false }
                )
                return
            }

            // 创建组合按键对话框 - 使用固定dp尺寸，去掉标题，布局更紧凑
            Dialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .width(500.dp)  // 固定宽度
                        .height(450.dp), // 固定高度
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(16.dp)
                    ) {
                        // 顶部按钮行 - 取消在左，创建在右
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            TextButton(
                                onClick = onDismiss
                            ) {
                                Text(text = "取消")
                            }
                            
                            Button(
                                onClick = {
                                    if (combinationName.value.isNotBlank() && selectedKeys.value.isNotEmpty()) {
                                        // 关键修复：确保组合按键创建后正确刷新位置
                                        val success = mainViewModel.controller?.createCombination(
                                            combinationName.value,
                                            selectedKeys.value
                                        ) ?: -1
                                        
                                        if (success != -1) {
                                            onCombinationCreated()
                                            onDismiss()
                                        } else {
                                            // 处理创建失败的情况
                                            // 可以显示一个 Toast 或错误消息
                                        }
                                    }
                                },
                                enabled = combinationName.value.isNotBlank() && selectedKeys.value.isNotEmpty()
                            ) {
                                Text(text = "创建")
                            }
                        }

                        Spacer(modifier = Modifier.height(8.dp)) // 减少间距，更紧凑

                        // 组合按键名称输入 - 去掉标题，直接显示输入框
                        OutlinedTextField(
                            value = combinationName.value,
                            onValueChange = { combinationName.value = it },
                            placeholder = { Text("输入组合按键名称") },
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(bottom = 12.dp) // 减少底部间距
                        )

                        // 选择的按键显示
                        Text(
                            text = "选择的按键 (${selectedKeys.value.size}/4)",
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(bottom = 8.dp)
                        )
                        
                        if (selectedKeys.value.isEmpty()) {
                            Box(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .weight(1f)
                                    .background(
                                        Color.LightGray.copy(alpha = 0.1f),
                                        MaterialTheme.shapes.small
                                    )
                                    .padding(16.dp),
                                contentAlignment = Alignment.Center
                            ) {
                                Text(
                                    text = "暂无选择的按键",
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f)
                                )
                            }
                        } else {
                            LazyColumn(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .weight(1f)
                            ) {
                                itemsIndexed(selectedKeys.value) { index, keyCode ->
                                    val keyName = getKeyName(keyCode)
                                    Row(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(vertical = 6.dp), // 减少垂直间距
                                        horizontalArrangement = Arrangement.SpaceBetween,
                                        verticalAlignment = Alignment.CenterVertically
                                    ) {
                                        Text(text = "${index + 1}. $keyName")
                                        IconButton(
                                            onClick = {
                                                val newList = selectedKeys.value.toMutableList()
                                                newList.removeAt(index)
                                                selectedKeys.value = newList
                                            },
                                            modifier = Modifier.size(20.dp) // 减小图标大小
                                        ) {
                                            Text(text = "❌", fontSize = 10.sp) // 减小字体大小
                                        }
                                    }
                                }
                            }
                        }

                        // 添加按键按钮
                        Button(
                            onClick = { 
                                if (selectedKeys.value.size < 4) {
                                    showKeySelection.value = true 
                                }
                            },
                            enabled = selectedKeys.value.size < 4,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 6.dp) // 减少垂直间距
                        ) {
                            Text(text = "➕ 添加按键")
                        }
                    }
                }
            }
        }

        @Composable
        fun KeySelectionDialog(
            initialSelectedKeys: List<Int>,
            onKeysSelected: (List<Int>) -> Unit,
            onDismiss: () -> Unit
        ) {
            val tempSelectedKeys = remember { mutableStateOf(initialSelectedKeys.toMutableList()) }

            Dialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .width(500.dp)  // 固定宽度
                        .height(500.dp), // 固定高度
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(16.dp)
                    ) {
                        // 顶部按钮行 - 取消在左，确定在右
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            TextButton(
                                onClick = onDismiss
                            ) {
                                Text(text = "取消")
                            }
                            
                            Button(
                                onClick = {
                                    onKeysSelected(tempSelectedKeys.value.toList())
                                },
                                enabled = tempSelectedKeys.value.isNotEmpty(),
                                colors = ButtonDefaults.buttonColors(
                                    containerColor = MaterialTheme.colorScheme.primary,
                                    contentColor = Color.White
                                )
                            ) {
                                Text(text = "确定")
                            }
                        }

                        Spacer(modifier = Modifier.height(16.dp))

                        // 标题
                        Text(
                            text = "选择按键 (${tempSelectedKeys.value.size}/4)",
                            style = MaterialTheme.typography.titleMedium,
                            modifier = Modifier.padding(bottom = 16.dp)
                        )

                        // 按键列表
                        LazyColumn(
                            modifier = Modifier
                                .fillMaxWidth()
                                .weight(1f)
                        ) {
                            itemsIndexed(getAvailableKeys()) { index, keyItem ->
                                val isSelected = tempSelectedKeys.value.contains(keyItem.keyCode)
                                KeySelectionItem(
                                    keyItem = keyItem,
                                    isSelected = isSelected,
                                    onClick = {
                                        val currentList = tempSelectedKeys.value.toMutableList()
                                        if (isSelected) {
                                            currentList.remove(keyItem.keyCode)
                                        } else {
                                            if (currentList.size < 4) {
                                                currentList.add(keyItem.keyCode)
                                            }
                                        }
                                        tempSelectedKeys.value = currentList
                                    },
                                    enabled = tempSelectedKeys.value.size < 4 || isSelected
                                )
                                
                                if (index < getAvailableKeys().size - 1) {
                                    HorizontalDivider(
                                        thickness = 0.5.dp,
                                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.1f)
                                    )
                                }
                            }
                        }

                        // 底部说明
                        Text(
                            text = "最多可选择4个按键",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = 8.dp)
                                .align(Alignment.CenterHorizontally)
                        )
                    }
                }
            }
        }

        @Composable
        fun KeySelectionItem(
            keyItem: KeyItem,
            isSelected: Boolean,
            onClick: () -> Unit,
            enabled: Boolean = true
        ) {
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 2.dp),
                color = if (isSelected) MaterialTheme.colorScheme.primary.copy(alpha = 0.2f) 
                       else Color.Transparent,
                onClick = onClick
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(16.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = keyItem.name,
                        style = MaterialTheme.typography.bodyMedium,
                        color = if (enabled) MaterialTheme.colorScheme.onSurface 
                               else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f)
                    )
                    if (isSelected) {
                        Text(
                            text = "✓",
                            color = MaterialTheme.colorScheme.primary,
                            fontSize = 16.sp
                        )
                    }
                }
            }
        }

        // 控件数据类
        data class ControlItem(
            val id: Int,
            val name: String,
            val description: String,
            val emoji: String,
            val type: ControlType
        )

        data class KeyItem(
            val keyCode: Int,
            val name: String
        )

        enum class ControlType {
            BUTTON, JOYSTICK, DPAD, COMBINATION
        }

        // 获取所有控件列表 - 添加组合按键
        fun getControlItems(): List<ControlItem> {
            val baseItems = listOf(
                // 按钮
                ControlItem(1, "A 按钮", "确认/主要动作", "🅰️", ControlType.BUTTON),
                ControlItem(2, "B 按钮", "取消/次要动作", "🅱️", ControlType.BUTTON),
                ControlItem(3, "X 按钮", "特殊功能", "❎", ControlType.BUTTON),
                ControlItem(4, "Y 按钮", "特殊功能", "💠", ControlType.BUTTON),
                ControlItem(5, "L 肩键", "左肩部按键", "🔗", ControlType.BUTTON),
                ControlItem(6, "R 肩键", "右肩部按键", "🔗", ControlType.BUTTON),
                ControlItem(7, "ZL 扳机", "左扳机键", "🎯", ControlType.BUTTON),
                ControlItem(8, "ZR 扳机", "右扳机键", "🎯", ControlType.BUTTON),
                ControlItem(9, "+ 按钮", "开始/菜单", "➕", ControlType.BUTTON),
                ControlItem(10, "- 按钮", "选择/返回", "➖", ControlType.BUTTON),
                ControlItem(11, "L3 按钮", "左摇杆按下", "🎮", ControlType.BUTTON),
                ControlItem(12, "R3 按钮", "右摇杆按下", "🎮", ControlType.BUTTON),
                
                // 摇杆
                ControlItem(101, "左摇杆", "移动/方向控制", "🕹️", ControlType.JOYSTICK),
                ControlItem(102, "右摇杆", "视角/镜头控制", "🕹️", ControlType.JOYSTICK),
                
                // 方向键 
                ControlItem(201, "方向键", "方向选择", "✛", ControlType.DPAD)
            )
            
            // 添加组合按键
            val combinations = MainActivity.mainViewModel?.controller?.getAllCombinations() ?: emptyList()
            val combinationItems = combinations.map { config ->
                ControlItem(
                    config.id,
                    config.name,  // 使用自定义名称而不是固定描述
                    "组合按键: ${config.keyCodes.joinToString("+") { getKeyName(it) }}",
                    "🔣",
                    ControlType.COMBINATION
                )
            }
            
            return baseItems + combinationItems
        }

        // 获取可用的按键列表 - 排除摇杆按键
        fun getAvailableKeys(): List<KeyItem> {
            return listOf(
                // 基础按钮
                KeyItem(0, "A 按钮"),
                KeyItem(1, "B 按钮"),
                KeyItem(2, "X 按钮"),
                KeyItem(3, "Y 按钮"),
                
                // 肩键和扳机
                KeyItem(4, "L 肩键"),
                KeyItem(5, "R 肩键"),
                KeyItem(6, "ZL 扳机"),
                KeyItem(7, "ZR 扳机"),
                
                // 功能按钮
                KeyItem(8, "+ 按钮"),
                KeyItem(9, "- 按钮"),
                KeyItem(10, "L3 按钮"),
                KeyItem(11, "R3 按钮"),
                
                // 方向键
                KeyItem(12, "上方向键"),
                KeyItem(13, "下方向键"),
                KeyItem(14, "左方向键"),
                KeyItem(15, "右方向键")
            )
        }

        // 获取按键名称
        fun getKeyName(keyCode: Int): String {
            return when (keyCode) {
                0 -> "A"
                1 -> "B"
                2 -> "X"
                3 -> "Y"
                4 -> "L"
                5 -> "R"
                6 -> "ZL"
                7 -> "ZR"
                8 -> "+"
                9 -> "-"
                10 -> "L3"
                11 -> "R3"
                12 -> "上"
                13 -> "下"
                14 -> "左"
                15 -> "右"
                else -> "未知"
            }
        }
    }
}
