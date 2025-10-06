package org.ryujinx.android.views

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.navigation.NavHostController
import org.ryujinx.android.viewmodels.SaveDataViewModel
import org.ryujinx.android.viewmodels.formatDate
import org.ryujinx.android.viewmodels.formatFileSize

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SaveDataViews(navController: NavHostController, titleId: String, gameName: String) {
    val context = LocalContext.current
    val viewModel: SaveDataViewModel = viewModel()
    
    // 文件选择器
    val filePickerLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.GetContent()
    ) { uri ->
        uri?.let { selectedUri ->
            viewModel.importSaveDataFromUri(selectedUri, context)
        }
    }
    
    // 初始化ViewModel
    LaunchedEffect(titleId, gameName) {
        viewModel.initialize(titleId, gameName)
    }
    
    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text("Save Data Management") },
                navigationIcon = {
                    IconButton(onClick = { navController.popBackStack() }) {
                        Icon(Icons.Filled.ArrowBack, contentDescription = "Back")
                    }
                }
            )
        }
    ) { contentPadding ->
        Column(
            modifier = Modifier
                .padding(contentPadding)
                .fillMaxSize()
                .padding(16.dp)
        ) {
            // 游戏信息
            Card(
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(
                    modifier = Modifier.padding(16.dp)
                ) {
                    Text(
                        text = gameName,
                        style = MaterialTheme.typography.headlineSmall,
                        maxLines = 1,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                    )
                    Text(
                        text = "Title ID: $titleId",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(16.dp))
            
            // 存档信息
            if (viewModel.saveDataInfo.value != null) {
                Card(
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Column(
                        modifier = Modifier.padding(16.dp)
                    ) {
                        Text(
                            text = "Save Data Info",
                            style = MaterialTheme.typography.titleMedium
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        InfoRow("Save ID:", viewModel.saveDataInfo.value!!.saveId)
                        InfoRow("Last Modified:", formatDate(viewModel.saveDataInfo.value!!.lastModified))
                        InfoRow("Size:", formatFileSize(viewModel.saveDataInfo.value!!.size))
                    }
                }
            } else {
                Card(
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Column(
                        modifier = Modifier.padding(16.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        Text(
                            "No save data found for this game",
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
            
            Spacer(modifier = Modifier.height(24.dp))
            
            // 操作按钮
            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                ActionButton(
                    text = "Export Save Data",
                    enabled = viewModel.hasSaveData() && !viewModel.operationInProgress.value
                ) {
                    viewModel.exportSaveData(context)
                }
                
                ActionButton(
                    text = "Import Save Data", 
                    enabled = !viewModel.operationInProgress.value
                ) {
                    // 打开文件选择器选择ZIP文件
                    filePickerLauncher.launch("application/zip")
                }
                
                ActionButton(
                    text = "Delete Save Data",
                    enabled = viewModel.hasSaveData() && !viewModel.operationInProgress.value,
                    isDestructive = true
                ) {
                    viewModel.deleteSaveData()
                }
            }
        }
    }
    
    // 操作进度对话框
    if (viewModel.operationInProgress.value) {
        AlertDialog(
            onDismissRequest = { /* 不允许取消 */ },
            title = { Text("Processing") },
            text = { 
                Column {
                    Text(viewModel.operationMessage.value)
                    Spacer(modifier = Modifier.height(16.dp))
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                }
            },
            confirmButton = {}
        )
    }
    
    // 操作结果对话框
    if (viewModel.showOperationResult.value) {
        AlertDialog(
            onDismissRequest = { viewModel.resetOperationResult() },
            title = { 
                Text(if (viewModel.operationSuccess.value) "Success" else "Error") 
            },
            text = { Text(viewModel.operationMessage.value) },
            confirmButton = {
                TextButton(onClick = { 
                    viewModel.resetOperationResult()
                    // 如果删除成功且我们还在当前页面，返回上一页
                    if (viewModel.operationSuccess.value && !viewModel.hasSaveData()) {
                        navController.popBackStack()
                    }
                }) {
                    Text("OK")
                }
            }
        )
    }
}

@Composable
fun InfoRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(text = label, style = MaterialTheme.typography.bodyMedium)
        Text(text = value, style = MaterialTheme.typography.bodyMedium)
    }
}

@Composable
fun ActionButton(
    text: String,
    enabled: Boolean = true,
    isDestructive: Boolean = false,
    onClick: () -> Unit
) {
    val buttonColors = if (isDestructive) {
        ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)
    } else {
        ButtonDefaults.buttonColors()
    }
    
    Button(
        onClick = onClick,
        modifier = Modifier.fillMaxWidth(),
        enabled = enabled,
        colors = buttonColors
    ) {
        Text(text = text)
    }
}
