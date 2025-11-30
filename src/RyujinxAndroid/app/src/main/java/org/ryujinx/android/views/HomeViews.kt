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
                                        .border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.5f), CircleShape)
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
                                    .border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.5f), RoundedCornerShape(16.dp))
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
                                    .border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.5f), RoundedCornerShape(16.dp))
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
                                        MaterialTheme.colorScheme.primary.copy(alpha = 0.5f),
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
                                                Box(modifier = Modifier) {
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
                        val name = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                        DlcViews.Main(titleId, name, openDlcDialog, canClose)
                    }

                }
            }

            if (showAppActions.value) {
                // 获取屏幕配置以确定横竖屏
                val configuration = LocalConfiguration.current
                val isLandscape = configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
                
                // 修复：添加菜单状态变量
                var showAppMenu by remember { mutableStateOf(false) }
                
                // 修复：在横屏模式下使用不同的底部边距
                val bottomPadding = if (isLandscape) 0.dp else 0.dp
                
                ModalBottomSheet(
                    onDismissRequest = {
                        showAppActions.value = false
                        selectedModel.value = null
                        showAppMenu = false // 关闭底部操作菜单时也关闭子菜单
                    },
                    sheetState = sheetState,
                    // 修复：移除透明背景，使用默认的半透明背景
                    // scrimColor = Color.Transparent,
                    modifier = Modifier
                        .fillMaxWidth()
                        .wrapContentHeight()
                        .padding(bottom = bottomPadding)
                ) {
                    // 使用可滚动的LazyColumn替代固定的Column
                    LazyColumn(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                    ) {
                        item {
                            // 显示游戏名和版本号
                            selectedModel.value?.let { game ->
                                Column(
                                    modifier = Modifier.fillMaxWidth()
                                ) {
                                    Text(
                                        text = game.getDisplayName(),
                                        fontSize = 20.sp,
                                        fontWeight = FontWeight.Bold,
                                        modifier = Modifier.align(Alignment.CenterHorizontally),
                                        textAlign = TextAlign.Center
                                    )
                                    if (!game.version.isNullOrEmpty()) {
                                        Text(
                                            text = "v${game.version}",
                                            fontSize = 16.sp,
                                            modifier = Modifier.align(Alignment.CenterHorizontally),
                                            textAlign = TextAlign.Center
                                        )
                                    }
                                    Spacer(modifier = Modifier.height(16.dp))
                                }
                            }
                        }
                        
                        item {
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
                                    // 只显示播放图标，不显示文字
                                    Icon(
                                        Icons.Filled.PlayArrow,
                                        contentDescription = "Run",
                                        modifier = Modifier.size(28.dp)
                                    )
                                }
                                
                                // 修复：将菜单按钮状态移到此处
                                Box {
                                    IconButton(onClick = {
                                        // 修复：正确切换菜单状态
                                        showAppMenu = !showAppMenu
                                    }) {
                                        // 只显示菜单图标，不显示文字
                                        Icon(
                                            Icons.Filled.Menu,
                                            contentDescription = "Menu",
                                            modifier = Modifier.size(28.dp)
                                        )
                                    }
                                    
                                    // 修复：简化下拉菜单，移除LazyColumn
                                    DropdownMenu(
                                        expanded = showAppMenu,
                                        onDismissRequest = { showAppMenu = false },
                                        modifier = Modifier
                                            .width(200.dp) // 固定宽度避免过长
                                    ) {
                                        // 修复：使用简单的Column而不是LazyColumn
                                        Column {
                                            DropdownMenuItem(
                                                text = { Text("重命名游戏") },
                                                onClick = {
                                                    showAppMenu = false
                                                    selectedModel.value?.let { game ->
                                                        newGameName = game.getDisplayName()
                                                        showRenameDialog = true
                                                    }
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("清除 PPTC 缓存") },
                                                onClick = {
                                                    showAppMenu = false
                                                    viewModel.mainViewModel?.clearPptcCache(
                                                        viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    )
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("清除着色器缓存") },
                                                onClick = {
                                                    showAppMenu = false
                                                    viewModel.mainViewModel?.purgeShaderCache(
                                                        viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    )
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("删除所有缓存") },
                                                onClick = {
                                                    showAppMenu = false
                                                    viewModel.mainViewModel?.deleteCache(
                                                        viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    )
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理更新") },
                                                onClick = {
                                                    showAppMenu = false
                                                    openTitleUpdateDialog.value = true
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理 DLC") },
                                                onClick = {
                                                    showAppMenu = false
                                                    openDlcDialog.value = true
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理金手指") },
                                                onClick = {
                                                    showAppMenu = false
                                                    val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    val gamePath = viewModel.mainViewModel?.selected?.path ?: ""
                                                    navController?.navigate("cheats/$titleId?gamePath=${android.net.Uri.encode(gamePath)}")
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理存档数据") },
                                                onClick = {
                                                    showAppMenu = false
                                                    val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""
                                                    val gameName = viewModel.mainViewModel?.selected?.getDisplayName() ?: ""
                                                    navController?.navigate("savedata/$titleId?gameName=${android.net.Uri.encode(gameName)}")
                                                }
                                            )
                                            DropdownMenuItem(
                                                text = { Text("管理 Mods") },
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
        }

        @Preview
        @Composable
        fun HomePreview() {
            Home(isPreview = true)
        }
    }
}}}
