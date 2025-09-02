package org.ryujinx.android.views

import androidx.compose.runtime.Composable
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import org.ryujinx.android.viewmodels.MainViewModel
import org.ryujinx.android.viewmodels.ModViewModel
import org.ryujinx.android.viewmodels.SettingsViewModel

class MainView {
    companion object {
        @Composable
        fun Main(mainViewModel: MainViewModel) {
            val navController = rememberNavController()
            mainViewModel.navController = navController

            NavHost(navController = navController, startDestination = "home") {
                composable("home") { HomeViews.Home(mainViewModel.homeViewModel, navController) }
                composable("user") { UserViews.Main(mainViewModel) }
                composable("game") { GameViews.Main() }
                composable("settings") {
                    SettingViews.Main(
                        SettingsViewModel(
                            navController,
                            mainViewModel.activity
                        ), mainViewModel
                    )
                }
                // 添加mods路由 - 使用正确的路径结构 mods/contents/{titleId}
                composable("mods/contents/{titleId}") { backStackEntry ->
                    val titleId = backStackEntry.arguments?.getString("titleId") ?: ""
                    ModView(viewModel = ModViewModel(titleId), titleId = titleId)
                }
            }
        }
    }
}
