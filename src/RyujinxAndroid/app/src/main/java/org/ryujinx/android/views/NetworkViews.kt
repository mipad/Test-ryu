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
// 移除不正确的图标导入
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Divider
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.ryujinx.android.viewmodels.NetworkViewModel
import org.ryujinx.android.viewmodels.SettingsViewModel
import org.ryujinx.android.viewmodels.NetworkViewModel.NetworkStatus

@Composable
fun NetworkView(settingsViewModel: SettingsViewModel) {
    val networkViewModel = remember { NetworkViewModel(settingsViewModel.activity) }
    
    Column(
        modifier = Modifier
            .verticalScroll(rememberScrollState())
            .padding(16.dp)
    ) {
        // 网络状态概览卡片
        NetworkStatusCard(networkViewModel)
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // 多人游戏设置
        MultiplayerSettingsCard(networkViewModel)
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // 网络接口设置
        NetworkInterfaceCard(networkViewModel)
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // 网络信息说明
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
    
    // 使用可用的图标
    val statusIcon = Icons.Filled.Info // 使用Info图标作为替代
    
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
                Icon(
                    imageVector = statusIcon,
                    contentDescription = "网络状态",
                    tint = statusColor
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "网络状态",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold,
                    color = statusColor
                )
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            
            // 网络连接状态
            Row(
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("网络连接:")
                Text(
                    text = networkViewModel.getNetworkStatusText(),
                    color = statusColor
                )
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            // 互联网访问设置状态
            Row(
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("互联网访问:")
                Text(
                    text = if (networkViewModel.enableInternetAccess) "已启用" else "已禁用",
                    color = if (networkViewModel.enableInternetAccess) MaterialTheme.colorScheme.primary 
                           else MaterialTheme.colorScheme.error
                )
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Row(
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("多人游戏模式:")
                Text(
                    text = networkViewModel.getMultiplayerModeName(networkViewModel.multiplayerModeIndex),
                    color = MaterialTheme.colorScheme.secondary
                )
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Row(
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("网络接口:")
                Text(
                    text = networkViewModel.networkInterfaceList.getOrNull(networkViewModel.networkInterfaceIndex)?.name ?: "默认",
                    color = MaterialTheme.colorScheme.secondary
                )
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
                    contentDescription = "多人游戏设置",
                    tint = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "多人游戏设置",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold
                )
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            Divider()
            Spacer(modifier = Modifier.height(12.dp))
            
            // 启用互联网访问开关
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
                modifier = Modifier.fillMaxWidth()
            ) {
                Column {
                    Text(
                        text = "启用互联网访问",
                        style = MaterialTheme.typography.bodyMedium
                    )
                    Text(
                        text = "允许模拟器访问互联网服务",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                    )
                }
                Switch(
                    checked = networkViewModel.enableInternetAccess,
                    onCheckedChange = { networkViewModel.enableInternetAccess = it }
                )
            }
            
            Spacer(modifier = Modifier.height(16.dp))
            
            // 多人游戏模式选择
            Text(
                text = "多人游戏模式:",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(bottom = 8.dp)
            )
            
            // 禁用模式
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                RadioButton(
                    selected = networkViewModel.multiplayerModeIndex == 0,
                    onClick = { networkViewModel.multiplayerModeIndex = 0 }
                )
                Spacer(modifier = Modifier.width(8.dp))
                Column {
                    Text(
                        text = "禁用",
                        style = MaterialTheme.typography.bodyMedium
                    )
                    Text(
                        text = "完全禁用多人游戏功能",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            // LDN 本地无线模式
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                RadioButton(
                    selected = networkViewModel.multiplayerModeIndex == 1,
                    onClick = { networkViewModel.multiplayerModeIndex = 1 }
                )
                Spacer(modifier = Modifier.width(8.dp))
                Column {
                    Text(
                        text = "LDN 本地无线",
                        style = MaterialTheme.typography.bodyMedium
                    )
                    Text(
                        text = "通过本地网络与其他Ryujinx设备联机",
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
                    contentDescription = "网络接口设置",
                    tint = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "网络接口设置",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold
                )
                
                Spacer(modifier = Modifier.weight(1f))
                
                // 刷新按钮
                IconButton(
                    onClick = { networkViewModel.refreshNetworkInterfaces() }
                ) {
                    Icon(
                        imageVector = Icons.Filled.Refresh,
                        contentDescription = "刷新网络接口"
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            Divider()
            Spacer(modifier = Modifier.height(12.dp))
            
            Text(
                text = "选择用于本地无线通信的网络接口:",
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(bottom = 12.dp)
            )
            
            // 网络接口列表
            Column {
                networkViewModel.networkInterfaceList.forEachIndexed { index, interfaceInfo ->
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        RadioButton(
                            selected = networkViewModel.networkInterfaceIndex == index,
                            onClick = { networkViewModel.networkInterfaceIndex = index }
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
                text = "当前选中接口: ${networkViewModel.getSelectedInterfaceId()}",
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
                    contentDescription = "网络信息",
                    tint = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "网络功能说明",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.primary
                )
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            
            Text(
                text = "• 互联网访问: 控制模拟器是否能够访问外部网络\n" +
                       "• LDN 本地无线: 模拟 Switch 本地无线联机功能，需要同一局域网\n" +
                       "• 网络接口: 选择用于本地无线通信的物理网络适配器\n" +
                       "• 权限说明: 应用需要网络权限来检测连接状态和接口信息",
                style = MaterialTheme.typography.bodyMedium,
                lineHeight = 20.sp
            )
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Text(
                text = "注意: 某些网络功能可能需要设备连接到网络才能正常工作",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
            )
        }
    }
}
