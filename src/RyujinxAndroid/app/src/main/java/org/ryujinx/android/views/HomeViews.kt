// HomeViews.kt
package org.ryujinx.android.views 

import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.content.res.Resources
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.basicMarquee
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AlertDialogDefaults
import androidx.compose.material3.BasicAlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SearchBar
import androidx.compose.material3.SearchBarDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
import androidx.navigation.NavHostController
import com.anggrayudi.storage.extension.launchOnUiThread
import kotlinx.coroutines.launch
import org.ryujinx.android.R
import org.ryujinx.android.viewmodels.FileType
import org.ryujinx.android.viewmodels.GameModel
import org.ryujinx.android.viewmodels.HomeViewModel
import org.ryujinx.android.viewmodels.ModModel
import org.ryujinx.android.viewmodels.ModType
import org.ryujinx.android.viewmodels.ModViewModel
import org.ryujinx.android.viewmodels.QuickSettings
import java.io.File
import java.util.Base64
import java.util.Locale
import kotlin.concurrent.thread
import kotlin.math.roundToInt

class HomeViews {
    companion object {
        const val ListImageSize = 150
        const val GridImageSize = 300

        @Composable
        fun NotAvailableIcon(modifier: Modifier = Modifier) {
            Icon(
                Icons.Filled.Add,
                contentDescription = "N/A",
                modifier = modifier
            )
        }

        @Composable
        fun NROIcon(modifier: Modifier = Modifier) {
            Image(
                painter = painterResource(id = R.drawable.icon_nro),
                contentDescription = "NRO",
                modifier = modifier
            )
        }

        @OptIn(ExperimentalFoundationApi::class)
        @Composable
        fun ListGameItem(
            gameModel: GameModel,
            viewModel: HomeViewModel,
            showAppActions: MutableState<Boolean>,
            showLoading: MutableState<Boolean>,
            selectedModel: MutableState<GameModel?>,
            showError: MutableState<String>
        ) {
            remember {
                selectedModel
            }
            val isSelected = selectedModel.value == gameModel

            val decoder = Base64.getDecoder()
            Surface(
                shape = MaterialTheme.shapes.medium,
                color = MaterialTheme.colorScheme.surface,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(8.dp)
                    .combinedClickable(
                        onClick = {
                            if (viewModel.mainViewModel?.selected != null) {
                                showAppActions.value = false
                                viewModel.mainViewModel?.apply {
                                    selected = null
                                }
                                selectedModel.value = null
                            } else if (gameModel.titleId.isNullOrEmpty() || gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro) {
                                thread {
                                    showLoading.value = true
                                    val success =
                                        viewModel.mainViewModel?.loadGame(gameModel) ?: 0
                                    if (success == 1) {
                                        launchOnUiThread {
                                            viewModel.mainViewModel?.navigateToGame()
                                        }
                                    } else {
                                        if (success == -2)
                                            showError.value =
                                                "Error loading update. Please re-add update file"
                                        gameModel.close()
                                    }
                                    showLoading.value = false
                                }
                            }
                        },
                        onLongClick = {
                            viewModel.mainViewModel?.selected = gameModel
                            showAppActions.value = true
                            selectedModel.value = gameModel
                        }
                    )
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(8.dp),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Row {
                        if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                            Box(
                                modifier = if (isSelected) {
                                    Modifier
                                        .padding(end = 8.dp)
                                        .border(2.dp, MaterialTheme.colorScheme.primary, CircleShape)
                                } else {
                                    Modifier.padding(end = 8.dp)
                                }
                            ) {
                                if (gameModel.icon?.isNotEmpty() == true) {
                                    val pic = decoder.decode(gameModel.icon)
                                    val size =
                                        ListImageSize / Resources.getSystem().displayMetrics.density
                                    Image(
                                        bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                            .asImageBitmap(),
                                        contentDescription = gameModel.getDisplayName() + " icon",
                                        modifier = Modifier
                                            .width(size.roundToInt().dp)
                                            .height(size.roundToInt().dp)
                                    )
                                } else if (gameModel.type == FileType.Nro)
                                    NROIcon()
                                else NotAvailableIcon()
                            }
                        } else NotAvailableIcon()
                        Column {
                            Text(text = gameModel.getDisplayName())
                            Text(text = gameModel.developer ?: "")
                            Text(text = gameModel.titleId ?: "")
                        }
                    }
                    Column {
                        Text(text = gameModel.version ?: "")
                        Text(text = String.format("%.3f", gameModel.fileSize))
                    }
                }
            }
        }

        @OptIn(ExperimentalFoundationApi::class)
        @Composable
        fun GridGameItem(
            gameModel: GameModel,
            viewModel: HomeViewModel,
            showAppActions: MutableState<Boolean>,
            showLoading: MutableState<Boolean>,
            selectedModel: MutableState<GameModel?>,
            showError: MutableState<String>
        ) {
            remember {
                selectedModel
            }
            val isSelected = selectedModel.value == gameModel

            val decoder = Base64.getDecoder()
            Surface(
                shape = MaterialTheme.shapes.medium,
                color = MaterialTheme.colorScheme.surface,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(4.dp)
                    .combinedClickable(
                        onClick = {
                            if (viewModel.mainViewModel?.selected != null) {
                                showAppActions.value = false
                                viewModel.mainViewModel?.apply {
                                    selected = null
                                }
                                selectedModel.value = null
                            } else if (gameModel.titleId.isNullOrEmpty() || gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro) {
                                thread {
                                    showLoading.value = true
                                    val success =
                                        viewModel.mainViewModel?.loadGame(gameModel) ?: 0
                                    if (success == 1) {
                                        launchOnUiThread {
                                            viewModel.mainViewModel?.navigateToGame()
                                        }
                                    } else {
                                        if (success == -2)
                                            showError.value =
                                                "Error loading update. Please re-add update file"
                                        gameModel.close()
                                    }
                                    showLoading.value = false
                                }
                            }
                        },
                        onLongClick = {
                            viewModel.mainViewModel?.selected = gameModel
                            showAppActions.value = true
                            selectedModel.value = gameModel
                        }
                    )
            ) {
                Column(
                    modifier = Modifier
                        .padding(4.dp)
                        .fillMaxWidth(),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                        Box(
                            modifier = if (isSelected) {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                                    .border(2.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(16.dp))
                            } else {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                            }
                        ) {
                            if (gameModel.icon?.isNotEmpty() == true) {
                                val pic = decoder.decode(gameModel.icon)
                                Box(
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(16.dp))
                                ) {
                                    Image(
                                        bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                            .asImageBitmap(),
                                        contentDescription = gameModel.getDisplayName() + " icon",
                                        contentScale = ContentScale.Crop,
                                        modifier = Modifier.fillMaxSize()
                                    )
                                }
                            } else if (gameModel.type == FileType.Nro) {
                                Box(
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(16.dp))
                                ) {
                                    NROIcon(
                                        modifier = Modifier
                                            .fillMaxSize(0.8f)
                                            .align(Alignment.Center)
                                    )
                                }
                            } else {
                                Box(
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(16.dp))
                                ) {
                                    NotAvailableIcon(
                                        modifier = Modifier
                                        .fillMaxSize(0.8f)
                                        .align(Alignment.Center)
                                    )
                                }
                            }
                        }
                    } else {
                        Box(
                            modifier = if (isSelected) {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                                    .border(2.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(16.dp))
                            } else {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                            }
                        ) {
                            NotAvailableIcon(
                                modifier = Modifier
                                    .fillMaxSize(0.8f)
                                    .align(Alignment.Center)
                            )
                        }
                    }
                    Text(
                        text = gameModel.getDisplayName(),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier
                            .padding(vertical = 4.dp)
                            .basicMarquee()
                    )
                }
            }
        }

        @OptIn(ExperimentalFoundationApi::class)
        @Composable
        fun LandscapeGameCarouselItem(
            gameModel: GameModel?,
            viewModel: HomeViewModel,
            showAppActions: MutableState<Boolean>,
            showLoading: MutableState<Boolean>,
            selectedModel: MutableState<GameModel?>,
            showError: MutableState<String>,
            isCentered: Boolean = false,
            onItemClick: () -> Unit = {}
        ) {
            if (gameModel == null) {
                // 空项目
                Box(
                    modifier = Modifier
                        .size(100.dp)
                        .clip(RoundedCornerShape(16.dp))
                ) {
                    // 空项目不显示任何内容
                }
                return
            }

            remember {
                selectedModel
            }
            val isSelected = selectedModel.value == gameModel
            val decoder = Base64.getDecoder()
            
            // 根据主题确定边框颜色 - 使用背景色的亮度来判断
            val backgroundColor = MaterialTheme.colorScheme.background
            val luminance = 0.299 * backgroundColor.red + 0.587 * backgroundColor.green + 0.114 * backgroundColor.blue
            val borderColor = if (luminance > 0.5) Color.Black else Color.White

            if (isCentered) {
                // 中央项目 - 只显示图标，不显示文字
                // 修复：将combinedClickable移到外层，确保边框不影响点击区域
                Box(
                    modifier = Modifier
                        .fillMaxWidth(0.6f)
                        .aspectRatio(1.3f)
                        .offset(y = (-23).dp) // 向上移动10dp
                        .combinedClickable(
                            onClick = {
                                if (viewModel.mainViewModel?.selected != null) {
                                    showAppActions.value = false
                                    viewModel.mainViewModel?.apply {
                                        selected = null
                                    }
                                    selectedModel.value = null
                                } else if (gameModel.titleId.isNullOrEmpty() || gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro) {
                                    thread {
                                        showLoading.value = true
                                        val success =
                                            viewModel.mainViewModel?.loadGame(gameModel) ?: 0
                                        if (success == 1) {
                                            launchOnUiThread {
                                                viewModel.mainViewModel?.navigateToGame()
                                            }
                                        } else {
                                            if (success == -2)
                                                showError.value =
                                                    "Error loading update. Please re-add update file"
                                            gameModel.close()
                                        }
                                        showLoading.value = false
                                    }
                                }
                            },
                            onLongClick = {
                                viewModel.mainViewModel?.selected = gameModel
                                showAppActions.value = true
                                selectedModel.value = gameModel
                            }
                        )
                ) {
                    Box(
                        modifier = Modifier
                            .fillMaxSize()
                            .border(1.dp, borderColor, RoundedCornerShape(12.dp)) // 添加超细线框
                            .then(
                                if (isSelected) {
                                    Modifier.border(
                                        2.dp,
                                        MaterialTheme.colorScheme.primary,
                                        RoundedCornerShape(12.dp)
                                    )
                                } else {
                                    Modifier
                                }
                            )
                    ) {
                        if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                            if (gameModel.icon?.isNotEmpty() == true) {
                                val pic = decoder.decode(gameModel.icon)
                                Image(
                                    bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                        .asImageBitmap(),
                                    contentDescription = gameModel.getDisplayName() + " icon",
                                    contentScale = ContentScale.FillBounds, // 改为FillBounds以拉伸图片
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(12.dp))
                                )
                            } else if (gameModel.type == FileType.Nro) {
                                NROIcon(
                                    modifier = Modifier
                                        .fillMaxSize(0.8f)
                                        .align(Alignment.Center)
                                )
                            } else {
                                NotAvailableIcon(
                                    modifier = Modifier
                                        .fillMaxSize(0.8f)
                                        .align(Alignment.Center)
                                )
                            }
                        } else {
                            NotAvailableIcon(
                                modifier = Modifier
                                    .fillMaxSize(0.8f)
                                    .align(Alignment.Center)
                            )
                        }
                    }
                }
            } else {
                // 两侧项目 - 只显示图标
                Box(
                    modifier = Modifier
                        .size(80.dp)
                        .clip(RoundedCornerShape(12.dp))
                        .clickable(onClick = onItemClick)
                ) {
                    if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                        if (gameModel.icon?.isNotEmpty() == true) {
                            val pic = decoder.decode(gameModel.icon)
                            Image(
                                bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                    .asImageBitmap(),
                                contentDescription = gameModel.getDisplayName() + " icon",
                                contentScale = ContentScale.Crop,
                                modifier = Modifier.fillMaxSize()
                            )
                        } else if (gameModel.type == FileType.Nro) {
                            NROIcon(
                                modifier = Modifier
                                    .fillMaxSize(0.8f)
                                    .align(Alignment.Center)
                            )
                        } else {
                            NotAvailableIcon(
                                modifier = Modifier
                                    .fillMaxSize(0.8f)
                                    .align(Alignment.Center)
                            )
                        }
                    } else {
                        NotAvailableIcon(
                            modifier = Modifier
                                .fillMaxSize(0.8f)
                                .align(Alignment.Center)
                        )
                    }
                }
            }
        }

        @Composable
        fun ModManagementDialog(
            viewModel: HomeViewModel,
            selectedModel: GameModel?,
            onDismiss: () -> Unit
        ) {
            val modViewModel = remember { ModViewModel() }
            val context = LocalContext.current
            val configuration = LocalConfiguration.current
            val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
            val scope = rememberCoroutineScope()
            
            // 状态变量
            var showDeleteAllDialog by remember { mutableStateOf(false) }
            var showDeleteDialog by remember { mutableStateOf<ModModel?>(null) }
            var showAddModDialog by remember { mutableStateOf(false) }
            var selectedModPath by remember { mutableStateOf("") }
            
            // 使用OpenDocumentTree来选择文件夹
            val folderPickerLauncher = rememberLauncherForActivityResult(
                ActivityResultContracts.OpenDocumentTree()
            ) { uri ->
                uri?.let {
                    val folderPath = getFolderPathFromUri(context, it)
                    if (!folderPath.isNullOrEmpty()) {
                        selectedModPath = folderPath
                        showAddModDialog = true
                    }
                }
            }

            // 加载Mod列表
            LaunchedEffect(selectedModel?.titleId) {
                selectedModel?.titleId?.let { titleId ->
                    modViewModel.resetLoadedState()
                    modViewModel.loadMods(titleId)
                }
            }

            // 显示错误消息
            modViewModel.errorMessage?.let { error ->
                LaunchedEffect(error) {
                    modViewModel.clearError()
                }
            }

            AlertDialog(
                onDismissRequest = onDismiss,
                title = { 
                    Text(
                        text = "Mod Management - ${selectedModel?.getDisplayName() ?: ""}",
                        style = MaterialTheme.typography.titleLarge
                    )
                },
                text = {
                    // 根据屏幕方向选择布局
                    if (isLandscape) {
                        // 横屏：左右布局
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(400.dp) // 固定高度，允许滚动
                        ) {
                            // 左侧：Mod列表（可滚动）
                            Column(
                                modifier = Modifier
                                    .weight(1f)
                                    .fillMaxHeight()
                                    .verticalScroll(rememberScrollState())
                            ) {
                                // 统计信息和删除所有按钮
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(
                                        text = "Mods: ${modViewModel.mods.size} (${modViewModel.mods.count { it.enabled }} enabled)",
                                        style = MaterialTheme.typography.bodyMedium
                                    )
                                    
                                    TextButton(
                                        onClick = { showDeleteAllDialog = true },
                                        enabled = modViewModel.mods.isNotEmpty()
                                    ) {
                                        Text("Delete All")
                                    }
                                }
                                
                                Spacer(modifier = Modifier.height(12.dp))
                                
                                // Mod列表
                                if (modViewModel.mods.isEmpty()) {
                                    Column(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .height(200.dp),
                                        horizontalAlignment = Alignment.CenterHorizontally,
                                        verticalArrangement = Arrangement.Center
                                    ) {
                                        Text(
                                            text = "📁",
                                            style = MaterialTheme.typography.displayMedium
                                        )
                                        Spacer(modifier = Modifier.height(8.dp))
                                        Text(
                                            text = "No mods found",
                                            style = MaterialTheme.typography.bodyLarge,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                        Text(
                                            text = "Click + button to add a mod",
                                            style = MaterialTheme.typography.bodyMedium,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                    }
                                } else {
                                    Column(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .verticalScroll(rememberScrollState())
                                    ) {
                                        modViewModel.mods.forEach { mod ->
                                            ModListItem(
                                                mod = mod,
                                                onEnabledChanged = { enabled ->
                                                    scope.launch {
                                                        selectedModel?.titleId?.let { titleId ->
                                                            modViewModel.setModEnabled(titleId, mod, enabled)
                                                        }
                                                    }
                                                },
                                                onDelete = {
                                                    showDeleteDialog = mod
                                                }
                                            )
                                        }
                                    }
                                }
                            }
                            
                            // 右侧：操作按钮和信息
                            Column(
                                modifier = Modifier
                                    .weight(0.4f)
                                    .fillMaxHeight()
                                    .padding(start = 16.dp),
                                verticalArrangement = Arrangement.spacedBy(12.dp)
                            ) {
                                Text(
                                    text = "Actions",
                                    style = MaterialTheme.typography.titleMedium,
                                    fontWeight = FontWeight.Bold
                                )
                                
                                Button(
                                    onClick = {
                                        selectedModel?.titleId?.let { titleId ->
                                            folderPickerLauncher.launch(null)
                                        }
                                    },
                                    modifier = Modifier.fillMaxWidth()
                                ) {
                                    Icon(Icons.Filled.Add, contentDescription = "Add Mod", modifier = Modifier.size(16.dp))
                                    Spacer(modifier = Modifier.width(6.dp))
                                    Text("Add Mod")
                                }
                                
                                OutlinedButton(
                                    onClick = {
                                        selectedModel?.titleId?.let { titleId ->
                                            scope.launch {
                                                modViewModel.resetLoadedState()
                                                modViewModel.loadMods(titleId)
                                            }
                                        }
                                    },
                                    modifier = Modifier.fillMaxWidth()
                                ) {
                                    Icon(Icons.Filled.Refresh, contentDescription = "Refresh", modifier = Modifier.size(16.dp))
                                    Spacer(modifier = Modifier.width(6.dp))
                                    Text("Refresh List")
                                }
                                
                                Spacer(modifier = Modifier.height(16.dp))
                                
                                Text(
                                    text = "Info",
                                    style = MaterialTheme.typography.titleMedium,
                                    fontWeight = FontWeight.Bold
                                )
                                
                                Text(
                                    text = "• Mods are stored in game's mod directory",
                                    style = MaterialTheme.typography.bodySmall
                                )
                                Text(
                                    text = "• Enable/disable mods using the switch",
                                    style = MaterialTheme.typography.bodySmall
                                )
                                Text(
                                    text = "• Supported types: RomFs, ExeFs",
                                    style = MaterialTheme.typography.bodySmall
                                )
                            }
                        }
                    } else {
                        // 竖屏：上下布局
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(500.dp) // 固定高度，允许滚动
                                .verticalScroll(rememberScrollState())
                        ) {
                            // 操作按钮区域
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(
                                    text = "Mods: ${modViewModel.mods.size} (${modViewModel.mods.count { it.enabled }} enabled)",
                                    style = MaterialTheme.typography.bodyMedium
                                )
                                
                                Row {
                                    TextButton(
                                        onClick = { showDeleteAllDialog = true },
                                        enabled = modViewModel.mods.isNotEmpty()
                                    ) {
                                        Text("Delete All")
                                    }
                                    
                                    Button(
                                        onClick = {
                                            selectedModel?.titleId?.let { titleId ->
                                                folderPickerLauncher.launch(null)
                                            }
                                        }
                                    ) {
                                        Icon(Icons.Filled.Add, contentDescription = "Add Mod", modifier = Modifier.size(16.dp))
                                        Spacer(modifier = Modifier.width(6.dp))
                                        Text("Add")
                                    }
                                }
                            }
                            
                            Spacer(modifier = Modifier.height(12.dp))
                            
                            // Mod列表
                            if (modViewModel.mods.isEmpty()) {
                                Column(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .height(200.dp),
                                    horizontalAlignment = Alignment.CenterHorizontally,
                                    verticalArrangement = Arrangement.Center
                                ) {
                                    Text(
                                        text = "📁",
                                        style = MaterialTheme.typography.displayMedium
                                    )
                                    Spacer(modifier = Modifier.height(8.dp))
                                    Text(
                                        text = "No mods found",
                                        style = MaterialTheme.typography.bodyLarge,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                    Text(
                                        text = "Click + button to add a mod",
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            } else {
                                Column {
                                    modViewModel.mods.forEach { mod ->
                                        ModListItem(
                                            mod = mod,
                                            onEnabledChanged = { enabled ->
                                                scope.launch {
                                                    selectedModel?.titleId?.let { titleId ->
                                                        modViewModel.setModEnabled(titleId, mod, enabled)
                                                    }
                                                }
                                            },
                                            onDelete = {
                                                showDeleteDialog = mod
                                            }
                                        )
                                    }
                                }
                            }
                            
                            // 刷新按钮
                            OutlinedButton(
                                onClick = {
                                    selectedModel?.titleId?.let { titleId ->
                                        scope.launch {
                                            modViewModel.resetLoadedState()
                                            modViewModel.loadMods(titleId)
                                        }
                                    }
                                },
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(top = 16.dp)
                            ) {
                                Icon(Icons.Filled.Refresh, contentDescription = "Refresh", modifier = Modifier.size(16.dp))
                                Spacer(modifier = Modifier.width(6.dp))
                                Text("Refresh List")
                            }
                        }
                    }
                },
                confirmButton = {
                    TextButton(onClick = onDismiss) {
                        Text("Close")
                    }
                }
            )

            // 删除单个Mod对话框
            showDeleteDialog?.let { mod ->
                AlertDialog(
                    onDismissRequest = { showDeleteDialog = null },
                    title = { Text("Delete Mod") },
                    text = { 
                        Text("Are you sure you want to delete \"${mod.name}\"? This action cannot be undone.") 
                    },
                    confirmButton = {
                        Button(
                            onClick = {
                                scope.launch {
                                    selectedModel?.titleId?.let { titleId ->
                                        modViewModel.deleteMod(titleId, mod)
                                        showDeleteDialog = null
                                    }
                                }
                            }
                        ) {
                            Text("Delete")
                        }
                    },
                    dismissButton = {
                        TextButton(
                            onClick = { showDeleteDialog = null }
                        ) {
                            Text("Cancel")
                        }
                    }
                )
            }

            // 删除所有Mod对话框
            if (showDeleteAllDialog) {
                AlertDialog(
                    onDismissRequest = { showDeleteAllDialog = false },
                    title = { Text("Delete All Mods") },
                    text = { 
                        Text("Are you sure you want to delete all ${modViewModel.mods.size} mods? This action cannot be undone.") 
                    },
                    confirmButton = {
                        Button(
                            onClick = {
                                scope.launch {
                                    selectedModel?.titleId?.let { titleId ->
                                        modViewModel.deleteAllMods(titleId)
                                        showDeleteAllDialog = false
                                    }
                                }
                            }
                        ) {
                            Text("Delete All")
                        }
                    },
                    dismissButton = {
                        TextButton(
                            onClick = { showDeleteAllDialog = false }
                        ) {
                            Text("Cancel")
                        }
                    }
                )
            }

            // 添加Mod对话框
            if (showAddModDialog) {
                AddModDialog(
                    selectedPath = selectedModPath,
                    onConfirm = { modName ->
                        scope.launch {
                            selectedModel?.titleId?.let { titleId ->
                                val sourceFile = File(selectedModPath)
                                if (!sourceFile.exists() || !sourceFile.isDirectory) {
                                    return@launch
                                }
                                
                                modViewModel.addMod(titleId, selectedModPath, modName)
                                showAddModDialog = false
                                selectedModPath = ""
                            }
                        }
                    },
                    onDismiss = {
                        showAddModDialog = false
                        selectedModPath = ""
                    }
                )
            }
        }

        @Composable
        private fun ModListItem(
            mod: ModModel,
            onEnabledChanged: (Boolean) -> Unit,
            onDelete: () -> Unit
        ) {
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 4.dp),
                shape = RoundedCornerShape(8.dp)
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    // 启用开关
                    Switch(
                        checked = mod.enabled,
                        onCheckedChange = onEnabledChanged
                    )
                    
                    Spacer(modifier = Modifier.width(12.dp))
                    
                    // Mod信息
                    Column(
                        modifier = Modifier.weight(1f)
                    ) {
                        Text(
                            text = mod.name,
                            style = MaterialTheme.typography.bodyLarge,
                            fontWeight = FontWeight.Medium
                        )
                        
                        Text(
                            text = "Type: ${mod.type.name} • ${if (mod.inExternalStorage) "External" else "Internal"}",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        
                        Text(
                            text = mod.path,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    
                    Spacer(modifier = Modifier.width(12.dp))
                    
                    // 删除按钮
                    IconButton(onClick = onDelete) {
                        Icon(Icons.Filled.Delete, contentDescription = "Delete")
                    }
                }
            }
        }

        @Composable
        private fun AddModDialog(
            selectedPath: String,
            onConfirm: (String) -> Unit,
            onDismiss: () -> Unit
        ) {
            var modName by remember { mutableStateOf("") }
            val folderName = File(selectedPath).name
            
            if (modName.isEmpty()) {
                modName = folderName
            }
            
            AlertDialog(
                onDismissRequest = onDismiss,
                title = { Text("Add Mod") },
                text = {
                    Column {
                        Text("Selected folder: $selectedPath")
                        Spacer(modifier = Modifier.height(12.dp))
                        Text("Mod name:")
                        OutlinedTextField(
                            value = modName,
                            onValueChange = { modName = it },
                            modifier = Modifier.fillMaxWidth(),
                            placeholder = { Text("Enter mod name") }
                        )
                    }
                },
                confirmButton = {
                    Button(
                        onClick = { onConfirm(modName) },
                        enabled = modName.isNotEmpty()
                    ) {
                        Text("Add Mod")
                    }
                },
                dismissButton = {
                    TextButton(onClick = onDismiss) {
                        Text("Cancel")
                    }
                }
            )
        }

        private fun getFolderPathFromUri(context: Context, uri: Uri): String? {
            return try {
                val contentResolver = context.contentResolver
                val takeFlags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                contentResolver.takePersistableUriPermission(uri, takeFlags)
                
                if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.LOLLIPOP) {
                    val documentId = android.provider.DocumentsContract.getTreeDocumentId(uri)
                    if (documentId.startsWith("primary:")) {
                        val path = documentId.substringAfter("primary:")
                        "/storage/emulated/0/$path"
                    } else {
                        uri.path
                    }
                } else {
                    uri.path
                }
            } catch (e: Exception) {
                e.printStackTrace()
                null
            }
        }

        @OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
        @Composable
        fun Home(
            viewModel: HomeViewModel = HomeViewModel(),
            navController: NavHostController? = null,
            isPreview: Boolean = false
        ) {
            viewModel.ensureReloadIfNecessary()
            val showAppActions = remember { mutableStateOf(false) }
            val showLoading = remember { mutableStateOf(false) }
            val openTitleUpdateDialog = remember { mutableStateOf(false) }
            val canClose = remember { mutableStateOf(true) }
            val openDlcDialog = remember { mutableStateOf(false) }
            var openAppBarExtra by remember { mutableStateOf(false) }
            val showError = remember {
                mutableStateOf("")
            }

            val selectedModel = remember {
                mutableStateOf(viewModel.mainViewModel?.selected)
            }
            var query by remember {
                mutableStateOf("")
            }
            var refreshUser by remember {
                mutableStateOf(true)
            }

            var isFabVisible by remember {
                mutableStateOf(true)
            }

            // 添加重命名功能的状态变量
            var showRenameDialog by remember { mutableStateOf(false) }
            var newGameName by remember { mutableStateOf("") }
            val focusRequester = remember { FocusRequester() }

            // 添加Mod管理对话框状态
            var showModManagementDialog by remember { mutableStateOf(false) }

            val nestedScrollConnection = remember {
                object : NestedScrollConnection {
                    override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
                        if (available.y < -1) {
                            isFabVisible = false
                        }
                        if (available.y > 1) {
                            isFabVisible = true
                        }
                        return Offset.Zero
                    }
                }
            }

            // 获取屏幕方向
            val configuration = LocalConfiguration.current
            val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE

            // 添加屏幕方向改变时的处理
            LaunchedEffect(configuration.orientation) {
                // 当屏幕方向改变时，关闭底部操作菜单
                showAppActions.value = false
                selectedModel.value = null
                viewModel.mainViewModel?.selected = null
            }

            // 横屏模式下跟踪当前中央项
            var centeredIndex by remember { mutableStateOf(0) }

            // 使用无背景的ModalBottomSheet
            val sheetState = androidx.compose.material3.rememberModalBottomSheetState()

            Scaffold(
                modifier = Modifier.fillMaxSize(),
                topBar = {
                    if (!isLandscape) {
                        // 竖屏模式下的搜索栏
                        SearchBar(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(8.dp),
                            shape = SearchBarDefaults.inputFieldShape,
                            query = query,
                            onQueryChange = {
                                query = it
                            },
                            onSearch = {},
                            active = false,
                            onActiveChange = {},
                            leadingIcon = {
                                Icon(
                                    Icons.Filled.Search,
                                    contentDescription = "Search Games"
                                )
                            },
                            placeholder = {
                                Text(text = "Ryujinx")
                            },
                            trailingIcon = {
                                IconButton(onClick = {
                                    openAppBarExtra = !openAppBarExtra
                                }) {
                                    if (!refreshUser) {
                                        refreshUser = true
                                    }
                                    if (refreshUser)
                                        if (viewModel.mainViewModel?.userViewModel?.openedUser?.userPicture?.isNotEmpty() == true) {
                                            val pic =
                                                viewModel.mainViewModel!!.userViewModel.openedUser.userPicture
                                            Image(
                                                bitmap = BitmapFactory.decodeByteArray(
                                                    pic,
                                                    0,
                                                    pic?.size ?: 0
                                                )
                                                    .asImageBitmap(),
                                                contentDescription = "user image",
                                                contentScale = ContentScale.Crop,
                                                modifier = Modifier
                                                    .padding(4.dp)
                                                    .size(40.dp) // 调整大小
                                                    .clip(RoundedCornerShape(12.dp)) // 改为圆角方形
                                                    .border(1.dp, Color.Gray, RoundedCornerShape(12.dp)) // 添加边框
                                            )
                                        } else {
                                            Box(
                                                modifier = Modifier
                                                    .size(40.dp)
                                                    .clip(RoundedCornerShape(12.dp))
                                                    .border(1.dp, Color.Gray, RoundedCornerShape(12.dp)),
                                                contentAlignment = Alignment.Center
                                            ) {
                                                Icon(
                                                    Icons.Filled.Person,
                                                    contentDescription = "user",
                                                    modifier = Modifier.size(24.dp)
                                                )
                                            }
                                        }
                                }
                            }
                        ) {

                        }
                    } else {
                        // 横屏模式下的紧凑搜索栏 - 移到左上角，宽度缩短
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(8.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.Start // 改为靠左对齐
                        ) {
                            // 搜索框 - 缩短宽度，靠左显示
                            SearchBar(
                                modifier = Modifier
                                    .width(185.dp) // 固定宽度
                                    .height(55.dp),
                                shape = SearchBarDefaults.inputFieldShape,
                                query = query,
                                onQueryChange = {
                                    query = it
                                },
                                onSearch = {},
                                active = false,
                                onActiveChange = {},
                                leadingIcon = {
                                    Icon(
                                        Icons.Filled.Search,
                                        contentDescription = "Search Games"
                                    )
                                },
                                placeholder = {
                                    Text(text = "Search Games")
                                },
                                trailingIcon = {
                                    // 用户头像放在搜索框内部右侧
                                    IconButton(
                                        onClick = {
                                            openAppBarExtra = !openAppBarExtra
                                        },
                                        modifier = Modifier.size(32.dp)
                                    ) {
                                        if (!refreshUser) {
                                        refreshUser = true
                                    }
                                    if (refreshUser)
                                        if (viewModel.mainViewModel?.userViewModel?.openedUser?.userPicture?.isNotEmpty() == true) {
                                            val pic =
                                                viewModel.mainViewModel!!.userViewModel.openedUser.userPicture
                                            Image(
                                                bitmap = BitmapFactory.decodeByteArray(
                                                    pic,
                                                    0,
                                                    pic?.size ?: 0
                                                )
                                                    .asImageBitmap(),
                                                contentDescription = "user image",
                                                contentScale = ContentScale.Crop,
                                                modifier = Modifier
                                                    .size(32.dp)
                                                    .clip(RoundedCornerShape(12.dp)) // 改为圆角方形
                                                    .border(1.dp, Color.Gray, RoundedCornerShape(12.dp)) // 添加边框
                                            )
                                        } else {
                                            Box(
                                                modifier = Modifier
                                                    .size(32.dp)
                                                    .clip(RoundedCornerShape(12.dp))
                                                    .border(1.dp, Color.Gray, RoundedCornerShape(12.dp)),
                                                contentAlignment = Alignment.Center
                                            ) {
                                                Icon(
                                                    Icons.Filled.Person,
                                                    contentDescription = "user",
                                                    modifier = Modifier.size(24.dp)
                                                )
                                            }
                                        }
                                }
                                }
                            ) {}
                        }
                    }
                },
                floatingActionButton = {
                    AnimatedVisibility(
                        visible = isFabVisible,
                        enter = slideInVertically(initialOffsetY = { it * 2 }),
                        exit = slideOutVertically(targetOffsetY = { it * 2 })
                    ) {
                        FloatingActionButton(
                            onClick = {
                                viewModel.requestReload()
                                viewModel.ensureReloadIfNecessary()
                            },
                            shape = MaterialTheme.shapes.small
                        ) {
                            Icon(Icons.Default.Refresh, contentDescription = "refresh")
                        }
                    }
                }

            ) { contentPadding ->
                // 将用户卡片和游戏列表分开处理，确保正确的z-index顺序
                Box(modifier = Modifier.fillMaxSize()) {
                    // 游戏列表内容
                    Box(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(contentPadding)
                            .zIndex(1f)
                    ) {
                        val list = remember {
                            viewModel.gameList
                        }
                        val isLoading = remember {
                            viewModel.isLoading
                        }
                        viewModel.filter(query)

                        if (!isPreview) {
                            val settings = QuickSettings(viewModel.activity!!)

                            if (isLoading.value) {
                                Box(modifier = Modifier.fillMaxSize())
                                {
                                    CircularProgressIndicator(
                                        modifier = Modifier
                                            .width(64.dp)
                                            .align(Alignment.Center),
                                        color = MaterialTheme.colorScheme.secondary,
                                        trackColor = MaterialTheme.colorScheme.surfaceVariant
                                    )
                                }
                            } else {
                                if (isLandscape) {
                                    // 横屏模式：使用自定义轮播布局
                                    val filteredList = list.filter {
                                        it.getDisplayName().isNotEmpty() && 
                                        (query.trim().isEmpty() || 
                                         it.getDisplayName().lowercase(Locale.getDefault()).contains(query))
                                    }
                                    
                                    if (filteredList.isEmpty()) {
                                        // 没有游戏时显示空状态
                                        Box(
                                            modifier = Modifier.fillMaxSize(),
                                            contentAlignment = Alignment.Center
                                        ) {
                                            Text(
                                                text = "No games found",
                                                color = MaterialTheme.colorScheme.onSurfaceVariant
                                            )
                                        }
                                    } else {
                                        // 确保centeredIndex在有效范围内
                                        if (centeredIndex >= filteredList.size) {
                                            centeredIndex = 0
                                        }
                                        
                                        // 计算左右项目的索引
                                        val leftIndex = if (centeredIndex == 0) filteredList.size - 1 else centeredIndex - 1
                                        val rightIndex = if (centeredIndex == filteredList.size - 1) 0 else centeredIndex + 1
                                        
                                        Box(
                                            modifier = Modifier
                                                .fillMaxSize()
                                                .nestedScroll(nestedScrollConnection)
                                                .pointerInput(Unit) {
                                                    detectHorizontalDragGestures { change, dragAmount ->
                                                        // 检测滑动手势
                                                        if (dragAmount > 50) {
                                                            // 向右滑动，显示上一个
                                                            centeredIndex = if (centeredIndex == 0) filteredList.size - 1 else centeredIndex - 1
                                                        } else if (dragAmount < -50) {
                                                            // 向左滑动，显示下一个
                                                            centeredIndex = if (centeredIndex == filteredList.size - 1) 0 else centeredIndex + 1
                                                        }
                                                    }
                                                }
                                        ) {
                                            // 游戏项目 - 调整排列方式确保三个项目都能显示
                                            Row(
                                                modifier = Modifier
                                                    .fillMaxSize()
                                                    .padding(horizontal = 8.dp), // 减少水平内边距
                                                horizontalArrangement = Arrangement.SpaceEvenly, // 使用均匀分布
                                                verticalAlignment = Alignment.CenterVertically
                                            ) {
                                                // 左侧项目
                                                LandscapeGameCarouselItem(
                                                    gameModel = filteredList.getOrNull(leftIndex),
                                                    viewModel = viewModel,
                                                    showAppActions = showAppActions,
                                                    showLoading = showLoading,
                                                    selectedModel = selectedModel,
                                                    showError = showError,
                                                    isCentered = false,
                                                    onItemClick = {
                                                        centeredIndex = leftIndex
                                                    }
                                                )
                                                
                                                // 中央项目
                                                LandscapeGameCarouselItem(
                                                    gameModel = filteredList.getOrNull(centeredIndex),
                                                    viewModel = viewModel,
                                                    showAppActions = showAppActions,
                                                    showLoading = showLoading,
                                                    selectedModel = selectedModel,
                                                    showError = showError,
                                                    isCentered = true
                                                )
                                                
                                                // 右侧项目
                                                LandscapeGameCarouselItem(
                                                    gameModel = filteredList.getOrNull(rightIndex),
                                                    viewModel = viewModel,
                                                    showAppActions = showAppActions,
                                                    showLoading = showLoading,
                                                    selectedModel = selectedModel,
                                                    showError = showError,
                                                    isCentered = false,
                                                    onItemClick = {
                                                        centeredIndex = rightIndex
                                                    }
                                                )
                                            }
                                        }
                                    }
                                } else if (settings.isGrid) {
                                    LazyVerticalGrid(
                                        columns = GridCells.Fixed(2),
                                        modifier = Modifier
                                            .fillMaxSize()
                                            .padding(horizontal = 8.dp)
                                            .nestedScroll(nestedScrollConnection),
                                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                                        verticalArrangement = Arrangement.spacedBy(8.dp)
                                    ) {
                                        items(list) {
                                            if (it.getDisplayName().isNotEmpty() && (query.trim()
                                                    .isEmpty() || it.getDisplayName().lowercase(Locale.getDefault())
                                                    .contains(query))
                                            ) {
                                                GridGameItem(
                                                    gameModel = it,
                                                    viewModel = viewModel,
                                                    showAppActions = showAppActions,
                                                    showLoading = showLoading,
                                                    selectedModel = selectedModel,
                                                    showError = showError
                                                )
                                            }
                                        }
                                    }
                                } else {
                                    LazyColumn(
                                        modifier = Modifier.fillMaxSize()
                                    ) {
                                        items(list) {
                                            if (it.getDisplayName().isNotEmpty() && (query.trim()
                                                    .isEmpty() || it.getDisplayName().lowercase(
                                                    Locale.getDefault()
                                                )
                                                    .contains(query))
                                            ) {
                                                ListGameItem(
                                                    gameModel = it,
                                                    viewModel = viewModel,
                                                    showAppActions = showAppActions,
                                                    showLoading = showLoading,
                                                    selectedModel = selectedModel,
                                                    showError = showError
                                                )
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 用户卡片 - 放在游戏列表上方
                    Column(
                        modifier = Modifier
                            .padding(contentPadding)
                            .zIndex(2f)
                    ) {
                        val iconSize = 52.dp
                        AnimatedVisibility(
                            visible = openAppBarExtra,
                        ) {
                            Card(
                                modifier = Modifier
                                    .padding(vertical = 8.dp, horizontal = 16.dp)
                                    .fillMaxWidth(),
                                shape = MaterialTheme.shapes.medium
                            ) {
                                Column(modifier = Modifier.padding(8.dp)) {
                                    Row(
                                        modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(12.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.SpaceBetween
                            ) {
                                if (refreshUser) {
                                    Box(
                                        modifier = Modifier
                                            .border(
                                                width = 2.dp,
                                                color = Color(0xFF14bf00),
                                                shape = RoundedCornerShape(12.dp) // 改为圆角方形
                                            )
                                            .size(iconSize)
                                            .padding(2.dp),
                                        contentAlignment = Alignment.Center
                                    ) {
                                        if (viewModel.mainViewModel?.userViewModel?.openedUser?.userPicture?.isNotEmpty() == true) {
                                            val pic =
                                                viewModel.mainViewModel!!.userViewModel.openedUser.userPicture
                                            Image(
                                                bitmap = BitmapFactory.decodeByteArray(
                                                    pic,
                                                    0,
                                                    pic?.size ?: 0
                                                )
                                                    .asImageBitmap(),
                                                contentDescription = "user image",
                                                contentScale = ContentScale.Crop,
                                                modifier = Modifier
                                                    .padding(4.dp)
                                                    .size(iconSize)
                                                    .clip(RoundedCornerShape(12.dp)) // 改为圆角方形
                                            )
                                        } else {
                                            Icon(
                                                Icons.Filled.Person,
                                                contentDescription = "user",
                                                modifier = Modifier.size(32.dp)
                                            )
                                        }
                                    }
                                }
                                Card(
                                    modifier = Modifier
                                        .padding(horizontal = 4.dp)
                                        .fillMaxWidth(0.7f),
                                    shape = MaterialTheme.shapes.small,
                                ) {
                                    LazyRow {
                                        if (viewModel.mainViewModel?.userViewModel?.userList?.isNotEmpty() == true) {
                                            items(viewModel.mainViewModel!!.userViewModel.userList) { user ->
                                                if (user.id != viewModel.mainViewModel!!.userViewModel.openedUser.id) {
                                                    Image(
                                                        bitmap = BitmapFactory.decodeByteArray(
                                                            user.userPicture,
                                                            0,
                                                            user.userPicture?.size ?: 0
                                                        )
                                                            .asImageBitmap(),
                                                        contentDescription = "selected image",
                                                        contentScale = ContentScale.Crop,
                                                        modifier = Modifier
                                                            .padding(4.dp)
                                                            .size(iconSize)
                                                            .clip(RoundedCornerShape(12.dp)) // 改为圆角方形
                                                            .combinedClickable(
                                                                onClick = {
                                                                    viewModel.mainViewModel!!.userViewModel.openUser(
                                                                        user
                                                                    )
                                                                    refreshUser =
                                                                        false
                                                                })
                                                    )
                                                }
                                            }
                                        }
                                    }
                                }
                                Box(
                                    modifier = Modifier
                                        .size(iconSize)
                                ) {
                                    IconButton(
                                        modifier = Modifier.fillMaxSize(),
                                        onClick = {
                                            openAppBarExtra = false
                                            navController?.navigate("user")
                                        }) {
                                        Icon(
                                            Icons.Filled.Add,
                                            contentDescription = "N/A"
                                        )
                                    }
                                }
                            }
                        }
                        TextButton(
                            modifier = Modifier.fillMaxWidth(),
                            onClick = {
                                navController?.navigate("settings")
                            }
                        ) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Icon(
                                    Icons.Filled.Settings,
                                    contentDescription = "Settings"
                                )
                                Text(
                                    text = "Settings",
                                    modifier = Modifier.padding(start = 8.dp)
                                )
                            }
                        }
                    }
                }
            }

            if (showLoading.value) {
                BasicAlertDialog(onDismissRequest = { }) {
                    Card(
                        modifier = Modifier
                            .padding(16.dp)
                            .fillMaxWidth(),
                        shape = MaterialTheme.shapes.medium
                    ) {
                        Column(
                            modifier = Modifier
                                .padding(16.dp)
                                .fillMaxWidth()
                        ) {
                            Text(text = "Loading")
                            LinearProgressIndicator(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(top = 16.dp)
                            )
                        }

                    }
                }
            }
            if (openTitleUpdateDialog.value) {
                BasicAlertDialog(onDismissRequest = {
                    openTitleUpdateDialog.value = false
                }) {
                    Surface(
                        modifier = Modifier
                            .wrapContentWidth()
                            .wrapContentHeight(),
                        shape = MaterialTheme.shapes.large,
                        tonalElevation = AlertDialogDefaults.TonalElevation
                    ) {
                        val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                        val name = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                        // TitleUpdateViews.Main(titleId, name, openTitleUpdateDialog, canClose)
                    }

                }
            }
            if (openDlcDialog.value) {
                BasicAlertDialog(onDismissRequest = {
                    openDlcDialog.value = false
                }) {
                    Surface(
                        modifier = Modifier
                            .wrapContentWidth()
                            .wrapContentHeight(),
                        shape = MaterialTheme.shapes.large,
                        tonalElevation = AlertDialogDefaults.TonalElevation
                    ) {
                        val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                        val name = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                        // DlcViews.Main(titleId, name, openDlcDialog, canClose)
                    }

                }
            }

            if (showAppActions.value) {
                ModalBottomSheet(
                    onDismissRequest = {
                        showAppActions.value = false
                        selectedModel.value = null
                    },
                    sheetState = sheetState,
                    scrimColor = Color.Transparent,
                    content = {
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(16.dp)
                        ) {
                            // 显示游戏名和版本号
                            selectedModel.value?.let { game ->
                                Text(
                                    text = game.getDisplayName(),
                                    fontSize = 20.sp,
                                    fontWeight = FontWeight.Bold,
                                    modifier = Modifier.align(Alignment.CenterHorizontally)
                                )
                                if (!game.version.isNullOrEmpty()) {
                                    Text(
                                        text = "v${game.version}",
                                        fontSize = 16.sp,
                                        modifier = Modifier.align(Alignment.CenterHorizontally)
                                    )
                                }
                                Spacer(modifier = Modifier.height(16.dp))
                            }
                            
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceEvenly
                            ) {
                                IconButton(onClick = {
                                    if (viewModel.mainViewModel?.selected != null) {
                                        thread {
                                            showLoading.value = true
                                            val success =
                                                viewModel.mainViewModel!!.loadGame(viewModel.mainViewModel!!.selected!!)
                                            if (success == 1) {
                                                launchOnUiThread {
                                                    viewModel.mainViewModel!!.navigateToGame()
                                                }
                                            } else {
                                                if (success == -2)
                                                    showError.value =
                                                        "Error loading update. Please re-add update file"
                                                viewModel.mainViewModel!!.selected!!.close()
                                            }
                                            showLoading.value = false
                                        }
                                    }
                                }) {
                                    Icon(
                                        Icons.Filled.PlayArrow,
                                        contentDescription = "Run"
                                    )
                                }
                                val showAppMenu = remember { mutableStateOf(false) }
                                Box {
                                    IconButton(onClick = {
                                        showAppMenu.value = true
                                    }) {
                                        Icon(
                                            Icons.Filled.Menu,
                                            contentDescription = "Menu"
                                        )
                                    }
                                    DropdownMenu(
                                        expanded = showAppMenu.value,
                                        onDismissRequest = { showAppMenu.value = false }) {
                                        DropdownMenuItem(text = {
                                            Text(text = "Rename Game")
                                        }, onClick = {
                                            showAppMenu.value = false
                                            selectedModel.value?.let { game ->
                                                newGameName = game.getDisplayName()
                                                showRenameDialog = true
                                            }
                                        })
                                        DropdownMenuItem(text = {
                                            Text(text = "Clear PPTC Cache")
                                        }, onClick = {
                                            showAppMenu.value = false
                                            viewModel.mainViewModel?.clearPptcCache(
                                                viewModel.mainViewModel?.selected?.titleId ?: ""
                                            )
                                        })
                                        DropdownMenuItem(text = {
                                            Text(text = "Purge Shader Cache")
                                        }, onClick = {
                                            showAppMenu.value = false
                                            viewModel.mainViewModel?.purgeShaderCache(
                                                viewModel.mainViewModel?.selected?.titleId ?: ""
                                            )
                                        })
                                        DropdownMenuItem(text = {
                                            Text(text = "Delete All Cache")
                                        }, onClick = {
                                            showAppMenu.value = false
                                            viewModel.mainViewModel?.deleteCache(
                                                viewModel.mainViewModel?.selected?.titleId ?: ""
                                            )
                                        })
                                        DropdownMenuItem(text = {
                                            Text(text = "Manage Updates")
                                        }, onClick = {
                                            showAppMenu.value = false
                                            openTitleUpdateDialog.value = true
                                        })
                                        DropdownMenuItem(text = {
                                            Text(text = "Manage DLC")
                                        }, onClick = {
                                            showAppMenu.value = false
                                            openDlcDialog.value = true
                                        })
                                       DropdownMenuItem(text = {
                                       Text(text = "Manage Cheats")
                                        }, onClick = {
                                     showAppMenu.value = false
                                     val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                                     val gamePath = viewModel.mainViewModel?.selected?.path ?: ""
                                     // 导航到金手指界面
                                    navController?.navigate("cheats/$titleId?gamePath=${android.net.Uri.encode(gamePath)}")
                                    })
                                        // 修改：移除导航到Mod管理，改为显示对话框
                                        DropdownMenuItem(text = {
                                            Text(text = "Manage Mods")
                                        }, onClick = {
                                            showAppMenu.value = false
                                            showModManagementDialog = true
                                        })                                      
                                    }
                                }
                            }
                        }
                    }
                )
            }

            // 添加重命名对话框
            if (showRenameDialog) {
                AlertDialog(
                    onDismissRequest = { showRenameDialog = false },
                    title = { Text(text = "Rename Game") },
                    text = {
                        OutlinedTextField(
                            value = newGameName,
                            onValueChange = { newGameName = it },
                            label = { Text("Game Name") },
                            modifier = Modifier.focusRequester(focusRequester)
                        )
                    },
                    confirmButton = {
                        TextButton(
                            onClick = {
                                selectedModel.value?.customName = newGameName
                                showRenameDialog = false
                                // 刷新列表以显示新名称
                                viewModel.filter(query)
                            }
                        ) {
                            Text("OK")
                        }
                    },
                    dismissButton = {
                        TextButton(
                            onClick = { showRenameDialog = false }
                        ) {
                            Text("Cancel")
                        }
                    }
                )
            }

            // 添加Mod管理对话框
            if (showModManagementDialog) {
                ModManagementDialog(
                    viewModel = viewModel,
                    selectedModel = selectedModel.value,
                    onDismiss = { showModManagementDialog = false }
                )
            }
        }

        @Preview
        @Composable
        fun HomePreview() {
            Home(isPreview = true)
        }
    }
}
