package org.ryujinx.android.views

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Divider
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.ryujinx.android.RyujinxNative
import org.ryujinx.android.viewmodels.GameModel
import org.ryujinx.android.viewmodels.NetworkViewModel
import org.ryujinx.android.viewmodels.NetworkStatus
import org.ryujinx.android.viewmodels.SettingsViewModel
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import org.ryujinx.android.viewmodels.MainViewModel
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.filled.Close
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow

@Composable
fun NetworkView(settingsViewModel: SettingsViewModel, mainViewModel: MainViewModel) {
    // 直接使用 mainViewModel 中的 homeViewModel 来获取游戏列表
    val networkViewModel = remember { NetworkViewModel(settingsViewModel.activity) }
    
    // 设置游戏列表 - 使用 homeViewModel 的 gameList
    LaunchedEffect(mainViewModel.homeViewModel.gameList) {
        val gameList = mainViewModel.homeViewModel.gameList.toList()
        println("DEBUG: Setting game list with ${gameList.size} games")
        networkViewModel.setGameList(gameList)
    }
    
    Column(
        modifier = Modifier
            .verticalScroll(rememberScrollState())
            .padding(16.dp)
    ) {
        // Network status overview card
        NetworkStatusCard(networkViewModel)
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // Multiplayer settings
        MultiplayerSettingsCard(networkViewModel)
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // 只在启用 LDN 模式时显示大厅管理
        if (networkViewModel.multiplayerModeIndex.value == 1) {
            LobbyManagementCard(networkViewModel, mainViewModel)
            Spacer(modifier = Modifier.height(16.dp))
        }
        
        // Network interface settings
        NetworkInterfaceCard(networkViewModel)
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // Network information description
        NetworkInfoCard()
    }
}

@Composable
fun NetworkStatusCard(networkViewModel: NetworkViewModel) {
    val networkStatus = networkViewModel.getNetworkStatus()
    val statusColor = when (networkStatus) {
        NetworkStatus.CONNECTED_WIFI -> MaterialTheme.colorScheme.primary
        NetworkStatus.CONNECTED_MOBILE -> MaterialTheme.colorScheme.primary
        NetworkStatus.CONNECTED_ETHERNET -> MaterialTheme.colorScheme.primary
        NetworkStatus.CONNECTED_UNKNOWN -> MaterialTheme.colorScheme.primary
        NetworkStatus.DISCONNECTED -> MaterialTheme.colorScheme.error
        NetworkStatus.UNKNOWN -> MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
    }
    
    OutlinedCard(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.outlinedCardColors(
            containerColor = MaterialTheme.colorScheme.surfaceVariant
        )
    ) {
        Column(
            modifier = Modifier.padding(16.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                // Use text instead of icon
                Text(
                    text = "🌐", // Use emoji as simple network icon
                    style = MaterialTheme.typography.titleMedium
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "Network Status",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold,
                    color = statusColor
                )
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            
            // Network connection status
            Row(
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("Network Connection:")
                Text(
                    text = networkViewModel.getNetworkStatusText(),
                    color = statusColor
                )
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            // Internet access setting status
            Row(
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("Internet Access:")
                Text(
                    text = if (networkViewModel.enableInternetAccess.value) "Enabled" else "Disabled",
                    color = if (networkViewModel.enableInternetAccess.value) MaterialTheme.colorScheme.primary 
                           else MaterialTheme.colorScheme.error
                )
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Row(
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("Multiplayer Mode:")
                Text(
                    text = networkViewModel.getMultiplayerModeName(networkViewModel.multiplayerModeIndex.value),
                    color = MaterialTheme.colorScheme.secondary
                )
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Row(
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("Network Interface:")
                Text(
                    text = networkViewModel.networkInterfaceList.getOrNull(networkViewModel.networkInterfaceIndex.value)?.name ?: "Default",
                    color = MaterialTheme.colorScheme.secondary
                )
            }
            
            // 显示当前大厅状态
            networkViewModel.currentLobby.value?.let { lobby ->
                Spacer(modifier = Modifier.height(8.dp))
                Divider()
                Spacer(modifier = Modifier.height(8.dp))
                Row(
                    horizontalArrangement = Arrangement.SpaceBetween,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("Current Lobby:")
                    Text(
                        text = lobby.name,
                        color = MaterialTheme.colorScheme.primary,
                        fontWeight = FontWeight.Bold
                    )
                }
                Spacer(modifier = Modifier.height(4.dp))
                Row(
                    horizontalArrangement = Arrangement.SpaceBetween,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("Players:")
                    Text(
                        text = "${lobby.playerCount}/${lobby.maxPlayers}",
                        color = MaterialTheme.colorScheme.secondary
                    )
                }
                Spacer(modifier = Modifier.height(4.dp))
                Row(
                    horizontalArrangement = Arrangement.SpaceBetween,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("Game:")
                    Text(
                        text = lobby.gameTitle,
                        color = MaterialTheme.colorScheme.secondary
                    )
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MultiplayerSettingsCard(networkViewModel: NetworkViewModel) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surface
        )
    ) {
        Column(
            modifier = Modifier.padding(16.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Icon(
                    imageVector = Icons.Filled.Settings,
                    contentDescription = "Multiplayer Settings",
                    tint = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "Multiplayer Settings",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold
                )
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            Divider()
            Spacer(modifier = Modifier.height(12.dp))
            
            // Enable internet access switch
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Column {
                    Text(
                        text = "Enable Internet Access",
                        style = MaterialTheme.typography.bodyMedium
                    )
                    Text(
                        text = "Allow the emulator to access internet services",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                    )
                }
                Switch(
                    checked = networkViewModel.enableInternetAccess.value,
                    onCheckedChange = { networkViewModel.setEnableInternetAccess(it) }
                )
            }
            
            Spacer(modifier = Modifier.height(16.dp))
            
            // Multiplayer mode selection
            Text(
                text = "Multiplayer Mode:",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(bottom = 8.dp)
            )
            
            // Disabled mode
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                RadioButton(
                    selected = networkViewModel.multiplayerModeIndex.value == 0,
                    onClick = { networkViewModel.setMultiplayerMode(0) }
                )
                Spacer(modifier = Modifier.width(8.dp))
                Column {
                    Text(
                        text = "Disabled",
                        style = MaterialTheme.typography.bodyMedium
                    )
                    Text(
                        text = "Completely disable multiplayer features",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            // LDN local wireless mode
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                RadioButton(
                    selected = networkViewModel.multiplayerModeIndex.value == 1,
                    onClick = { networkViewModel.setMultiplayerMode(1) }
                )
                Spacer(modifier = Modifier.width(8.dp))
                Column {
                    Text(
                        text = "LDN Local Wireless",
                        style = MaterialTheme.typography.bodyMedium
                    )
                    Text(
                        text = "Connect with other Ryujinx devices over local network",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                    )
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LobbyManagementCard(networkViewModel: NetworkViewModel, mainViewModel: MainViewModel) {
    // 添加创建大厅对话框状态管理
    if (networkViewModel.showCreateLobbyDialog.value) {
        CreateLobbyDialog(
            onDismiss = { networkViewModel.showCreateLobbyDialog.value = false },
            onCreate = { lobbyName, gameTitle, maxPlayers ->
                networkViewModel.createLobby(lobbyName, gameTitle, maxPlayers, "")
            },
            gameList = mainViewModel.homeViewModel.gameList.toList() // 直接从 homeViewModel 获取游戏列表
        )
    }
    
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surface
        )
    ) {
        Column(
            modifier = Modifier.padding(16.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                // 使用表情符号代替图标
                Text(
                    text = "👥", // 使用人物表情符号
                    style = MaterialTheme.typography.titleMedium
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "Game Lobby",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold
                )
                
                Spacer(modifier = Modifier.weight(1f))
                
                // 刷新大厅列表按钮
                IconButton(
                    onClick = { networkViewModel.refreshLobbyList() }
                ) {
                    Icon(
                        imageVector = Icons.Filled.Refresh,
                        contentDescription = "Refresh Lobbies"
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            Divider()
            Spacer(modifier = Modifier.height(12.dp))
            
            when (networkViewModel.lobbyState.value) {
                org.ryujinx.android.viewmodels.LobbyState.IDLE -> IdleLobbyView(networkViewModel)
                org.ryujinx.android.viewmodels.LobbyState.HOSTING -> HostingLobbyView(networkViewModel)
                org.ryujinx.android.viewmodels.LobbyState.IN_LOBBY -> InLobbyView(networkViewModel)
                org.ryujinx.android.viewmodels.LobbyState.CREATING, 
                org.ryujinx.android.viewmodels.LobbyState.JOINING -> {
                    // 加载状态
                    Column(
                        horizontalAlignment = Alignment.CenterHorizontally,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        CircularProgressIndicator()
                        Spacer(modifier = Modifier.height(8.dp))
                        Text("Connecting...")
                    }
                }
                else -> {}
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CreateLobbyDialog(
    onDismiss: () -> Unit,
    onCreate: (String, String, Int) -> Unit,
    gameList: List<GameModel>
) {
    var lobbyName by remember { mutableStateOf("") }
    var selectedGameName by remember { mutableStateOf("") }
    var maxPlayers by remember { mutableStateOf(4) }
    var showGameSelectionDialog by remember { mutableStateOf(false) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("创建大厅") },
        text = {
            Column {
                OutlinedTextField(
                    value = lobbyName,
                    onValueChange = { lobbyName = it },
                    label = { Text("大厅名称") },
                    modifier = Modifier.fillMaxWidth(),
                    placeholder = { Text("输入大厅名称") }
                )
                Spacer(modifier = Modifier.height(12.dp))
                
                // 显示游戏数量信息
                Text(
                    text = "找到 ${gameList.size} 个游戏",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                    modifier = Modifier.padding(bottom = 4.dp)
                )
                
                // 游戏选择
                Text("选择游戏:", style = MaterialTheme.typography.bodyMedium)
                Spacer(modifier = Modifier.height(4.dp))
                
                // 游戏选择框 - 使用按钮打开游戏选择对话框
                OutlinedTextField(
                    value = selectedGameName,
                    onValueChange = { }, // 不允许直接编辑
                    label = { Text("选择游戏") },
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable { 
                            if (gameList.isNotEmpty()) {
                                showGameSelectionDialog = true 
                            }
                        },
                    placeholder = { 
                        if (gameList.isEmpty()) {
                            Text("没有找到游戏")
                        } else {
                            Text("点击选择游戏")
                        }
                    },
                    readOnly = true,
                    trailingIcon = {
                        Icon(
                            imageVector = Icons.Filled.ArrowDropDown,
                            contentDescription = "选择游戏",
                            modifier = Modifier.clickable { 
                                if (gameList.isNotEmpty()) {
                                    showGameSelectionDialog = true 
                                }
                            }
                        )
                    },
                    enabled = gameList.isNotEmpty()
                )
                
                Spacer(modifier = Modifier.height(12.dp))
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        text = "最大玩家数:",
                        style = MaterialTheme.typography.bodyMedium
                    )
                    Spacer(modifier = Modifier.weight(1f))
                    Button(
                        onClick = { if (maxPlayers > 1) maxPlayers-- },
                        colors = ButtonDefaults.buttonColors(
                            containerColor = MaterialTheme.colorScheme.secondaryContainer
                        ),
                        modifier = Modifier.width(48.dp)
                    ) {
                        Text("-")
                    }
                    Text(
                        text = "$maxPlayers",
                        modifier = Modifier.padding(horizontal = 16.dp),
                        fontWeight = FontWeight.Bold,
                        fontSize = 16.sp
                    )
                    Button(
                        onClick = { if (maxPlayers < 8) maxPlayers++ },
                        colors = ButtonDefaults.buttonColors(
                            containerColor = MaterialTheme.colorScheme.secondaryContainer
                        ),
                        modifier = Modifier.width(48.dp)
                    ) {
                        Text("+")
                    }
                }
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    if (lobbyName.isNotBlank() && selectedGameName.isNotBlank()) {
                        onCreate(lobbyName, selectedGameName, maxPlayers)
                        onDismiss()
                    }
                },
                enabled = lobbyName.isNotBlank() && selectedGameName.isNotBlank()
            ) {
                Text("创建")
            }
        },
        dismissButton = {
            Button(
                onClick = onDismiss,
                colors = ButtonDefaults.buttonColors(
                    containerColor = MaterialTheme.colorScheme.surfaceVariant
                )
            ) {
                Text("取消")
            }
        }
    )

    // 游戏选择对话框
    if (showGameSelectionDialog) {
        GameSelectionDialog(
            games = gameList,
            onDismiss = { showGameSelectionDialog = false },
            onGameSelected = { gameName ->
                selectedGameName = gameName
                showGameSelectionDialog = false
            }
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun GameSelectionDialog(
    games: List<GameModel>,
    onDismiss: () -> Unit,
    onGameSelected: (String) -> Unit
) {
    var searchText by remember { mutableStateOf("") }
    val filteredGames = remember(searchText, games) {
        if (searchText.isBlank()) {
            games
        } else {
            games.filter { game ->
                game.getDisplayName().contains(searchText, ignoreCase = true) ||
                (game.titleId?.contains(searchText, ignoreCase = true) == true)
            }
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { 
            Column {
                Text("选择游戏")
                Text(
                    text = "找到 ${filteredGames.size} 个游戏",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                )
            }
        },
        text = {
            Column {
                // 搜索框
                OutlinedTextField(
                    value = searchText,
                    onValueChange = { searchText = it },
                    label = { Text("搜索游戏") },
                    placeholder = { Text("输入游戏名称或ID") },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                    keyboardActions = KeyboardActions(onSearch = { /* 搜索已自动执行 */ }),
                    trailingIcon = {
                        if (searchText.isNotEmpty()) {
                            IconButton(
                                onClick = { searchText = "" }
                            ) {
                                Icon(
                                    imageVector = Icons.Filled.Close,
                                    contentDescription = "清除搜索"
                                )
                            }
                        }
                    }
                )
                Spacer(modifier = Modifier.height(8.dp))
                
                // 游戏列表
                LazyColumn(
                    modifier = Modifier.height(300.dp)
                ) {
                    items(filteredGames) { game ->
                        Card(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 4.dp)
                                .clickable { onGameSelected(game.getDisplayName()) },
                            colors = CardDefaults.cardColors(
                                containerColor = MaterialTheme.colorScheme.surfaceVariant
                            )
                        ) {
                            Column(
                                modifier = Modifier.padding(12.dp)
                            ) {
                                Text(
                                    text = game.getDisplayName(),
                                    style = MaterialTheme.typography.bodyMedium,
                                    fontWeight = FontWeight.Bold,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                                Spacer(modifier = Modifier.height(2.dp))
                                Text(
                                    text = "ID: ${game.titleId ?: "Unknown"}",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                                )
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            Button(onClick = onDismiss) {
                Text("关闭")
            }
        }
    )
}

@Composable
fun IdleLobbyView(networkViewModel: NetworkViewModel) {
    Column {
        // 创建大厅按钮
        Button(
            onClick = { 
                println("DEBUG: Create lobby button clicked")
                networkViewModel.showCreateLobbyDialog.value = true 
            },
            modifier = Modifier.fillMaxWidth(),
            colors = ButtonDefaults.buttonColors(
                containerColor = MaterialTheme.colorScheme.primary
            )
        ) {
            Icon(
                imageVector = Icons.Filled.Settings,
                contentDescription = "Create Lobby"
            )
            Spacer(modifier = Modifier.width(8.dp))
            Text("创建大厅")
        }
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // 大厅列表
        LobbyListView(networkViewModel)
    }
}

@Composable
fun LobbyListView(networkViewModel: NetworkViewModel) {
    Column {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth()
        ) {
            Text(
                text = "可用大厅",
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.Bold
            )
            
            Spacer(modifier = Modifier.weight(1f))
            
            // 扫描状态指示器
            if (networkViewModel.isScanningLobbies.value) {
                Row(
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(16.dp)
                    )
                    Spacer(modifier = Modifier.width(4.dp))
                    Text(
                        text = "扫描中...",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                    )
                }
            } else {
                Text(
                    text = "找到 ${networkViewModel.lobbyList.value.size} 个大厅",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                )
            }
        }
        
        Spacer(modifier = Modifier.height(8.dp))
        
        when {
            networkViewModel.isScanningLobbies.value -> {
                // 扫描中
                Column(
                    horizontalAlignment = Alignment.CenterHorizontally,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(32.dp)
                ) {
                    CircularProgressIndicator()
                    Spacer(modifier = Modifier.height(8.dp))
                    Text("正在扫描网络中的大厅...")
                }
            }
            
            networkViewModel.lobbyList.value.isEmpty() -> {
                // 没有大厅
                Column(
                    horizontalAlignment = Alignment.CenterHorizontally,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(32.dp)
                ) {
                    Icon(
                        imageVector = Icons.Filled.Search,
                        contentDescription = "No Lobbies",
                        tint = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.3f)
                    )
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        text = "没有找到大厅",
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                    )
                    Spacer(modifier = Modifier.height(4.dp))
                    Text(
                        text = "点击刷新按钮扫描网络",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.4f)
                    )
                }
            }
            
            else -> {
                // 显示大厅列表
                LazyColumn {
                    items(networkViewModel.lobbyList.value) { lobby ->
                        LobbyItem(lobby = lobby, onJoinClick = {
                            networkViewModel.joinLobby(lobby)
                        })
                    }
                }
            }
        }
    }
}

@Composable
fun LobbyItem(lobby: org.ryujinx.android.viewmodels.LobbyInfo, onJoinClick: () -> Unit) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceVariant
        )
    ) {
        Column(
            modifier = Modifier.padding(12.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(
                    modifier = Modifier.weight(1f)
                ) {
                    Text(
                        text = lobby.name,
                        style = MaterialTheme.typography.bodyMedium,
                        fontWeight = FontWeight.Bold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    Spacer(modifier = Modifier.height(2.dp))
                    Text(
                        text = lobby.gameTitle,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f)
                    )
                }
                
                Button(
                    onClick = onJoinClick,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.primary
                    )
                ) {
                    Text("加入")
                }
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Row(
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text(
                    text = "玩家: ${lobby.playerCount}/${lobby.maxPlayers}",
                    style = MaterialTheme.typography.bodySmall
                )
                Text(
                    text = "延迟: ${lobby.ping}ms",
                    style = MaterialTheme.typography.bodySmall
                )
                Text(
                    text = lobby.hostIp,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                )
            }
        }
    }
}

@Composable
fun HostingLobbyView(networkViewModel: NetworkViewModel) {
    val currentLobby = networkViewModel.currentLobby.value
    
    if (currentLobby != null) {
        Column {
            Card(
                modifier = Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(
                    containerColor = MaterialTheme.colorScheme.primaryContainer
                )
            ) {
                Column(
                    modifier = Modifier.padding(16.dp)
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        // 使用表情符号
                        Text(
                            text = "🎮", // 使用游戏手柄表情符号
                            style = MaterialTheme.typography.titleMedium
                        )
                        Spacer(modifier = Modifier.width(8.dp))
                        Text(
                            text = "正在托管大厅",
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.Bold,
                            color = MaterialTheme.colorScheme.onPrimaryContainer
                        )
                    }
                    
                    Spacer(modifier = Modifier.height(12.dp))
                    
                    Row(
                        horizontalArrangement = Arrangement.SpaceBetween,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("大厅名称:")
                        Text(
                            text = currentLobby.name,
                            fontWeight = FontWeight.Bold
                        )
                    }
                    
                    Spacer(modifier = Modifier.height(4.dp))
                    
                    Row(
                        horizontalArrangement = Arrangement.SpaceBetween,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("游戏:")
                        Text(currentLobby.gameTitle)
                    }
                    
                    Spacer(modifier = Modifier.height(4.dp))
                    
                    Row(
                        horizontalArrangement = Arrangement.SpaceBetween,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("玩家:")
                        Text("${currentLobby.playerCount}/${currentLobby.maxPlayers}")
                    }
                    
                    Spacer(modifier = Modifier.height(4.dp))
                    
                    Row(
                        horizontalArrangement = Arrangement.SpaceBetween,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("IP地址:")
                        Text(currentLobby.hostIp)
                    }
                    
                    Spacer(modifier = Modifier.height(12.dp))
                    
                    Button(
                        onClick = { networkViewModel.leaveLobby() },
                        colors = ButtonDefaults.buttonColors(
                            containerColor = MaterialTheme.colorScheme.error
                        ),
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("关闭大厅")
                    }
                }
            }
            
            Spacer(modifier = Modifier.height(16.dp))
            
            // 显示其他可用大厅
            LobbyListView(networkViewModel)
        }
    }
}

@Composable
fun InLobbyView(networkViewModel: NetworkViewModel) {
    val currentLobby = networkViewModel.currentLobby.value
    
    if (currentLobby != null) {
        Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(
                containerColor = MaterialTheme.colorScheme.secondaryContainer
            )
        ) {
            Column(
                modifier = Modifier.padding(16.dp)
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    // 使用表情符号
                    Text(
                        text = "🎯", // 使用靶心表情符号
                        style = MaterialTheme.typography.titleMedium
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    Text(
                        text = "已加入大厅",
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.Bold,
                        color = MaterialTheme.colorScheme.onSecondaryContainer
                    )
                }
                
                Spacer(modifier = Modifier.height(12.dp))
                
                Row(
                    horizontalArrangement = Arrangement.SpaceBetween,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("大厅名称:")
                    Text(
                        text = currentLobby.name,
                        fontWeight = FontWeight.Bold
                    )
                }
                
                Spacer(modifier = Modifier.height(4.dp))
                
                Row(
                    horizontalArrangement = Arrangement.SpaceBetween,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("游戏:")
                    Text(currentLobby.gameTitle)
                }
                
                Spacer(modifier = Modifier.height(4.dp))
                
                Row(
                    horizontalArrangement = Arrangement.SpaceBetween,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("玩家:")
                    Text("${currentLobby.playerCount}/${currentLobby.maxPlayers}")
                }
                
                Spacer(modifier = Modifier.height(4.dp))
                
                Row(
                    horizontalArrangement = Arrangement.SpaceBetween,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("主机:")
                    Text(currentLobby.hostIp)
                }
                
                Spacer(modifier = Modifier.height(12.dp))
                
                Button(
                    onClick = { networkViewModel.leaveLobby() },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.error
                    ),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("离开大厅")
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NetworkInterfaceCard(networkViewModel: NetworkViewModel) {
    var expanded by remember { mutableStateOf(false) }
    
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surface
        )
    ) {
        Column(
            modifier = Modifier.padding(16.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Icon(
                    imageVector = Icons.Filled.Settings,
                    contentDescription = "Network Interface",
                    tint = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "Network Interface",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold
                )
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            Divider()
            Spacer(modifier = Modifier.height(12.dp))
            
            // Network interface selection
            Column {
                Text(
                    text = "Select Network Interface:",
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.padding(bottom = 8.dp)
                )
                
                Box {
                    // Network interface dropdown
                    OutlinedTextField(
                        value = networkViewModel.networkInterfaceList.getOrNull(networkViewModel.networkInterfaceIndex.value)?.name ?: "Default",
                        onValueChange = { },
                        modifier = Modifier
                            .fillMaxWidth()
                            .clickable { expanded = true },
                        readOnly = true,
                        trailingIcon = {
                            Icon(
                                imageVector = Icons.Filled.ArrowDropDown,
                                contentDescription = "Network Interfaces"
                            )
                        }
                    )
                    
                    DropdownMenu(
                        expanded = expanded,
                        onDismissRequest = { expanded = false }
                    ) {
                        networkViewModel.networkInterfaceList.forEachIndexed { index, interfaceInfo ->
                            DropdownMenuItem(
                                text = { 
                                    Column {
                                        Text(interfaceInfo.name)
                                        Text(
                                            text = interfaceInfo.description,
                                            style = MaterialTheme.typography.bodySmall,
                                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                                        )
                                    }
                                },
                                onClick = {
                                    networkViewModel.setNetworkInterfaceIndex(index)
                                    expanded = false
                                }
                            )
                        }
                    }
                }
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            
            // Refresh interfaces button
            Button(
                onClick = { networkViewModel.refreshNetworkInterfaces() },
                modifier = Modifier.fillMaxWidth(),
                colors = ButtonDefaults.buttonColors(
                    containerColor = MaterialTheme.colorScheme.secondaryContainer
                )
            ) {
                Icon(
                    imageVector = Icons.Filled.Refresh,
                    contentDescription = "Refresh Interfaces"
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text("Refresh Network Interfaces")
            }
        }
    }
}

@Composable
fun NetworkInfoCard() {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceVariant
        )
    ) {
        Column(
            modifier = Modifier.padding(16.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Icon(
                    imageVector = Icons.Filled.Info,
                    contentDescription = "Network Information",
                    tint = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "Network Information",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold
                )
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            Divider()
            Spacer(modifier = Modifier.height(12.dp))
            
            Text(
                text = "LDN Local Wireless Network",
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.Bold
            )
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Text(
                text = "LDN (Local Distribution Network) allows you to connect with other Ryujinx devices on the same local network for multiplayer gaming experiences.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.8f)
            )
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Text(
                text = "• Make sure all devices are on the same Wi-Fi network\n" +
                       "• Enable LDN Local Wireless mode\n" +
                       "• Create or join a game lobby\n" +
                       "• Start compatible multiplayer games",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.7f)
            )
        }
    }
}
