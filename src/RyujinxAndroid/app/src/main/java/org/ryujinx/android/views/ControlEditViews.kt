package org.ryujinx.android.views

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.background // 添加这个导入
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

            // 主调整对话框
            Dialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .fillMaxWidth(0.9f)
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    ) {
                        // 标题行和按钮行 - 修改为按钮在标题左右
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            // 左侧：全部重置按钮
                            TextButton(
                                onClick = {
                                    // 重置所有单独设置 - 使用优化后的方法
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
                                                // 不重置组合按键，让用户单独管理
                                            }
                                        }
                                    }
                                    // 不需要调用 refreshControls()，因为单个更新方法已经优化
                                },
                                colors = ButtonDefaults.textButtonColors(
                                    contentColor = MaterialTheme.colorScheme.secondary
                                )
                            ) {
                                Text(text = "全部重置")
                            }

                            // 中间：标题
                            Text(
                                text = "调整按键设置",
                                style = MaterialTheme.typography.headlineSmall
                            )

                            // 右侧：确定按钮
                            TextButton(
                                onClick = {
                                    // 在关闭对话框时不需要调用 refreshControls()，因为单个更新方法已经优化
                                    onDismiss()
                                }
                            ) {
                                Text(text = "确定")
                            }
                        }

                        Spacer(modifier = Modifier.height(16.dp))

                        // 创建组合按键按钮
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.Center,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Button(
                                onClick = { showCreateCombination.value = true },
                                colors = ButtonDefaults.buttonColors(
                                    containerColor = MaterialTheme.colorScheme.primary,
                                    contentColor = Color.White
                                ),
                                modifier = Modifier.padding(vertical = 8.dp)
                            ) {
                                Row(
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(text = "➕", fontSize = 18.sp, modifier = Modifier.padding(end = 8.dp))
                                    Text(text = "创建组合按键")
                                }
                            }
                        }

                        Text(
                            text = "单个按键设置",
                            style = MaterialTheme.typography.titleMedium,
                            modifier = Modifier.padding(vertical = 8.dp)
                        )

                        // 按键列表 - 增加高度以充分利用空间
                        LazyColumn(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(400.dp) // 增加高度
                        ) {
                            itemsIndexed(getControlItems()) { index, control ->
                                ControlListItem(
                                    control = control,
                                    mainViewModel = mainViewModel,
                                    onClick = { selectedControl.value = control }
                                )
                                if (index < getControlItems().size - 1) {
                                    HorizontalDivider(
                                        modifier = Modifier.padding(horizontal = 8.dp),
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
                    .padding(vertical = 4.dp),
                color = Color.Transparent,
                onClick = onClick
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(16.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            text = control.emoji,
                            fontSize = 20.sp,
                            modifier = Modifier.padding(end = 12.dp)
                        )
                        Column {
                            Text(
                                text = control.name,
                                style = MaterialTheme.typography.bodyMedium
                            )
                            Text(
                                text = control.description,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
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
                            fontSize = 12.sp
                        )
                        Text(
                            text = "大小:${scale.value}% 透明:${opacity.value}%",
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                            fontSize = 10.sp
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

            Dialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .fillMaxWidth(0.8f)
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    ) {
                        // 标题行和按钮行 - 同样修改为按钮在标题左右
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            // 左侧：重置按钮（组合按键显示删除按钮）
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
                                        // 不需要调用 refreshControls()，因为单个更新方法已经优化
                                    },
                                    colors = ButtonDefaults.textButtonColors(
                                        contentColor = MaterialTheme.colorScheme.secondary
                                    )
                                ) {
                                    Text(text = "重置")
                                }
                            }

                            // 中间：标题
                            Row(
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(
                                    text = control.emoji,
                                    fontSize = 24.sp,
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

                            // 右侧：确定按钮
                            TextButton(
                                onClick = {
                                    // 在返回时不需要调用 refreshControls()，因为单个更新方法已经优化
                                    onDismiss()
                                }
                            ) {
                                Text(text = "确定")
                            }
                        }

                        Spacer(modifier = Modifier.height(16.dp))

                        // 单独缩放
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
                                // 不需要调用 refreshControls()，因为单个更新方法已经优化
                            },
                            valueRange = 10f..200f,
                            modifier = Modifier.fillMaxWidth()
                        )

                        Spacer(modifier = Modifier.height(16.dp))

                        // 单独透明度
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
                                // 不需要调用 refreshControls()，因为单个更新方法已经优化
                            },
                            valueRange = 0f..100f,
                            modifier = Modifier.fillMaxWidth()
                        )

                        Spacer(modifier = Modifier.height(16.dp))

                        // 隐藏显示开关
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
                                    // 不需要调用 refreshControls()，因为单个更新方法已经优化
                                }
                            )
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

            Dialog(onDismissRequest = onDismiss) {
                Surface(
                    modifier = Modifier
                        .fillMaxWidth(0.9f)
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    ) {
                        // 标题
                        Text(
                            text = "创建组合按键",
                            style = MaterialTheme.typography.headlineSmall,
                            modifier = Modifier
                                .padding(bottom = 16.dp)
                                .align(Alignment.CenterHorizontally)
                        )

                        // 组合按键名称输入
                        Text(
                            text = "组合按键名称",
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(bottom = 8.dp)
                        )
                        OutlinedTextField(
                            value = combinationName.value,
                            onValueChange = { combinationName.value = it },
                            placeholder = { Text("输入组合按键名称") },
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(bottom = 16.dp)
                        )

                        // 选择的按键显示
                        Text(
                            text = "选择的按键 (${selectedKeys.value.size}/4)",
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(bottom = 8.dp)
                        )
                        
                        if (selectedKeys.value.isEmpty()) {
                            Text(
                                text = "暂无选择的按键",
                                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f),
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(vertical = 16.dp)
                                    .background( // 这里使用了 background 修饰符
                                        Color.LightGray.copy(alpha = 0.2f),
                                        MaterialTheme.shapes.small
                                    )
                                    .padding(16.dp)
                            )
                        } else {
                            Column(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(vertical = 8.dp)
                            ) {
                                selectedKeys.value.forEachIndexed { index, keyCode ->
                                    val keyName = getKeyName(keyCode)
                                    Row(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(vertical = 4.dp),
                                        horizontalArrangement = Arrangement.SpaceBetween,
                                        verticalAlignment = Alignment.CenterVertically
                                    ) {
                                        Text(text = "${index + 1}. $keyName")
                                        IconButton(
                                            onClick = {
                                                // 创建新的列表并移除对应项
                                                val newList = selectedKeys.value.toMutableList()
                                                newList.removeAt(index)
                                                selectedKeys.value = newList
                                            },
                                            modifier = Modifier.size(24.dp)
                                        ) {
                                            Text(text = "❌", fontSize = 12.sp)
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
                                .padding(vertical = 8.dp)
                        ) {
                            Text(text = "➕ 添加按键")
                        }

                        Spacer(modifier = Modifier.height(16.dp))

                        // 按钮行
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            TextButton(
                                onClick = onDismiss
                            ) {
                                Text(text = "取消")
                            }
                            
                            Button(
                                onClick = {
                                    if (combinationName.value.isNotBlank() && selectedKeys.value.isNotEmpty()) {
                                        mainViewModel.controller?.createCombination(
                                            combinationName.value,
                                            selectedKeys.value
                                        )
                                        onCombinationCreated()
                                        onDismiss()
                                    }
                                },
                                enabled = combinationName.value.isNotBlank() && selectedKeys.value.isNotEmpty()
                            ) {
                                Text(text = "创建")
                            }
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
                        .fillMaxWidth(0.9f)
                        .wrapContentHeight(),
                    shape = MaterialTheme.shapes.large,
                    tonalElevation = AlertDialogDefaults.TonalElevation,
                    color = MaterialTheme.colorScheme.surface.copy(alpha = 0.95f)
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    ) {
                        // 顶部按钮行 - 添加确定按钮
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
                            
                            // 标题
                            Text(
                                text = "选择按键 (${tempSelectedKeys.value.size}/4)",
                                style = MaterialTheme.typography.titleMedium
                            )
                            
                            Button(
                                onClick = {
                                    onKeysSelected(tempSelectedKeys.value.toList())
                                },
                                enabled = tempSelectedKeys.value.isNotEmpty(),
                                colors = ButtonDefaults.buttonColors(
                                    disabledContainerColor = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.12f),
                                    disabledContentColor = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.38f)
                                )
                            ) {
                                Text(text = "确定")
                            }
                        }

                        Spacer(modifier = Modifier.height(16.dp))

                        // 按键列表 - 排除摇杆按键
                        LazyColumn(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(400.dp)
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
