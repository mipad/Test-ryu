package org.ryujinx.android.views

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import org.ryujinx.android.viewmodels.NetworkViewModel
import org.ryujinx.android.viewmodels.NetworkStatus
import org.ryujinx.android.viewmodels.SettingsViewModel

@Composable
fun NetworkView(settingsViewModel: SettingsViewModel) {
    val networkViewModel = NetworkViewModel(settingsViewModel.activity)
    
    // Start discovery when view is created, stop when destroyed
    LaunchedEffect(Unit) {
        networkViewModel.startDiscovery()
    }
    
    DisposableEffect(Unit) {
        onDispose {
            networkViewModel.stopDiscovery()
        }
    }
    
    Column(
        modifier = Modifier
            .verticalScroll(rememberScrollState())
            .padding(16.dp)
    ) {
        // Network status overview card
        NetworkStatusCard(networkViewModel)
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // Device discovery card
        DeviceDiscoveryCard(networkViewModel)
        
        Spacer(modifier = Modifier.height(16.dp))
        
        // Multiplayer settings
        MultiplayerSettingsCard(networkViewModel)
        
        Spacer(modifier = Modifier.height(16.dp))
        
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
                Text(
                    text = "🌐",
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
        }
    }
}

@Composable
fun DeviceDiscoveryCard(networkViewModel: NetworkViewModel) {
    val discoveredDevices by networkViewModel.discoveredDevices
    val isDiscovering by networkViewModel.isDiscovering
    
    // Auto-refresh the device list periodically
    LaunchedEffect(isDiscovering) {
        while (isDiscovering) {
            networkViewModel.refreshDiscoveredDevices()
            delay(2000) // Refresh every 2 seconds
        }
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
                Icon(
                    imageVector = Icons.Filled.Devices,
                    contentDescription = "Device Discovery",
                    tint = MaterialTheme.colorScheme.primary
                )
                Spacer(modifier = Modifier.width(8.dp))
                Text(
                    text = "Device Discovery",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold
                )
                
                Spacer(modifier = Modifier.weight(1f))
                
                // Refresh button
                IconButton(
                    onClick = { 
                        networkViewModel.refreshDiscoveredDevices()
                        // Also trigger a new broadcast
                        networkViewModel.startDiscovery()
                    },
                    enabled = isDiscovering
                ) {
                    Icon(
                        imageVector = Icons.Filled.Refresh,
                        contentDescription = "Refresh Devices"
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            Divider()
            Spacer(modifier = Modifier.height(12.dp))
            
            // Discovery status
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text(
                    text = "Discovery Status:",
                    style = MaterialTheme.typography.bodyMedium
                )
                Spacer(modifier = Modifier.width(8.dp))
                if (isDiscovering) {
                    Text(
                        text = "Active",
                        color = MaterialTheme.colorScheme.primary
                    )
                } else {
                    Text(
                        text = "Inactive",
                        color = MaterialTheme.colorScheme.error
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(12.dp))
            
            // Discovered devices list
            if (discoveredDevices.isEmpty()) {
                Text(
                    text = "No other Ryujinx devices found on the network",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),
                    modifier = Modifier.fillMaxWidth()
                )
            } else {
                Text(
                    text = "Found ${discoveredDevices.size} device(s):",
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.padding(bottom = 8.dp)
                )
                
                Column {
                    discoveredDevices.forEach { device ->
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 4.dp)
                        ) {
                            Icon(
                                imageVector = Icons.Filled.Computer,
                                contentDescription = "Device",
                                tint = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.size(20.dp)
                            )
                            Spacer(modifier = Modifier.width(12.dp))
                            Column(
                                modifier = Modifier.weight(1f)
                            ) {
                                Text(
                                    text = device.name,
                                    style = MaterialTheme.typography.bodyMedium,
                                    fontWeight = FontWeight.Medium
                                )
                                Text(
                                    text = "IP: ${device.ip}",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                                )
                            }
                            // Online status indicator
                            Box(
                                modifier = Modifier
                                    .size(8.dp)
                                    .background(
                                        color = MaterialTheme.colorScheme.primary,
                                        shape = MaterialTheme.shapes.small
                                    )
                            )
                        }
                        if (device != discoveredDevices.last()) {
                            Divider(modifier = Modifier.padding(vertical = 4.dp))
                        }
                    }
                }
            }
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Text(
                text = "Note: Only devices running Ryujinx Android on the same network will be discovered",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
            )
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
                       "• Device Discovery: Automatically finds other Ryujinx devices on the same WiFi network\n" +
                       "• Network Interface: Select physical network adapter for local wireless communication\n" +
                       "• Permissions: App requires network permissions to detect connection status and interface information",
                style = MaterialTheme.typography.bodyMedium,
                lineHeight = 20.sp
            )
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Text(
                text = "Note: Device discovery only works when all devices are connected to the same WiFi network",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
            )
        }
    }
}
