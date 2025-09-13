package org.ryujinx.android.views

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.navigation.NavController
import org.ryujinx.android.viewmodels.CheatsViewModel
import androidx.compose.ui.Alignment

@OptIn(ExperimentalMaterial3Api::class) // 添加这个注解来处理实验性API警告
@Composable
fun CheatsViews(
    navController: NavController,
    titleId: String,
    gamePath: String
) {
    val viewModel = remember { CheatsViewModel(titleId, gamePath) }
    val cheats by viewModel.cheats.collectAsState(emptyList())
    val isLoading by viewModel.isLoading.collectAsState()
    val errorMessage by viewModel.errorMessage.collectAsState()
    
    // 显示错误对话框
    if (errorMessage != null) {
        AlertDialog(
            onDismissRequest = { viewModel.clearError() },
            title = { Text("Error") },
            text = { Text(errorMessage!!) },
            confirmButton = {
                TextButton(
                    onClick = { viewModel.clearError() }
                ) {
                    Text("OK")
                }
            }
        )
    }
    
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Manage Cheats") },
                navigationIcon = {
                    IconButton(onClick = { navController.popBackStack() }) {
                        Icon(Icons.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    // 使用文字按钮代替图标
                    TextButton(
                        onClick = { viewModel.saveCheats() },
                        enabled = !isLoading
                    ) {
                        Text("Save")
                    }
                }
            )
        }
    ) { innerPadding ->
        if (isLoading) {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(innerPadding),
                contentAlignment = Alignment.Center
            ) {
                CircularProgressIndicator()
            }
        } else if (cheats.isEmpty()) {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(innerPadding),
                contentAlignment = Alignment.Center
            ) {
                Text("No cheats found for this game")
            }
        } else {
            LazyColumn(
                modifier = Modifier
                    .padding(innerPadding)
                    .fillMaxSize()
            ) {
                items(cheats) { cheat ->
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp),
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Text(
                            text = cheat.name,
                            modifier = Modifier.weight(1f)
                        )
                        Switch(
                            checked = cheat.enabled,
                            onCheckedChange = { enabled ->
                                viewModel.setCheatEnabled(cheat.id, enabled)
                            }
                        )
                    }
                    Divider()
                }
            }
        }
    }
}
