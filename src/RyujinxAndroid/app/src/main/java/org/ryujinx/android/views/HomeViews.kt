// HomeViews.kt
package org.ryujinx.android.views 

import android.content.res.Configuration
import android.content.res.Resources
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.basicMarquee
import androidx.compose.foundation.border
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Build
import androidx.compose.material.icons.filled.Storage
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.Extension
import androidx.compose.material.icons.filled.Save
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.AlertDialogDefaults
import androidx.compose.material3.BasicAlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.FloatingActionButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SearchBar
import androidx.compose.material3.SearchBarDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
import androidx.navigation.NavHostController
import com.anggrayudi.storage.extension.launchOnUiThread
import org.ryujinx.android.R
import org.ryujinx.android.viewmodels.FileType
import org.ryujinx.android.viewmodels.GameModel
import org.ryujinx.android.viewmodels.HomeViewModel
import org.ryujinx.android.viewmodels.QuickSettings
import java.util.Base64
import java.util.Locale
import kotlin.concurrent.thread
import kotlin.math.roundToInt
import kotlin.math.abs
import kotlinx.coroutines.launch
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.foundation.layout.calculateStartPadding
import androidx.compose.foundation.clickable
import java.io.File
import android.content.Context
import androidx.compose.ui.platform.LocalContext
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.OutlinedTextField
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester

class HomeViews {
    companion object {
        const val ListImageSize = 150
        const val GridImageSize = 300

        @Composable
        fun NotAvailableIcon(modifier: Modifier = Modifier) {
            Icon(
                Icons.Filled.Add,
                contentDescription = "N/A",
                modifier = modifier,
                tint = MaterialTheme.colorScheme.onSurfaceVariant
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
            ElevatedCard(
                elevation = CardDefaults.elevatedCardElevation(defaultElevation = 4.dp),
                shape = MaterialTheme.shapes.medium,
                colors = CardDefaults.elevatedCardColors(
                    containerColor = MaterialTheme.colorScheme.surfaceContainer,
                    contentColor = MaterialTheme.colorScheme.onSurface
                ),
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
                        .padding(16.dp),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                            Box(
                                modifier = if (isSelected) {
                                    Modifier
                                        .padding(end = 16.dp)
                                        .border(3.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(12.dp))
                                        .shadow(8.dp, RoundedCornerShape(12.dp))
                                } else {
                                    Modifier
                                        .padding(end = 16.dp)
                                        .shadow(4.dp, RoundedCornerShape(12.dp))
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
                                        contentScale = ContentScale.Crop,
                                        modifier = Modifier
                                            .width(size.roundToInt().dp)
                                            .height(size.roundToInt().dp)
                                            .clip(RoundedCornerShape(12.dp))
                                    )
                                } else if (gameModel.type == FileType.Nro)
                                    NROIcon(
                                        modifier = Modifier
                                            .width(size.roundToInt().dp)
                                            .height(size.roundToInt().dp)
                                            .padding(8.dp)
                                    )
                                else NotAvailableIcon(
                                    modifier = Modifier
                                        .width(size.roundToInt().dp)
                                        .height(size.roundToInt().dp)
                                        .padding(16.dp)
                                )
                            }
                        } else NotAvailableIcon(
                            modifier = Modifier
                                .size(48.dp)
                                .padding(8.dp)
                        )
                        Column {
                            Text(
                                text = gameModel.getDisplayName(),
                                style = MaterialTheme.typography.titleMedium,
                                fontWeight = FontWeight.SemiBold,
                                color = MaterialTheme.colorScheme.onSurface
                            )
                            Spacer(modifier = Modifier.height(4.dp))
                            Text(
                                text = gameModel.developer ?: "Unknown Developer",
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Spacer(modifier = Modifier.height(2.dp))
                            Text(
                                text = gameModel.titleId ?: "No Title ID",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.outline
                            )
                        }
                    }
                    Column(
                        horizontalAlignment = Alignment.End
                    ) {
                        Text(
                            text = gameModel.version ?: "v1.0.0",
                            style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.primary,
                            fontWeight = FontWeight.Medium
                        )
                        Spacer(modifier = Modifier.height(4.dp))
                        Text(
                            text = String.format("%.2f GB", gameModel.fileSize),
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.outline
                        )
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
            ElevatedCard(
                elevation = CardDefaults.elevatedCardElevation(
                    defaultElevation = 8.dp,
                    pressedElevation = 4.dp
                ),
                shape = MaterialTheme.shapes.large,
                colors = CardDefaults.elevatedCardColors(
                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
                    contentColor = MaterialTheme.colorScheme.onSurface
                ),
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(8.dp)
                    .aspectRatio(0.75f)
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
                        .padding(12.dp)
                        .fillMaxWidth(),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                        Box(
                            modifier = if (isSelected) {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                                    .border(3.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(20.dp))
                                    .shadow(12.dp, RoundedCornerShape(20.dp))
                            } else {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                                    .shadow(8.dp, RoundedCornerShape(16.dp))
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
                                        .background(MaterialTheme.colorScheme.surfaceVariant),
                                    contentAlignment = Alignment.Center
                                ) {
                                    NROIcon(
                                        modifier = Modifier
                                            .fillMaxSize(0.7f)
                                    )
                                }
                            } else {
                                Box(
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(16.dp))
                                        .background(MaterialTheme.colorScheme.surfaceVariant),
                                    contentAlignment = Alignment.Center
                                ) {
                                    NotAvailableIcon(
                                        modifier = Modifier
                                            .fillMaxSize(0.5f)
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
                                    .border(3.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(20.dp))
                            } else {
                                Modifier
                                    .fillMaxWidth()
                                    .aspectRatio(1f)
                            }
                        ) {
                            Box(
                                modifier = Modifier
                                    .fillMaxSize()
                                    .clip(RoundedCornerShape(16.dp))
                                    .background(MaterialTheme.colorScheme.surfaceVariant),
                                contentAlignment = Alignment.Center
                            ) {
                                NotAvailableIcon(
                                    modifier = Modifier
                                        .fillMaxSize(0.5f)
                                )
                            }
                        }
                    }
                    Spacer(modifier = Modifier.height(12.dp))
                    Text(
                        text = gameModel.getDisplayName(),
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.Medium,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        textAlign = TextAlign.Center,
                        modifier = Modifier
                            .padding(horizontal = 4.dp)
                            .fillMaxWidth()
                    )
                    Spacer(modifier = Modifier.height(4.dp))
                    Text(
                        text = gameModel.version ?: "v1.0.0",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.primary,
                        fontWeight = FontWeight.Medium,
                        modifier = Modifier.padding(horizontal = 4.dp)
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
                Box(
                    modifier = Modifier
                        .size(100.dp)
                        .clip(RoundedCornerShape(16.dp))
                        .background(MaterialTheme.colorScheme.surfaceContainerHigh)
                )
                return
            }

            remember {
                selectedModel
            }
            val isSelected = selectedModel.value == gameModel
            val decoder = Base64.getDecoder()
            
            val backgroundColor = MaterialTheme.colorScheme.background
            val luminance = 0.299 * backgroundColor.red + 0.587 * backgroundColor.green + 0.114 * backgroundColor.blue
            val borderColor = if (luminance > 0.5) Color(0x26000000) else Color(0x26FFFFFF)

            if (isCentered) {
                ElevatedCard(
                    elevation = CardDefaults.elevatedCardElevation(defaultElevation = 16.dp),
                    shape = RoundedCornerShape(24.dp),
                    colors = CardDefaults.elevatedCardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceContainerHighest
                    ),
                    modifier = Modifier
                        .fillMaxWidth(0.7f)
                        .aspectRatio(1.1f)
                        .offset(y = (-30).dp)
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
                            .border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.3f), RoundedCornerShape(24.dp))
                            .then(
                                if (isSelected) {
                                    Modifier.border(
                                        4.dp,
                                        MaterialTheme.colorScheme.primary,
                                        RoundedCornerShape(24.dp)
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
                                    contentScale = ContentScale.Crop,
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(24.dp))
                                )
                            } else if (gameModel.type == FileType.Nro) {
                                Box(
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(24.dp))
                                        .background(MaterialTheme.colorScheme.surfaceVariant),
                                    contentAlignment = Alignment.Center
                                ) {
                                    NROIcon(
                                        modifier = Modifier
                                            .fillMaxSize(0.6f)
                                    )
                                }
                            } else {
                                Box(
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .clip(RoundedCornerShape(24.dp))
                                        .background(MaterialTheme.colorScheme.surfaceVariant),
                                    contentAlignment = Alignment.Center
                                ) {
                                    NotAvailableIcon(
                                        modifier = Modifier
                                            .fillMaxSize(0.4f)
                                    )
                                }
                            }
                        } else {
                            Box(
                                modifier = Modifier
                                    .fillMaxSize()
                                    .clip(RoundedCornerShape(24.dp))
                                    .background(MaterialTheme.colorScheme.surfaceVariant),
                                contentAlignment = Alignment.Center
                            ) {
                                NotAvailableIcon(
                                    modifier = Modifier
                                        .fillMaxSize(0.4f)
                                )
                            }
                        }
                        
                        // 添加游戏名称覆盖层
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(40.dp)
                                .align(Alignment.BottomCenter)
                                .background(
                                    brush = Brush.verticalGradient(
                                        colors = listOf(
                                            Color.Transparent,
                                            MaterialTheme.colorScheme.surfaceContainerHighest.copy(alpha = 0.9f)
                                        )
                                    )
                                )
                        ) {
                            Text(
                                text = gameModel.getDisplayName(),
                                style = MaterialTheme.typography.titleSmall,
                                fontWeight = FontWeight.SemiBold,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier
                                    .align(Alignment.Center)
                                    .padding(horizontal = 12.dp)
                                    .basicMarquee()
                            )
                        }
                    }
                }
            } else {
                Card(
                    elevation = CardDefaults.cardElevation(defaultElevation = 8.dp),
                    shape = RoundedCornerShape(16.dp),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceContainerHigh
                    ),
                    modifier = Modifier
                        .size(90.dp)
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
                                modifier = Modifier
                                    .fillMaxSize()
                                    .clip(RoundedCornerShape(16.dp))
                            )
                        } else if (gameModel.type == FileType.Nro) {
                            Box(
                                modifier = Modifier
                                    .fillMaxSize()
                                    .clip(RoundedCornerShape(16.dp))
                                    .background(MaterialTheme.colorScheme.surfaceVariant),
                                contentAlignment = Alignment.Center
                            ) {
                                NROIcon(
                                    modifier = Modifier
                                        .fillMaxSize(0.7f)
                                )
                            }
                        } else {
                            Box(
                                modifier = Modifier
                                    .fillMaxSize()
                                    .clip(RoundedCornerShape(16.dp))
                                    .background(MaterialTheme.colorScheme.surfaceVariant),
                                contentAlignment = Alignment.Center
                            ) {
                                NotAvailableIcon(
                                    modifier = Modifier
                                        .fillMaxSize(0.5f)
                                )
                            }
                        }
                    } else {
                        Box(
                            modifier = Modifier
                                .fillMaxSize()
                                .clip(RoundedCornerShape(16.dp))
                                .background(MaterialTheme.colorScheme.surfaceVariant),
                            contentAlignment = Alignment.Center
                        ) {
                            NotAvailableIcon(
                                modifier = Modifier
                                    .fillMaxSize(0.5f)
                            )
                        }
                    }
                }
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

            var showRenameDialog by remember { mutableStateOf(false) }
            var newGameName by remember { mutableStateOf("") }
            val focusRequester = remember { FocusRequester() }

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

            val configuration = LocalConfiguration.current
            val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE

            LaunchedEffect(configuration.orientation) {
                showAppActions.value = false
                selectedModel.value = null
                viewModel.mainViewModel?.selected = null
            }

            var centeredIndex by remember { mutableStateOf(0) }
            var isInitialLoad by remember { mutableStateOf(true) }

            val sheetState = androidx.compose.material3.rememberModalBottomSheetState()

            Scaffold(
                modifier = Modifier.fillMaxSize(),
                topBar = {
                    if (!isLandscape) {
                        Surface(
                            tonalElevation = 8.dp,
                            shadowElevation = 8.dp,
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            SearchBar(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(16.dp),
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
                                        contentDescription = "Search Games",
                                        tint = MaterialTheme.colorScheme.primary
                                    )
                                },
                                placeholder = {
                                    Text(
                                        text = "Search your games...",
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
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
                                                        .size(36.dp)
                                                        .clip(RoundedCornerShape(10.dp))
                                                        .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(10.dp))
                                                )
                                            } else {
                                                Box(
                                                    modifier = Modifier
                                                        .size(36.dp)
                                                        .clip(RoundedCornerShape(10.dp))
                                                        .background(MaterialTheme.colorScheme.surfaceVariant)
                                                        .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(10.dp)),
                                                    contentAlignment = Alignment.Center
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Person,
                                                        contentDescription = "user",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                                                    )
                                                }
                                            }
                                    }
                                },
                                colors = SearchBarDefaults.colors(
                                    containerColor = MaterialTheme.colorScheme.surfaceContainer,
                                    inputFieldColor = MaterialTheme.colorScheme.surface
                                )
                            ) {}
                        }
                    } else {
                        Surface(
                            tonalElevation = 8.dp,
                            shadowElevation = 8.dp,
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(16.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.Start
                            ) {
                                SearchBar(
                                    modifier = Modifier
                                        .width(220.dp)
                                        .height(56.dp),
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
                                            contentDescription = "Search Games",
                                            tint = MaterialTheme.colorScheme.primary
                                        )
                                    },
                                    placeholder = {
                                        Text(
                                            text = "Search Games",
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                    },
                                    trailingIcon = {
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
                                                            .size(28.dp)
                                                            .clip(RoundedCornerShape(8.dp))
                                                            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(8.dp))
                                                    )
                                                } else {
                                                    Box(
                                                        modifier = Modifier
                                                            .size(28.dp)
                                                            .clip(RoundedCornerShape(8.dp))
                                                            .background(MaterialTheme.colorScheme.surfaceVariant)
                                                            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(8.dp)),
                                                        contentAlignment = Alignment.Center
                                                    ) {
                                                        Icon(
                                                            Icons.Filled.Person,
                                                            contentDescription = "user",
                                                            modifier = Modifier.size(16.dp),
                                                            tint = MaterialTheme.colorScheme.onSurfaceVariant
                                                        )
                                                    }
                                                }
                                        }
                                    },
                                    colors = SearchBarDefaults.colors(
                                        containerColor = MaterialTheme.colorScheme.surfaceContainer,
                                        inputFieldColor = MaterialTheme.colorScheme.surface
                                    )
                                ) {}
                            }
                        }
                    }
                },
                floatingActionButton = {
                    AnimatedVisibility(
                        visible = isFabVisible,
                        enter = slideInVertically(initialOffsetY = { it * 2 }) + fadeIn(),
                        exit = slideOutVertically(targetOffsetY = { it * 2 }) + fadeOut()
                    ) {
                        FloatingActionButton(
                            onClick = {
                                viewModel.requestReload()
                                viewModel.ensureReloadIfNecessary()
                            },
                            shape = MaterialTheme.shapes.medium,
                            containerColor = MaterialTheme.colorScheme.primaryContainer,
                            contentColor = MaterialTheme.colorScheme.onPrimaryContainer,
                            elevation = FloatingActionButtonDefaults.elevation(
                                defaultElevation = 8.dp,
                                pressedElevation = 4.dp
                            )
                        ) {
                            Icon(Icons.Default.Refresh, contentDescription = "Refresh Games")
                        }
                    }
                }

            ) { contentPadding ->
                Box(modifier = Modifier.fillMaxSize()) {
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
                                    Column(
                                        modifier = Modifier.align(Alignment.Center),
                                        horizontalAlignment = Alignment.CenterHorizontally
                                    ) {
                                        CircularProgressIndicator(
                                            modifier = Modifier.size(64.dp),
                                            color = MaterialTheme.colorScheme.primary,
                                            strokeWidth = 4.dp
                                        )
                                        Spacer(modifier = Modifier.height(16.dp))
                                        Text(
                                            text = "Loading Games...",
                                            style = MaterialTheme.typography.bodyMedium,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                    }
                                }
                            } else {
                                if (isLandscape) {
                                    val filteredList = list.filter {
                                        it.getDisplayName().isNotEmpty() && 
                                        (query.trim().isEmpty() || 
                                         it.getDisplayName().lowercase(Locale.getDefault()).contains(query))
                                    }
                                    
                                    if (filteredList.isEmpty()) {
                                        Box(
                                            modifier = Modifier.fillMaxSize(),
                                            contentAlignment = Alignment.Center
                                        ) {
                                            Column(
                                                horizontalAlignment = Alignment.CenterHorizontally
                                            ) {
                                                Icon(
                                                    Icons.Filled.Search,
                                                    contentDescription = "No games",
                                                    modifier = Modifier.size(64.dp),
                                                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                                                )
                                                Spacer(modifier = Modifier.height(16.dp))
                                                Text(
                                                    text = "No games found",
                                                    style = MaterialTheme.typography.titleMedium,
                                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                                )
                                                Spacer(modifier = Modifier.height(8.dp))
                                                Text(
                                                    text = "Try adjusting your search or add new games",
                                                    style = MaterialTheme.typography.bodyMedium,
                                                    color = MaterialTheme.colorScheme.outline,
                                                    textAlign = TextAlign.Center
                                                )
                                            }
                                        }
                                    } else {
                                        if (centeredIndex >= filteredList.size) {
                                            centeredIndex = 0
                                        }
                                        
                                        val leftIndex = if (centeredIndex == 0) filteredList.size - 1 else centeredIndex - 1
                                        val rightIndex = if (centeredIndex == filteredList.size - 1) 0 else centeredIndex + 1
                                        
                                        Box(
                                            modifier = Modifier
                                                .fillMaxSize()
                                                .nestedScroll(nestedScrollConnection)
                                                .pointerInput(Unit) {
                                                    detectHorizontalDragGestures { change, dragAmount ->
                                                        if (dragAmount > 50) {
                                                            centeredIndex = if (centeredIndex == 0) filteredList.size - 1 else centeredIndex - 1
                                                        } else if (dragAmount < -50) {
                                                            centeredIndex = if (centeredIndex == filteredList.size - 1) 0 else centeredIndex + 1
                                                        }
                                                    }
                                                }
                                        ) {
                                            Row(
                                                modifier = Modifier
                                                    .fillMaxSize()
                                                    .padding(horizontal = 16.dp),
                                                horizontalArrangement = Arrangement.SpaceEvenly,
                                                verticalAlignment = Alignment.CenterVertically
                                            ) {
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
                                                
                                                LandscapeGameCarouselItem(
                                                    gameModel = filteredList.getOrNull(centeredIndex),
                                                    viewModel = viewModel,
                                                    showAppActions = showAppActions,
                                                    showLoading = showLoading,
                                                    selectedModel = selectedModel,
                                                    showError = showError,
                                                    isCentered = true
                                                )
                                                
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
                                            
                                            // 添加指示器
                                            if (filteredList.size > 1) {
                                                Box(
                                                    modifier = Modifier
                                                        .align(Alignment.BottomCenter)
                                                        .padding(bottom = 32.dp)
                                                ) {
                                                    Row(
                                                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                                                    ) {
                                                        filteredList.forEachIndexed { index, _ ->
                                                            Box(
                                                                modifier = Modifier
                                                                    .size(8.dp)
                                                                    .clip(CircleShape)
                                                                    .background(
                                                                        if (index == centeredIndex) 
                                                                            MaterialTheme.colorScheme.primary 
                                                                        else 
                                                                            MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.3f)
                                                                    )
                                                            )
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                } else if (settings.isGrid) {
                                    LazyVerticalGrid(
                                        columns = GridCells.Adaptive(160.dp),
                                        modifier = Modifier
                                            .fillMaxSize()
                                            .padding(16.dp)
                                            .nestedScroll(nestedScrollConnection),
                                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                                        verticalArrangement = Arrangement.spacedBy(16.dp)
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
                                        modifier = Modifier
                                            .fillMaxSize()
                                            .padding(horizontal = 8.dp)
                                            .nestedScroll(nestedScrollConnection),
                                        verticalArrangement = Arrangement.spacedBy(8.dp)
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

                    Column(
                        modifier = Modifier
                            .padding(contentPadding)
                            .zIndex(2f)
                    ) {
                        val iconSize = 56.dp
                        AnimatedVisibility(
                            visible = openAppBarExtra,
                            enter = slideInVertically() + fadeIn(),
                            exit = slideOutVertically() + fadeOut()
                        ) {
                            ElevatedCard(
                                elevation = CardDefaults.elevatedCardElevation(defaultElevation = 12.dp),
                                shape = MaterialTheme.shapes.large,
                                colors = CardDefaults.elevatedCardColors(
                                    containerColor = MaterialTheme.colorScheme.surfaceContainerHigh
                                ),
                                modifier = Modifier
                                    .padding(horizontal = 16.dp, vertical = 8.dp)
                                    .fillMaxWidth()
                            ) {
                                Column(modifier = Modifier.padding(16.dp)) {
                                    Row(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(8.dp),
                                        verticalAlignment = Alignment.CenterVertically,
                                        horizontalArrangement = Arrangement.SpaceBetween
                                    ) {
                                        if (refreshUser) {
                                            Box(
                                                modifier = Modifier
                                                    .border(
                                                        width = 2.dp,
                                                        color = MaterialTheme.colorScheme.primary,
                                                        shape = RoundedCornerShape(14.dp)
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
                                                            .size(iconSize)
                                                            .clip(RoundedCornerShape(12.dp))
                                                    )
                                                } else {
                                                    Box(
                                                        modifier = Modifier
                                                            .size(iconSize)
                                                            .clip(RoundedCornerShape(12.dp))
                                                            .background(MaterialTheme.colorScheme.surfaceVariant),
                                                        contentAlignment = Alignment.Center
                                                    ) {
                                                        Icon(
                                                            Icons.Filled.Person,
                                                            contentDescription = "user",
                                                            modifier = Modifier.size(28.dp),
                                                            tint = MaterialTheme.colorScheme.onSurfaceVariant
                                                        )
                                                    }
                                                }
                                            }
                                        }
                                        Card(
                                            modifier = Modifier
                                                .padding(horizontal = 8.dp)
                                                .fillMaxWidth(0.7f)
                                                .height(iconSize),
                                            shape = MaterialTheme.shapes.medium,
                                            colors = CardDefaults.cardColors(
                                                containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                                            )
                                        ) {
                                            LazyRow(
                                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                                contentPadding = PaddingValues(8.dp)
                                            ) {
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
                                                                contentDescription = "user image",
                                                                contentScale = ContentScale.Crop,
                                                                modifier = Modifier
                                                                    .size(40.dp)
                                                                    .clip(RoundedCornerShape(10.dp))
                                                                    .combinedClickable(
                                                                        onClick = {
                                                                            viewModel.mainViewModel!!.userViewModel.openUser(
                                                                                user
                                                                            )
                                                                            refreshUser = false
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
                                                    contentDescription = "Add User",
                                                    tint = MaterialTheme.colorScheme.primary
                                                )
                                            }
                                        }
                                    }
                                    TextButton(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(top = 8.dp),
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
                                                contentDescription = "Settings",
                                                tint = MaterialTheme.colorScheme.onSurfaceVariant
                                            )
                                            Text(
                                                text = "Settings",
                                                modifier = Modifier.padding(start = 12.dp),
                                                color = MaterialTheme.colorScheme.onSurface
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (showLoading.value) {
                BasicAlertDialog(onDismissRequest = { }) {
                    Card(
                        modifier = Modifier
                            .padding(32.dp)
                            .fillMaxWidth(0.8f),
                        shape = MaterialTheme.shapes.extraLarge,
                        colors = CardDefaults.cardColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainerHigh
                        ),
                        elevation = CardDefaults.cardElevation(defaultElevation = 24.dp)
                    ) {
                        Column(
                            modifier = Modifier
                                .padding(24.dp)
                                .fillMaxWidth(),
                            horizontalAlignment = Alignment.CenterHorizontally
                        ) {
                            Text(
                                text = "Loading Game",
                                style = MaterialTheme.typography.titleMedium,
                                fontWeight = FontWeight.SemiBold
                            )
                            Spacer(modifier = Modifier.height(20.dp))
                            LinearProgressIndicator(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .height(6.dp)
                                    .clip(RoundedCornerShape(3.dp)),
                                color = MaterialTheme.colorScheme.primary
                            )
                            Spacer(modifier = Modifier.height(8.dp))
                            Text(
                                text = "Please wait...",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
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
                        shape = MaterialTheme.shapes.extraLarge,
                        tonalElevation = AlertDialogDefaults.TonalElevation,
                        color = MaterialTheme.colorScheme.surfaceContainerHigh
                    ) {
                        val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                        val name = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                        TitleUpdateViews.Main(titleId, name, openTitleUpdateDialog, canClose)
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
                        shape = MaterialTheme.shapes.extraLarge,
                        tonalElevation = AlertDialogDefaults.TonalElevation,
                        color = MaterialTheme.colorScheme.surfaceContainerHigh
                    ) {
                        val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                        val name = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                        DlcViews.Main(titleId, name, openDlcDialog, canClose)
                    }
                }
            }

            if (showAppActions.value) {
                val configuration = LocalConfiguration.current
                val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
                
                var showAppMenu by remember { mutableStateOf(false) }
                
                ModalBottomSheet(
                    onDismissRequest = {
                        showAppActions.value = false
                        selectedModel.value = null
                        showAppMenu = false
                    },
                    sheetState = sheetState,
                    containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
                    scrimColor = Color(0x99000000),
                    modifier = if (isLandscape) {
                        Modifier.heightIn(max = configuration.screenHeightDp.dp * 0.7f)
                    } else {
                        Modifier
                    }
                ) {
                    LazyColumn(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 16.dp, vertical = 8.dp)
                    ) {
                        item {
                            selectedModel.value?.let { game ->
                                Column(
                                    modifier = Modifier.fillMaxWidth()
                                ) {
                                    Text(
                                        text = game.getDisplayName(),
                                        style = MaterialTheme.typography.titleLarge,
                                        fontWeight = FontWeight.Bold,
                                        modifier = Modifier.align(Alignment.CenterHorizontally),
                                        textAlign = TextAlign.Center,
                                        color = MaterialTheme.colorScheme.onSurface
                                    )
                                    if (!game.version.isNullOrEmpty()) {
                                        Text(
                                            text = "Version ${game.version}",
                                            style = MaterialTheme.typography.bodyMedium,
                                            modifier = Modifier.align(Alignment.CenterHorizontally),
                                            textAlign = TextAlign.Center,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )
                                    }
                                    Spacer(modifier = Modifier.height(20.dp))
                                }
                            }
                        }
                        
                        item {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceEvenly
                            ) {
                                IconButton(
                                    onClick = {
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
                                    },
                                    modifier = Modifier.size(56.dp)
                                ) {
                                    Column(
                                        horizontalAlignment = Alignment.CenterHorizontally
                                    ) {
                                        Box(
                                            modifier = Modifier
                                                .size(44.dp)
                                                .clip(CircleShape)
                                                .background(MaterialTheme.colorScheme.primaryContainer),
                                            contentAlignment = Alignment.Center
                                        ) {
                                            Icon(
                                                Icons.Filled.PlayArrow,
                                                contentDescription = "Run",
                                                modifier = Modifier.size(24.dp),
                                                tint = MaterialTheme.colorScheme.onPrimaryContainer
                                            )
                                        }
                                        Spacer(modifier = Modifier.height(4.dp))
                                        Text(
                                            text = "Play",
                                            style = MaterialTheme.typography.labelSmall,
                                            color = MaterialTheme.colorScheme.onSurface
                                        )
                                    }
                                }
                                
                                Box {
                                    IconButton(
                                        onClick = {
                                            showAppMenu = !showAppMenu
                                        },
                                        modifier = Modifier.size(56.dp)
                                    ) {
                                        Column(
                                            horizontalAlignment = Alignment.CenterHorizontally
                                        ) {
                                            Box(
                                                modifier = Modifier
                                                    .size(44.dp)
                                                    .clip(CircleShape)
                                                    .background(MaterialTheme.colorScheme.secondaryContainer),
                                                contentAlignment = Alignment.Center
                                            ) {
                                                Icon(
                                                    Icons.Filled.MoreVert,
                                                    contentDescription = "Menu",
                                                    modifier = Modifier.size(24.dp),
                                                    tint = MaterialTheme.colorScheme.onSecondaryContainer
                                                )
                                            }
                                            Spacer(modifier = Modifier.height(4.dp))
                                            Text(
                                                text = "More",
                                                style = MaterialTheme.typography.labelSmall,
                                                color = MaterialTheme.colorScheme.onSurface
                                            )
                                        }
                                    }
                                    
                                    DropdownMenu(
                                        expanded = showAppMenu,
                                        onDismissRequest = { showAppMenu = false },
                                        modifier = Modifier.background(MaterialTheme.colorScheme.surfaceContainerHigh)
                                    ) {
                                        DropdownMenuItem(
                                            text = { 
                                                Row(
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Edit,
                                                        contentDescription = "Rename",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurface
                                                    )
                                                    Spacer(modifier = Modifier.width(12.dp))
                                                    Text("Rename Game")
                                                }
                                            },
                                            onClick = {
                                                showAppMenu = false
                                                selectedModel.value?.let { game ->
                                                    newGameName = game.getDisplayName()
                                                    showRenameDialog = true
                                                }
                                            }
                                        )
                                        DropdownMenuItem(
                                            text = { 
                                                Row(
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Build,
                                                        contentDescription = "Clear Cache",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurface
                                                    )
                                                    Spacer(modifier = Modifier.width(12.dp))
                                                    Text("Clear PPTC Cache")
                                                }
                                            },
                                            onClick = {
                                                showAppMenu = false
                                                viewModel.mainViewModel?.clearPptcCache(
                                                    viewModel.mainViewModel?.selected?.titleId ?: ""
                                                )
                                            }
                                        )
                                        DropdownMenuItem(
                                            text = { 
                                                Row(
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Storage,
                                                        contentDescription = "Purge Shaders",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurface
                                                    )
                                                    Spacer(modifier = Modifier.width(12.dp))
                                                    Text("Clear Shader Cache")
                                                }
                                            },
                                            onClick = {
                                                showAppMenu = false
                                                viewModel.mainViewModel?.purgeShaderCache(
                                                    viewModel.mainViewModel?.selected?.titleId ?: ""
                                                )
                                            }
                                        )
                                        DropdownMenuItem(
                                            text = { 
                                                Row(
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Delete,
                                                        contentDescription = "Delete Cache",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurface
                                                    )
                                                    Spacer(modifier = Modifier.width(12.dp))
                                                    Text("Delete All Cache")
                                                }
                                            },
                                            onClick = {
                                                showAppMenu = false
                                                viewModel.mainViewModel?.deleteCache(
                                                    viewModel.mainViewModel?.selected?.titleId ?: ""
                                                )
                                            }
                                        )
                                        DropdownMenuItem(
                                            text = { 
                                                Row(
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Build,
                                                        contentDescription = "Manage Updates",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurface
                                                    )
                                                    Spacer(modifier = Modifier.width(12.dp))
                                                    Text("Manage Updates")
                                                }
                                            },
                                            onClick = {
                                                showAppMenu = false
                                                openTitleUpdateDialog.value = true
                                            }
                                        )
                                        DropdownMenuItem(
                                            text = { 
                                                Row(
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Extension,
                                                        contentDescription = "Manage DLC",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurface
                                                    )
                                                    Spacer(modifier = Modifier.width(12.dp))
                                                    Text("Manage DLC")
                                                }
                                            },
                                            onClick = {
                                                showAppMenu = false
                                                openDlcDialog.value = true
                                            }
                                        )
                                        DropdownMenuItem(
                                            text = { 
                                                Row(
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Code,
                                                        contentDescription = "Manage Cheats",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurface
                                                    )
                                                    Spacer(modifier = Modifier.width(12.dp))
                                                    Text("Manage Cheats")
                                                }
                                            },
                                            onClick = {
                                                showAppMenu = false
                                                val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                                                val gamePath = viewModel.mainViewModel?.selected?.path ?: ""
                                                navController?.navigate("cheats/$titleId?gamePath=${android.net.Uri.encode(gamePath)}")
                                            }
                                        )
                                        DropdownMenuItem(
                                            text = { 
                                                Row(
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Save,
                                                        contentDescription = "Manage Save Data",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurface
                                                    )
                                                    Spacer(modifier = Modifier.width(12.dp))
                                                    Text("Manage Save Data")
                                                }
                                            },
                                            onClick = {
                                                showAppMenu = false
                                                val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                                                val gameName = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                                                navController?.navigate("savedata/$titleId?gameName=${android.net.Uri.encode(gameName)}")
                                            }
                                        )
                                        DropdownMenuItem(
                                            text = { 
                                                Row(
                                                    verticalAlignment = Alignment.CenterVertically
                                                ) {
                                                    Icon(
                                                        Icons.Filled.Extension,
                                                        contentDescription = "Manage Mods",
                                                        modifier = Modifier.size(20.dp),
                                                        tint = MaterialTheme.colorScheme.onSurface
                                                    )
                                                    Spacer(modifier = Modifier.width(12.dp))
                                                    Text("Manage Mods")
                                                }
                                            },
                                            onClick = {
                                                showAppMenu = false
                                                val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                                                val gameName = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                                                navController?.navigate("mods/$titleId?gameName=${android.net.Uri.encode(gameName)}")
                                            }
                                        )
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (showRenameDialog) {
                AlertDialog(
                    onDismissRequest = { showRenameDialog = false },
                    title = { 
                        Text(
                            text = "Rename Game", 
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.SemiBold
                        )
                    },
                    text = {
                        OutlinedTextField(
                            value = newGameName,
                            onValueChange = { newGameName = it },
                            label = { Text("Game Name") },
                            modifier = Modifier
                                .fillMaxWidth()
                                .focusRequester(focusRequester),
                            shape = MaterialTheme.shapes.small,
                            colors = androidx.compose.material3.OutlinedTextFieldDefaults.colors(
                                focusedBorderColor = MaterialTheme.colorScheme.primary,
                                unfocusedBorderColor = MaterialTheme.colorScheme.outline
                            )
                        )
                    },
                    confirmButton = {
                        TextButton(
                            onClick = {
                                selectedModel.value?.customName = newGameName
                                showRenameDialog = false
                                viewModel.filter(query)
                            }
                        ) {
                            Text(
                                "Confirm",
                                color = MaterialTheme.colorScheme.primary
                            )
                        }
                    },
                    dismissButton = {
                        TextButton(
                            onClick = { showRenameDialog = false }
                        ) {
                            Text(
                                "Cancel",
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    },
                    containerColor = MaterialTheme.colorScheme.surfaceContainerHigh
                )
                
                LaunchedEffect(showRenameDialog) {
                    if (showRenameDialog) {
                        focusRequester.requestFocus()
                    }
                }
            }
        }

        @Preview
        @Composable
        fun HomePreview() {
            Home(isPreview = true)
        }
    }
}
