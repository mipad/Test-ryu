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
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Divider
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
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
import kotlinx.coroutines.delay
import org.ryujinx.android.RyujinxNative
import org.ryujinx.android.viewmodels.NetworkViewModel
import org.ryujinx.android.viewmodels.NetworkStatus
import org.ryujinx.android.viewmodels.SettingsViewModel
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json

@Composable
fun NetworkView(settingsViewModel: SettingsViewModel) {
    val networkViewModel = NetworkViewModel(settingsViewModel.activity)
    
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
            LobbyManagementCard(networkViewModel)
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
fun LobbyManagementCard(networkViewModel: NetworkViewModel) {
    // 添加创建大厅对话框状态管理
    if (networkViewModel.showCreateLobbyDialog.value) {
        CreateLobbyDialog(
            onDismiss = { networkViewModel.showCreateLobbyDialog.value = false },
            onCreate = { lobbyName, gameTitle, maxPlayers ->
                networkViewModel.createLobby(lobbyName, gameTitle, maxPlayers)
            }
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
    onCreate: (String, String, Int) -> Unit
) {
    var lobbyName by remember { mutableStateOf("") }
    var gameTitle by remember { mutableStateOf("") }
    var maxPlayers by remember { mutableStateOf(4) }

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
                OutlinedTextField(
                    value = gameTitle,
                    onValueChange = { gameTitle = it },
                    label = { Text("游戏标题") },
                    modifier = Modifier.fillMaxWidth(),
                    placeholder = { Text("输入游戏标题") }
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
                    if (lobbyName.isNotBlank() && gameTitle.isNotBlank()) {
                        onCreate(lobbyName, gameTitle, maxPlayers)
                        onDismiss()
                    }
                },
                enabled = lobbyName.isNotBlank() && gameTitle.isNotBlank()
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
}

@Composable
fun IdleLobbyView(networkViewModel: NetworkViewModel) {
    Column {
        // 创建大厅按钮
        Button(
            onClick = { 
                networkViewModel.showCreateLobbyDialog.value = true
            },
            modifier = Modifier.fillMaxWidth()
        ) {
            Text("Create New Lobby")
        }
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // 大厅列表
        Text(
            text = "Available Lobbies:",
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Bold,
            modifier = Modifier.padding(bottom = 8.dp)
        )
        
        if (networkViewModel.isScanningLobbies.value) {
            Column(
                horizontalAlignment = Alignment.CenterHorizontally,
                modifier = Modifier.fillMaxWidth()
            ) {
                CircularProgressIndicator()
                Spacer(modifier = Modifier.height(8.dp))
                Text("Scanning for lobbies...")
            }
        } else if (networkViewModel.lobbyList.value.isEmpty()) {
            Text(
                text = "No lobbies found. Make sure you're on the same network.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
            )
        } else {
            Column {
                networkViewModel.lobbyList.value.forEach { lobby ->
                    LobbyListItem(lobby, networkViewModel)
                    Spacer(modifier = Modifier.height(8.dp))
                }
            }
        }
        
        // 自动刷新大厅列表
        LaunchedEffect(networkViewModel.isScanningLobbies.value) {
            if (!networkViewModel.isScanningLobbies.value) {
                delay(5000) // 5秒后自动刷新
                networkViewModel.refreshLobbyList()
            }
        }
    }
}

@Composable
fun LobbyListItem(lobby: org.ryujinx.android.viewmodels.LobbyInfo, networkViewModel: NetworkViewModel) {
    Card(
        onClick = { networkViewModel.joinLobby(lobby) },
        modifier = Modifier.fillMaxWidth(),
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
                        fontWeight = FontWeight.Bold
                    )
                    Text(
                        text = "${lobby.gameTitle} • Host: ${lobby.hostName}",
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                
                Column(
                    horizontalAlignment = Alignment.End
                ) {
                    Text(
                        text = "${lobby.playerCount}/${lobby.maxPlayers}",
                        style = MaterialTheme.typography.bodyMedium
                    )
                    Text(
                        text = "${lobby.ping}ms",
                        style = MaterialTheme.typography.bodySmall,
                        color = when {
                            lobby.ping < 50 -> MaterialTheme.colorScheme.primary
                            lobby.ping < 100 -> MaterialTheme.colorScheme.secondary
                            else -> MaterialTheme.colorScheme.error
                        }
                    )
                }
            }
            
            if (lobby.isPasswordProtected) {
                Spacer(modifier = Modifier.height(4.dp))
                Text(
                    text = "🔒 Password Protected",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.secondary
                )
            }
        }
    }
}

@Composable
fun HostingLobbyView(networkViewModel: NetworkViewModel) {
    val currentLobby = networkViewModel.currentLobby.value ?: return
    
    Column {
        Text(
            text = "Hosting Lobby: ${currentLobby.name}",
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Bold
        )
        
        Spacer(modifier = Modifier.height(8.dp))
        
        // 玩家列表
        Text("Players in lobby:")
        Column(
            modifier = Modifier.padding(start = 8.dp)
        ) {
            Text("• ${currentLobby.hostName} (Host)")
            // 这里应该显示实际连接的玩家列表
            Text("• Waiting for players...")
        }
        
        Spacer(modifier = Modifier.height(16.dp))
        
        Button(
            onClick = { networkViewModel.leaveLobby() },
            colors = ButtonDefaults.buttonColors(
                containerColor = MaterialTheme.colorScheme.error
            )
        ) {
            Text("Close Lobby")
        }
    }
}

@Composable
fun InLobbyView(networkViewModel: NetworkViewModel) {
    val currentLobby = networkViewModel.currentLobby.value ?: return
    
    Column {
        Text(
            text = "Joined: ${currentLobby.name}",
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Bold
        )
        
        Spacer(modifier = Modifier.height(8.dp))
        
        Text("Players in lobby:")
        Column(
            modifier = Modifier.padding(start = 8.dp)
        ) {
            Text("• ${currentLobby.hostName} (Host)")
            Text("• You")
        }
        
        Spacer(modifier = Modifier.height(16.dp))
        
        Button(
            onClick = { networkViewModel.leaveLobby() }
        ) {
            Text("Leave Lobby")
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NetworkInterfaceCard(networkViewModel: NetworkViewModel) {
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
                    contentDescription = "Network Interface Settings",
                    tint = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "Network Interface Settings",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold
                )
                
                Spacer(modifier = Modifier.weight(1f))
                
                // Refresh button
                IconButton(
                    onClick = { networkViewModel.refreshNetworkInterfaces() }
                ) {
                    Icon(
                        imageVector = Icons.Filled.Refresh,
                        contentDescription = "Refresh Network Interfaces"
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            Divider()
            Spacer(modifier = Modifier.height(12.dp))
            
            Text(
                text = "Select network interface for local wireless communication:",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(bottom = 12.dp)
            )
            
            // Network interface list
            Column {
                networkViewModel.networkInterfaceList.forEachIndexed { index, interfaceInfo ->
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        RadioButton(
                            selected = networkViewModel.networkInterfaceIndex.value == index,
                            onClick = { networkViewModel.setNetworkInterfaceIndex(index) }
                        )
                        Spacer(modifier = Modifier.width(8.dp))
                        Column(
                            modifier = Modifier.weight(1f)
                        ) {
                            Text(
                                text = interfaceInfo.name,
                                style = MaterialTheme.typography.bodyMedium
                            )
                            if (interfaceInfo.description.isNotEmpty()) {
                                Text(
                                    text = interfaceInfo.description,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                                    fontSize = 12.sp
                                )
                            }
                        }
                    }
                    if (index < networkViewModel.networkInterfaceList.size - 1) {
                        Spacer(modifier = Modifier.height(4.dp))
                    }
                }
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Text(
                text = "Currently selected interface: ${networkViewModel.getSelectedInterfaceId()}",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
            )
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
                    text = "Network Features Description",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.primary
                )
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            
            Text(
                text = "• Internet Access: Controls whether the emulator can access external networks\n" +
                       "• LDN Local Wireless: Emulates Switch local wireless multiplayer, requires being on the same network\n" +
                       "• Game Lobby: Create or join multiplayer game sessions over local network\n" +
                       "• Network Interface: Select physical network adapter for local wireless communication\n" +
                       "• Permissions: App requires network permissions to detect connection status and interface information",
                style = MaterialTheme.typography.bodyMedium,
                lineHeight = 20.sp
            )
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Text(
                text = "Note: Some network features may require device to be connected to a network to work properly",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
            )
        }
    }
}
