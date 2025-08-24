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
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.style.TextOverflow
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

        @OptIn(ExperimentalFoundationApi::class)
        @Composable
        fun LandscapeGameItem(
            gameModel: GameModel,
            viewModel: HomeViewModel,
            showAppActions: MutableState<Boolean>,
            showLoading: MutableState<Boolean>,
            selectedModel: MutableState<GameModel?>,
            showError: MutableState<String>,
            isCentered: Boolean = false,
            scale: Float = 1f,
            alpha: Float = 1f,
            onCentered: () -> Unit = {}
        ) {
            remember {
                selectedModel
            }
            val isSelected = selectedModel.value == gameModel

            val decoder = Base64.getDecoder()
            
            Surface(
                shape = MaterialTheme.shapes.large,
                color = MaterialTheme.colorScheme.surface,
                modifier = Modifier
                    .width((220 * scale).dp)
                    .graphicsLayer {
                        this.scaleX = scale
                        this.scaleY = scale
                        this.alpha = alpha
                    }
                    .combinedClickable(
                        onClick = {
                            if (isCentered) {
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
                            } else {
                                onCentered()
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
                        .padding(8.dp)
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
                            .padding(vertical = 8.dp)
                            .basicMarquee(),
                        fontSize = if (isCentered) 16.sp else 14.sp,
                        color = if (isCentered) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface
                    )
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
            var centeredItem by remember { mutableStateOf<GameModel?>(null) }
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
                                Text(text = "Ryujinx", fontSize = 14.sp)
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
                                    // 横屏模式：横向堆叠式列表
                                    val listState = rememberLazyListState()
                                    val density = LocalDensity.current
                                    val screenWidthPx: Float = with(density) { configuration.screenWidthDp.dp.toPx() }
                                    val itemWidth = 220.dp
                                    val itemSpacing = 16.dp
                                    val itemWidthPx = with(density) { itemWidth.toPx() }
                                    val itemSpacingPx = with(density) { itemSpacing.toPx() }
                                    
                                    // 计算居中范围 - 减去左右两个空白项目加上四个间隔
                                    val centerRangePx = screenWidthPx / 2f - (itemWidthPx + itemSpacingPx * 2f)
                                    
                                    val centeredIndex = remember { derivedStateOf {
                                        val layoutInfo = listState.layoutInfo
                                        if (layoutInfo.visibleItemsInfo.isEmpty()) {
                                            return@derivedStateOf -1
                                        }
                                        
                                        val viewportCenter = layoutInfo.viewportStartOffset + layoutInfo.viewportSize.width / 2f
                                        var closestIndex = -1
                                        var minDistance = Float.MAX_VALUE
                                        
                                        for (item in layoutInfo.visibleItemsInfo) {
                                            val itemCenter = item.offset + item.size / 2f
                                            val distance = abs(itemCenter - viewportCenter)
                                            
                                            if (distance < minDistance) {
                                                minDistance = distance
                                                closestIndex = item.index
                                            }
                                        }
                                        
                                        closestIndex
                                    }}

                                    // 自动设置中央项
                                    LaunchedEffect(centeredIndex.value) {
                                        if (centeredIndex.value != -1 && centeredIndex.value < list.size) {
                                            centeredItem = list[centeredIndex.value]
                                        }
                                    }

                                    val coroutineScope = rememberCoroutineScope()
                                    
                                    // 创建带有空白项目的列表
                                    val listWithPadding = remember(list) {
                                        if (list.isEmpty()) {
                                            list
                                        } else {
                                            // 在列表前后各添加一个空白项目
                                            listOf(null) + list + listOf(null)
                                        }
                                    }

                                    // 计算内容填充，确保可以滚动到两端
                                    val contentPadding = remember {
                                        val extraSpacePx = (screenWidthPx / 2f - itemWidthPx / 2f - itemSpacingPx * 2f)
                                        val extraSpaceDp = with(density) { extraSpacePx.toDp() }
                                        PaddingValues(
                                            start = extraSpaceDp,
                                            end = extraSpaceDp
                                        )
                                    }

                                    // 初始滚动到第二个项目（第一个是空白项目）
                                    LaunchedEffect(Unit) {
                                        if (listWithPadding.isNotEmpty() && isInitialLoad) {
                                            kotlinx.coroutines.delay(100)
                                            listState.scrollToItem(1)
                                            isInitialLoad = false
                                        }
                                    }

                                    LazyRow(
                                        state = listState,
                                        modifier = Modifier
                                            .fillMaxSize()
                                            .nestedScroll(nestedScrollConnection)
                                            .padding(top = 8.dp),
                                        horizontalArrangement = Arrangement.spacedBy(itemSpacing),
                                        verticalAlignment = Alignment.CenterVertically,
                                        contentPadding = contentPadding
                                    ) {
                                        itemsIndexed(items = listWithPadding) { index, item ->
                                            if (item != null) {
                                                item.titleName?.apply {
                                                    if (this.isNotEmpty() && (query.value.trim()
                                                            .isEmpty() || this.lowercase(Locale.getDefault())
                                                            .contains(query.value))
                                                    ) {
                                                        // 获取项目布局信息
                                                        val itemInfo = listState.layoutInfo.visibleItemsInfo.find { it.index == index }
                                                        
                                                        // 计算项目距离中心的距离和比例
                                                        val distance = if (itemInfo != null) {
                                                            val itemCenter = itemInfo.offset + itemInfo.size / 2f
                                                            val viewportCenter = listState.layoutInfo.viewportStartOffset + 
                                                                               listState.layoutInfo.viewportSize.width / 2f
                                                            abs(itemCenter - viewportCenter)
                                                        } else {
                                                            centerRangePx * 2f // 如果不可见，设为最大值
                                                        }
                                                        
                                                        // 计算缩放比例和透明度
                                                        val scale = if (distance < centerRangePx) {
                                                            // 在居中范围内，根据距离计算缩放
                                                            1.2f - (distance / centerRangePx) * 0.4f
                                                        } else {
                                                            // 超出居中范围，使用最小缩放
                                                            0.8f
                                                        }
                                                        
                                                        val alpha = if (distance < centerRangePx * 1.5f) {
                                                            // 在可见范围内，根据距离计算透明度
                                                            1f - (distance / (centerRangePx * 1.5f)) * 0.3f
                                                        } else {
                                                            // 超出可见范围，使用最小透明度
                                                            0.7f
                                                        }
                                                        
                                                        val isCentered = distance < centerRangePx / 2f
                                                        
                                                        // 使用动画使变化更平滑
                                                        val animatedScale by animateFloatAsState(
                                                            targetValue = scale,
                                                            label = "scaleAnimation"
                                                        )
                                                        val animatedAlpha by animateFloatAsState(
                                                            targetValue = alpha,
                                                            label = "alphaAnimation"
                                                        )
                                                        
                                                        Box(
                                                            modifier = Modifier
                                                                .zIndex(if (isCentered) 10f else 1f - (distance / centerRangePx))
                                                        ) {
                                                            LandscapeGameItem(
                                                                gameModel = item,
                                                                viewModel = viewModel,
                                                                showAppActions = showAppActions,
                                                                showLoading = showLoading,
                                                                selectedModel = selectedModel,
                                                                showError = showError,
                                                                isCentered = isCentered,
                                                                scale = animatedScale,
                                                                alpha = animatedAlpha,
                                                                onCentered = { 
                                                                    centeredItem = item
                                                                    coroutineScope.launch {
                                                                        listState.animateScrollToItem(index)
                                                                    }
                                                                }
                                                            )
                                                        }
                                                    }
                                                }
                                            } else {
                                                // 渲染空白项目
                                                Box(
                                                    modifier = Modifier
                                                        .width(itemWidth)
                                                        .aspectRatio(1f)
                                                        .clickable(enabled = false) {}
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
}}}
