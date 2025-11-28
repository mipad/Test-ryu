package org.ryujinx.android.views

import android.net.Uri
import android.content.res.Configuration
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.documentfile.provider.DocumentFile
import org.ryujinx.android.MainActivity
import org.ryujinx.android.viewmodels.TitleUpdateViewModel
import java.io.File

class TitleUpdateViews {
    companion object {
        @Composable
        fun Main(
            titleId: String,
            name: String,
            openDialog: MutableState<Boolean>,
            canClose: MutableState<Boolean>
        ) {
            val viewModel = TitleUpdateViewModel(titleId)
            val configuration = LocalConfiguration.current
            val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
            
            // 根据屏幕方向调整高度
            val contentHeight = if (isLandscape) 200.dp else 280.dp

            val selectedIndex = remember { mutableIntStateOf(0) }
            viewModel.data?.apply {
                selectedIndex.intValue = paths.indexOf(this.selected) + 1
            }

            Column(modifier = Modifier.padding(16.dp)) {
                // 操作按钮行
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    // 左侧操作按钮
                    Row {
                        IconButton(
                            onClick = {
                                viewModel.remove(selectedIndex.intValue)
                            }
                        ) {
                            Icon(Icons.Filled.Delete, contentDescription = "Remove")
                        }
                        IconButton(
                            onClick = {
                                viewModel.add()
                            }
                        ) {
                            Icon(Icons.Filled.Add, contentDescription = "Add")
                        }
                    }
                    
                    // 右侧保存按钮 - 使用文字按钮
                    TextButton(
                        onClick = {
                            canClose.value = true
                            viewModel.save(selectedIndex.intValue, openDialog)
                        }
                    ) {
                        Text("Save")
                    }
                }
                
                Spacer(modifier = Modifier.height(8.dp))
                
                Column {
                    Text(
                        text = "Updates for $name", 
                        textAlign = TextAlign.Center,
                        style = MaterialTheme.typography.titleMedium,
                        modifier = Modifier.fillMaxWidth()
                    )
                    
                    Surface(
                        modifier = Modifier
                            .padding(8.dp)
                            .height(contentHeight),
                        color = MaterialTheme.colorScheme.surfaceVariant,
                        shape = MaterialTheme.shapes.medium
                    ) {
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .verticalScroll(rememberScrollState())
                                .padding(8.dp)
                        ) {
                            // None 选项
                            Row(
                                modifier = Modifier.padding(vertical = 4.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                RadioButton(
                                    selected = (selectedIndex.intValue == 0),
                                    onClick = { selectedIndex.intValue = 0 }
                                )
                                Text(
                                    text = "None",
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(start = 8.dp)
                                )
                            }

                            // 文件列表
                            val paths = remember { mutableStateListOf<String>() }
                            viewModel.setPaths(paths, canClose)
                            
                            paths.forEachIndexed { index, path ->
                                val itemIndex = index + 1
                                val fileName = getFileNameFromPath(path)
                                
                                if (fileName != null) {
                                    Row(
                                        modifier = Modifier.padding(vertical = 4.dp),
                                        verticalAlignment = Alignment.CenterVertically
                                    ) {
                                        RadioButton(
                                            selected = (selectedIndex.intValue == itemIndex),
                                            onClick = { selectedIndex.intValue = itemIndex }
                                        )
                                        Column(
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .padding(start = 8.dp)
                                        ) {
                                            Text(
                                                text = fileName,
                                                style = MaterialTheme.typography.bodyMedium
                                            )
                                            Text(
                                                text = getPathType(path),
                                                style = MaterialTheme.typography.labelSmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant
                                            )
                                        }
                                    }
                                }
                            }

                            Spacer(modifier = Modifier.height(8.dp))
                        }
                    }
                }
            }
        }
        
        /**
         * 从路径获取文件名 - 支持 URI 和文件路径
         */
        private fun getFileNameFromPath(path: String): String? {
            return try {
                if (path.startsWith("content://")) {
                    // URI 路径
                    val uri = Uri.parse(path)
                    val documentFile = DocumentFile.fromSingleUri(MainActivity.mainViewModel!!.activity, uri)
                    documentFile?.name ?: "Unknown File"
                } else {
                    // 文件系统路径
                    val file = File(path)
                    if (file.exists()) {
                        file.name
                    } else {
                        // 尝试从路径中提取文件名
                        path.substringAfterLast('/').takeIf { it.isNotEmpty() } ?: "Invalid Path"
                    }
                }
            } catch (e: Exception) {
                "Error: ${e.message}"
            }
        }
        
        /**
         * 获取路径类型显示
         */
        private fun getPathType(path: String): String {
            return if (path.startsWith("content://")) {
                "URI Path"
            } else {
                "File Path"
            }
        }
        
        /**
         * 检查文件是否存在 - 支持两种路径格式
         */
        private fun isFileExists(path: String): Boolean {
            return try {
                if (path.startsWith("content://")) {
                    val uri = Uri.parse(path)
                    val documentFile = DocumentFile.fromSingleUri(MainActivity.mainViewModel!!.activity, uri)
                    documentFile?.exists() ?: false
                } else {
                    File(path).exists()
                }
            } catch (e: Exception) {
                false
            }
        }
    }
}
