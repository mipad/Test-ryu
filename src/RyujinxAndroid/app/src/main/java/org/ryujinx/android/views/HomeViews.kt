package org.ryujinx.android.views 

import android.content.res.Configuration
import android.content.res.Resources
import android.graphics.BitmapFactory
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
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
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.ArrowForward
import androidx.compose.material3.AlertDialogDefaults
import androidx.compose.material3.BasicAlertDialog
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
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SearchBar
import androidx.compose.material3.SearchBarDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
                                        contentDescription = gameModel.titleName + " icon",
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
                            Text(text = gameModel.titleName ?: "")
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
                                        contentDescription = gameModel.titleName + " icon",
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
                        text = gameModel.titleName ?: "N/A",
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier
                            .padding(vertical = 4.dp)
                            .basicMarquee()
                    )
                }
            }
        }

        @Composable
        fun CenterFrameBox() {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(200.dp)
                    .border(
                        width = 2.dp,
                        color = MaterialTheme.colorScheme.primary.copy(alpha = 0.5f),
                        shape = RoundedCornerShape(16.dp)
                    )
            ) {
                // 空框，只显示边框
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
                        .size(120.dp)
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

            if (isCentered) {
                // 中央项目 - 显示完整信息
                Surface(
                    shape = RoundedCornerShape(16.dp),
                    color = MaterialTheme.colorScheme.surfaceContainer,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(200.dp)
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
                            .padding(16.dp)
                            .fillMaxSize()
                    ) {
                        // 上部：图标和版本信息
                        Row(
                            modifier = Modifier
                                .weight(1f)
                                .fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            // 图标部分
                            if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                                Box(
                                    modifier = if (isSelected) {
                                        Modifier
                                            .size(120.dp)
                                            .border(2.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(16.dp))
                                    } else {
                                        Modifier.size(120.dp)
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
                                                contentDescription = gameModel.titleName + " icon",
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
                                            .size(120.dp)
                                            .border(2.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(16.dp))
                                    } else {
                                        Modifier.size(120.dp)
                                    }
                                ) {
                                    NotAvailableIcon(
                                        modifier = Modifier
                                            .fillMaxSize(0.8f)
                                            .align(Alignment.Center)
                                    )
                                }
                            }
                            
                            // 版本信息部分 - 右侧横向显示
                            Column(
                                horizontalAlignment = Alignment.End,
                                verticalArrangement = Arrangement.Center
                            ) {
                                Text(
                                    text = "版本",
                                    fontSize = 14.sp,
                                    fontWeight = FontWeight.Bold,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                                Text(
                                    text = gameModel.version ?: "1.0.0",
                                    fontSize = 16.sp,
                                    fontWeight = FontWeight.Bold,
                                    color = MaterialTheme.colorScheme.primary
                                )
                                Spacer(modifier = Modifier.height(8.dp))
                                Text(
                                    text = "开发者",
                                    fontSize = 12.sp,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                                Text(
                                    text = gameModel.developer ?: "",
                                    fontSize = 14.sp,
                                    maxLines = 2,
                                    overflow = TextOverflow.Ellipsis,
                                    color = MaterialTheme.colorScheme.onSurface
                                )
                            }
                        }
                        
                        // 底部：游戏名称
                        Text(
                            text = gameModel.titleName ?: "N/A",
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = 8.dp)
                                .basicMarquee(),
                            fontSize = 18.sp,
                            fontWeight = FontWeight.Bold,
                            color = MaterialTheme.colorScheme.primary
                        )
                    }
                }
            } else {
                // 两侧项目 - 只显示图标
                Box(
                    modifier = Modifier
                        .size(120.dp)
                        .clip(RoundedCornerShape(16.dp))
                        .clickable { onItemClick() }
                ) {
                    if (!gameModel.titleId.isNullOrEmpty() && (gameModel.titleId != "0000000000000000" || gameModel.type == FileType.Nro)) {
                        if (gameModel.icon?.isNotEmpty() == true) {
                            val pic = decoder.decode(gameModel.icon)
                            Image(
                                bitmap = BitmapFactory.decodeByteArray(pic, 0, pic.size)
                                    .asImageBitmap(),
                                contentDescription = gameModel.titleName + " icon",
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
            val query = remember {
                mutableStateOf("")
            }
            var refreshUser by remember {
                mutableStateOf(true)
            }

            var isFabVisible by remember {
                mutableStateOf(true)
            }

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

            // 横屏模式下跟踪当前中央项
            var centeredIndex by remember { mutableStateOf(0) }
            // 添加一个标志来跟踪是否是初始加载
            var isInitialLoad by remember { mutableStateOf(true) }

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
                            query = query.value,
                            onQueryChange = {
                                query.value = it
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
                                                    .size(52.dp)
                                                    .clip(CircleShape)
                                            )
                                        } else {
                                            Icon(
                                                Icons.Filled.Person,
                                                contentDescription = "user"
                                            )
                                        }
                                }
                            }
                        ) {

                        }
                    } else {
                        // 横屏模式下的紧凑搜索栏
                        SearchBar(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 4.dp, horizontal = 8.dp)
                                .height(48.dp),
                            shape = SearchBarDefaults.inputFieldShape,
                            query = query.value,
                            onQueryChange = {
                                query.value = it
                            },
                            onSearch = {},
                            active = false,
                            onActiveChange = {},
                            leadingIcon = {
                                Icon(
                                    Icons.Filled.Search,
                                    contentDescription = "Search Games",
                                    modifier = Modifier.size(24.dp)
                                )
                            },
                            placeholder = {
                                Text(text = "Ryujinx", fontSize = 11.sp)
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
                                                    .size(32.dp)
                                                    .clip(CircleShape)
                                            )
                                        } else {
                                            Icon(
                                                Icons.Filled.Person,
                                                contentDescription = "user",
                                                modifier = Modifier.size(24.dp)
                                            )
                                        }
                                }
                            }
                        ) {

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
                        viewModel.filter(query.value)

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
                                        it.titleName?.isNotEmpty() == true && 
                                        (query.value.trim().isEmpty() || 
                                         it.titleName!!.lowercase(Locale.getDefault()).contains(query.value))
                                    }
                                    
                                    if (filteredList.isEmpty()) {
                                        // 没有游戏时显示空状态
                                        Box(
                                            modifier = Modifier.fillMaxSize(),
                                            contentAlignment = Alignment.Center
                                        ) {
                                            CenterFrameBox()
                                            Text(
                                                text = "No games found",
                                                modifier = Modifier.padding(top = 220.dp),
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
                                            // 中央框 - 始终显示
                                            CenterFrameBox()
                                            
                                            // 游戏项目
                                            Row(
                                                modifier = Modifier
                                                    .fillMaxSize()
                                                    .padding(horizontal = 40.dp),
                                                horizontalArrangement = Arrangement.SpaceBetween,
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
                                            
                                            // 导航按钮
                                            Row(
                                                modifier = Modifier
                                                    .fillMaxWidth()
                                                    .padding(horizontal = 16.dp)
                                                    .align(Alignment.CenterVertically),
                                                horizontalArrangement = Arrangement.SpaceBetween
                                            ) {
                                                IconButton(
                                                    onClick = {
                                                        centeredIndex = if (centeredIndex == 0) filteredList.size - 1 else centeredIndex - 1
                                                    },
                                                    modifier = Modifier.size(48.dp)
                                                ) {
                                                    Icon(
                                                        Icons.Filled.ArrowBack,
                                                        contentDescription = "Previous"
                                                    )
                                                }
                                                
                                                IconButton(
                                                    onClick = {
                                                        centeredIndex = if (centeredIndex == filteredList.size - 1) 0 else centeredIndex + 1
                                                    },
                                                    modifier = Modifier.size(48.dp)
                                                ) {
                                                    Icon(
                                                        Icons.Filled.ArrowForward,
                                                        contentDescription = "Next"
                                                    )
                                                }
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
                                            it.titleName?.apply {
                                                if (this.isNotEmpty() && (query.value.trim()
                                                        .isEmpty() || this.lowercase(Locale.getDefault())
                                                        .contains(query.value))
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
                                    }
                                } else {
                                    LazyColumn(
                                        modifier = Modifier.fillMaxSize()
                                    ) {
                                        items(list) {
                                            it.titleName?.apply {
                                                if (this.isNotEmpty() && (query.value.trim()
                                                        .isEmpty() || this.lowercase(
                                                        Locale.getDefault()
                                                    )
                                                        .contains(query.value))
                                                ) {
                                                    Box(modifier = Modifier.animateItemPlacement()) {
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
                                                        shape = CircleShape
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
                                                    .clip(CircleShape)
                                            )
                                        } else {
                                            Icon(
                                                Icons.Filled.Person,
                                                contentDescription = "user"
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
                                                            .clip(CircleShape)
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
                        TextButton(modifier = Modifier.fillMaxWidth(),
                            onClick = {
                                navController?.navigate("settings")
                            }
                        ) {
                            Row(modifier = Modifier.fillMaxWidth()) {
                                Icon(
                                    Icons.Filled.Settings,
                                    contentDescription = "Settings"
                                )
                                Text(
                                    text = "Settings",
                                    modifier = Modifier
                                        .align(Alignment.CenterVertically)
                                        .padding(start = 8.dp)
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
                        val name = viewModel.mainViewModel?.selected?.titleName ?: ""
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
                        shape = MaterialTheme.shapes.large,
                        tonalElevation = AlertDialogDefaults.TonalElevation
                    ) {
                        val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                        val name = viewModel.mainViewModel?.selected?.titleName ?: ""
                        DlcViews.Main(titleId, name, openDlcDialog)
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
                        Row(
                            modifier = Modifier.padding(8.dp),
                            horizontalArrangement = Arrangement.SpaceEvenly
                        ) {
                            if (showAppActions.value) {
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
                                    }
                                }
                            }
                        }
                    }
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
