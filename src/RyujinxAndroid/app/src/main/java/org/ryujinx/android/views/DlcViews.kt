package org.ryujinx.android.views

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import org.ryujinx.android.viewmodels.BatchInstallProgress
import org.ryujinx.android.viewmodels.DlcItem
import org.ryujinx.android.viewmodels.DlcViewModel

class DlcViews {
    companion object {
        @Composable
        fun Main(titleId: String, name: String, openDialog: MutableState<Boolean>) {
            val viewModel = remember { DlcViewModel(titleId) }
            val dlcItemsState = remember { SnapshotStateList<DlcItem>() }
            val canClose = remember { mutableStateOf(false) }
            val batchProgress = remember { viewModel.batchInstallProgress }

            // 初始化ViewModel与状态列表的连接
            LaunchedEffect(viewModel) {
                viewModel.setDlcItems(dlcItemsState, canClose)
            }

            // 处理canClose状态变化
            LaunchedEffect(canClose.value) {
                if (canClose.value) {
                    // 可以执行关闭操作，但这里我们只是重置状态
                    canClose.value = false
                }
            }

            Column(modifier = Modifier.padding(16.dp)) {
                Column {
                    Row(
                        modifier = Modifier
                            .padding(8.dp)
                            .fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text(
                            text = "DLC for ${name}",
                            textAlign = TextAlign.Center,
                            modifier = Modifier.align(Alignment.CenterVertically)
                        )
                    }
                    
                    // 批量安装进度显示
                    when (val progress = batchProgress.value) {
                        is BatchInstallProgress.RUNNING -> {
                            Column(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp)
                            ) {
                                Text(
                                    text = "批量安装中: ${progress.processed}/${progress.total}",
                                    style = MaterialTheme.typography.bodySmall
                                )
                                LinearProgressIndicator(
                                    progress = progress.processed.toFloat() / progress.total.toFloat(),
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .height(4.dp)
                                )
                            }
                        }
                        is BatchInstallProgress.COMPLETED -> {
                            Text(
                                text = "批量安装完成: ${progress.success}/${progress.total} 个成功",
                                style = MaterialTheme.typography.bodySmall,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(8.dp)
                            )
                        }
                        else -> {
                            // IDLE状态，不显示任何内容
                        }
                    }
                    
                    Surface(
                        modifier = Modifier.padding(8.dp),
                        color = MaterialTheme.colorScheme.surfaceVariant,
                        shape = MaterialTheme.shapes.medium
                    ) {
                        if (dlcItemsState.isEmpty()) {
                            Column(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .height(200.dp)
                                    .padding(16.dp),
                                verticalArrangement = Arrangement.Center,
                                horizontalAlignment = Alignment.CenterHorizontally
                            ) {
                                Text(
                                    text = "没有DLC内容",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        } else {
                            LazyColumn(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .height(400.dp)
                            ) {
                                items(dlcItemsState) { dlcItem ->
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
                }
                
                Spacer(modifier = Modifier.height(8.dp))
                
                // 操作按钮行
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    // 左侧按钮组
                    Row {
                        TextButton(
                            modifier = Modifier.padding(4.dp),
                            onClick = {
                                viewModel.add()
                            }
                        ) {
                            Text("添加单个")
                        }
                        
                        // 批量添加按钮 - 只有在没有进行中的批量操作时才显示
                        if (batchProgress.value is BatchInstallProgress.IDLE || 
                            batchProgress.value is BatchInstallProgress.COMPLETED) {
                            TextButton(
                                modifier = Modifier.padding(4.dp),
                                onClick = {
                                    viewModel.addBatch()
                                }
                            ) {
                                Text("批量添加")
                            }
                        } else {
                            // 显示加载中的批量按钮
                            Button(
                                modifier = Modifier.padding(4.dp),
                                onClick = { /* 无操作 */ },
                                enabled = false
                            ) {
                                CircularProgressIndicator(
                                    modifier = Modifier
                                        .size(16.dp)
                                        .padding(end = 8.dp)
                                )
                                Text("处理中...")
                            }
                        }
                        
                        // 清空所有按钮
                        OutlinedButton(
                            modifier = Modifier.padding(4.dp),
                            onClick = {
                                viewModel.removeAll()
                            }
                        ) {
                            Text("清空所有")
                        }
                    }
                    
                    // 右侧按钮组
                    Row {
                        TextButton(
                            modifier = Modifier.padding(4.dp),
                            onClick = {
                                openDialog.value = false
                            }
                        ) {
                            Text("取消")
                        }
                        TextButton(
                            modifier = Modifier.padding(4.dp),
                            onClick = {
                                viewModel.save(openDialog)
                            }
                        ) {
                            Text("保存")
                        }
                    }
                }
            }
        }
    }
}
