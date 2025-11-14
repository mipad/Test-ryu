package org.ryujinx.android.views

import android.content.res.Configuration
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.remember
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import org.ryujinx.android.viewmodels.DlcItem
import org.ryujinx.android.viewmodels.DlcViewModel

class DlcViews {
    companion object {
        @Composable
        fun Main(titleId: String, name: String, openDialog: MutableState<Boolean>, canClose: MutableState<Boolean>) {
            val viewModel = remember { DlcViewModel(titleId) }
            val dlcItems = remember { SnapshotStateList<DlcItem>() }
            val configuration = LocalConfiguration.current
            val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
            val context = LocalContext.current
            
            // 使用 OpenMultipleDocuments 来支持多选文件
            val filePickerLauncher = rememberLauncherForActivityResult(
                ActivityResultContracts.OpenMultipleDocuments()
            ) { uris ->
                // 处理选中的多个文件
                if (uris.isNotEmpty()) {
                    viewModel.addSelectedFiles(uris, context)
                }
            }
            
            // 根据屏幕方向调整布局
            if (isLandscape) {
                // 横屏布局 - 右侧垂直按钮
                Row(modifier = Modifier.padding(16.dp)) {
                    // DLC列表区域 (占70%宽度)
                    Column(modifier = Modifier.weight(0.7f)) {
                        // 标题区域
                        Row(
                            modifier = Modifier
                                .padding(8.dp)
                                .fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            Text(
                                text = "DLC for $name",
                                textAlign = TextAlign.Center,
                                modifier = Modifier.align(Alignment.CenterVertically)
                            )
                        }
                        
                        Surface(
                            modifier = Modifier.padding(8.dp),
                            color = MaterialTheme.colorScheme.surfaceVariant,
                            shape = MaterialTheme.shapes.medium
                        ) {
                            viewModel.setDlcItems(dlcItems, canClose)
                            
                            LazyColumn(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .height(300.dp) // 固定高度，确保内容可滚动
                            ) {
                                items(dlcItems) { dlcItem ->
                                    Row(
                                        modifier = Modifier
                                            .padding(8.dp)
                                            .fillMaxWidth()
                                    ) {
                                        Checkbox(
                                            checked = dlcItem.isEnabled.value,
                                            onCheckedChange = { dlcItem.isEnabled.value = it }
                                        )
                                        Text(
                                            text = dlcItem.name,
                                            modifier = Modifier
                                                .align(Alignment.CenterVertically)
                                                .wrapContentWidth(Alignment.Start)
                                                .fillMaxWidth(0.7f)
                                        )
                                        IconButton(
                                            onClick = {
                                                viewModel.remove(dlcItem)
                                            }
                                        ) {
                                            Icon(
                                                Icons.Filled.Delete,
                                                contentDescription = "remove"
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    // 按钮区域 (占30%宽度，垂直排列)
                    Column(
                        modifier = Modifier
                            .weight(0.3f)
                            .padding(start = 16.dp),
                        verticalArrangement = Arrangement.Center,
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        // 添加按钮
                        IconButton(
                            modifier = Modifier.padding(8.dp),
                            onClick = {
                                // 启动文件选择器，支持多选
                                filePickerLauncher.launch(arrayOf("application/x-nx-nsp", "application/x-nx-xci", "*/*"))
                            }
                        ) {
                            Icon(
                                Icons.Filled.Add,
                                contentDescription = "Add"
                            )
                        }
                        
                        Spacer(modifier = Modifier.height(16.dp))
                        
                        // 保存按钮 - 横屏使用✔图标
                        IconButton(
                            modifier = Modifier.padding(8.dp),
                            onClick = {
                                canClose.value = true
                                viewModel.save(openDialog)
                            }
                        ) {
                            Icon(
                                Icons.Filled.Check,
                                contentDescription = "Save"
                            )
                        }
                    }
                }
            } else {
                // 竖屏布局 - 保持原有设计
                Column(modifier = Modifier.padding(16.dp)) {
                    // 标题区域 
                    Column {
                        Row(
                            modifier = Modifier
                                .padding(8.dp)
                                .fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            Text(
                                text = "DLC for $name",
                                textAlign = TextAlign.Center,
                                modifier = Modifier.align(Alignment.CenterVertically)
                            )
                        }
                        
                        // DLC列表区域 
                        Surface(
                            modifier = Modifier.padding(8.dp),
                            color = MaterialTheme.colorScheme.surfaceVariant,
                            shape = MaterialTheme.shapes.medium
                        ) {
                            viewModel.setDlcItems(dlcItems, canClose)
                            
                            LazyColumn(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .height(400.dp)
                            ) {
                                items(dlcItems) { dlcItem ->
                                    Row(
                                        modifier = Modifier
                                            .padding(8.dp)
                                            .fillMaxWidth()
                                    ) {
                                        Checkbox(
                                            checked = dlcItem.isEnabled.value,
                                            onCheckedChange = { dlcItem.isEnabled.value = it }
                                        )
                                        Text(
                                            text = dlcItem.name,
                                            modifier = Modifier
                                                .align(Alignment.CenterVertically)
                                                .wrapContentWidth(Alignment.Start)
                                                .fillMaxWidth(0.9f)
                                        )
                                        IconButton(
                                            onClick = {
                                                viewModel.remove(dlcItem)
                                            }
                                        ) {
                                            Icon(
                                                Icons.Filled.Delete,
                                                contentDescription = "remove"
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    Spacer(modifier = Modifier.height(8.dp))
                    
                    // 底部按钮区域 
                    Row(modifier = Modifier.align(Alignment.End)) {
                        // 添加按钮 
                        IconButton(
                            modifier = Modifier.padding(4.dp),
                            onClick = {
                                // 启动文件选择器，支持多选
                                filePickerLauncher.launch(arrayOf("application/x-nx-nsp", "application/x-nx-xci", "*/*"))
                            }
                        ) {
                            Icon(
                                Icons.Filled.Add,
                                contentDescription = "Add"
                            )
                        }
                        
                        // 保存按钮 - 竖屏使用文本
                        TextButton(
                            modifier = Modifier.padding(4.dp),
                            onClick = {
                                canClose.value = true
                                viewModel.save(openDialog)
                            }
                        ) {
                            Text("Save")
                        }
                    }
                }
            }
        }
    }
}