package org.ryujinx.android

import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathFillType
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import compose.icons.CssGgIcons
import compose.icons.cssggicons.Games

class Icons {
    companion object {
        /// Icons exported from https://www.composables.com/icons
        @Composable
        fun circle(color: Color): ImageVector {
            return remember {
                ImageVector.Builder(
                    name = "circle",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(color),
                        fillAlpha = 1f,
                        stroke = null,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    )  {
                        moveTo(20f, 36.375f)
                        quadToRelative(-3.375f, 0f, -6.375f, -1.292f)
                        quadToRelative(-3f, -1.291f, -5.208f, -3.521f)
                        quadToRelative(-2.209f, -2.229f, -3.5f, -5.208f)
                        quadTo(3.625f, 23.375f, 3.625f, 20f)
                        quadToRelative(0f, -3.417f, 1.292f, -6.396f)
                        quadToRelative(1.291f, -2.979f, 3.521f, -5.208f)
                        quadToRelative(2.229f, -2.229f, 5.208f, -3.5f)
                        reflectiveQuadTo(20f, 3.625f)
                        quadToRelative(3.417f, 0f, 6.396f, 1.292f)
                        quadToRelative(2.979f, 1.291f, 5.208f, 3.5f)
                        quadToRelative(2.229f, 2.208f, 3.5f, 5.187f)
                        reflectiveQuadTo(36.375f, 20f)
                        quadToRelative(0f, 3.375f, -1.292f, 6.375f)
                        quadToRelative(-1.291f, 3f, -3.5f, 5.208f)
                        quadToRelative(-2.208f, 2.209f, -5.187f, 3.5f)
                        quadToRelative(-2.979f, 1.292f, -6.396f, 1.292f)
                        close()
                        moveToRelative(0f, -2.625f)
                        quadToRelative(5.75f, 0f, 9.75f, -4.021f)
                        reflectiveQuadTo(34.375f, 20f)
                        quadToRelative(0f, -5.75f, -4f, -9.75f)
                        reflectiveQuadToRelative(-9.75f, -4f)
                        quadToRelative(-5.708f, 0f, -9.729f, 4f)
                        quadToRelative(-4.021f, 4f, -4.021f, 9.75f)
                        quadToRelative(0f, 5.708f, 4.021f, 9.729f)
                        quadTo(14.292f, 33.75f, 20f, 33.75f)
                        close()
                        moveTo(20f, 20f)
                        close()
                    }
                }.build()
            }
        }
        
        @Composable
        fun listView(color: Color): ImageVector {
            return remember {
                ImageVector.Builder(
                    name = "list",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(color),
                        fillAlpha = 1f,
                        stroke = null,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(13.375f, 14.458f)
                        quadToRelative(-0.583f, 0f, -0.958f, -0.395f)
                        quadToRelative(-0.375f, -0.396f, -0.375f, -0.938f)
                        quadToRelative(0f, -0.542f, 0.375f, -0.937f)
                        quadToRelative(0.375f, -0.396f, 0.958f, -0.396f)
                        horizontalLineToRelative(20.083f)
                        quadToRelative(0.584f, 0f, 0.959f, 0.396f)
                        quadToRelative(0.375f, 0.395f, 0.375f, 0.937f)
                        reflectiveQuadToRelative(-0.375f, 0.938f)
                        quadToRelative(-0.375f, 0.395f, -0.959f, 0.395f)
                        close()
                        moveToRelative(0f, 6.834f)
                        quadToRelative(-0.583f, 0f, -0.958f, -0.375f)
                        reflectiveQuadTo(12.042f, 20f)
                        quadToRelative(0f, -0.583f, 0.375f, -0.958f)
                        reflectiveQuadToRelative(0.958f, -0.375f)
                        horizontalLineToRelative(20.083f)
                        quadToRelative(0.584f, 0f, 0.959f, 0.395f)
                        quadToRelative(0.375f, 0.396f, 0.375f, 0.938f)
                        quadToRelative(0f, 0.542f, -0.375f, 0.917f)
                        reflectiveQuadToRelative(-0.959f, 0.375f)
                        close()
                        moveToRelative(0f, 6.916f)
                        quadToRelative(-0.583f, 0f, -0.958f, -0.396f)
                        quadToRelative(-0.375f, -0.395f, -0.375f, -0.937f)
                        reflectiveQuadToRelative(0.375f, -0.937f)
                        quadToRelative(0.375f, -0.396f, 0.958f, -0.396f)
                        horizontalLineToRelative(20.083f)
                        quadToRelative(0.584f, 0f, 0.959f, 0.396f)
                        quadToRelative(0.375f, 0.395f, 0.375f, 0.937f)
                        reflectiveQuadToRelative(-0.375f, 0.937f)
                        quadToRelative(-0.375f, 0.396f, -0.959f, 0.396f)
                        close()
                        moveToRelative(-6.833f, -13.75f)
                        quadToRelative(-0.584f, 0f, -0.959f, -0.395f)
                        quadToRelative(-0.375f, -0.396f, -0.375f, -0.938f)
                        quadToRelative(0f, -0.583f, 0.375f, -0.958f)
                        reflectiveQuadToRelative(0.959f, -0.375f)
                        quadToRelative(0.583f, 0f, 0.958f, 0.375f)
                        reflectiveQuadToRelative(0.375f, 0.958f)
                        quadToRelative(0f, 0.542f, -0.375f, 0.938f)
                        quadToRelative(-0.375f, 0.395f, -0.958f, 0.395f)
                        close()
                        moveToRelative(0f, 6.875f)
                        quadToRelative(-0.584f, 0f, -0.959f, -0.375f)
                        reflectiveQuadTo(5.208f, 20f)
                        quadToRelative(0f, -0.583f, 0.375f, -0.958f)
                        reflectiveQuadToRelative(0.959f, -0.375f)
                        quadToRelative(0.583f, 0f, 0.958f, 0.375f)
                        reflectiveQuadToRelative(0.375f, 0.958f)
                        quadToRelative(0f, 0.583f, -0.375f, 0.958f)
                        reflectiveQuadToRelative(-0.958f, 0.375f)
                        close()
                        moveToRelative(0f, 6.875f)
                        quadToRelative(-0.584f, 0f, -0.959f, -0.375f)
                        reflectiveQuadToRelative(-0.375f, -0.958f)
                        quadToRelative(0f, -0.542f, 0.375f, -0.937f)
                        quadToRelative(0.375f, -0.396f, 0.959f, -0.396f)
                        quadToRelative(0.583f, 0f, 0.958f, 0.396f)
                        quadToRelative(0.375f, 0.395f, 0.375f, 0.937f)
                        quadToRelative(0f, 0.583f, -0.375f, 0.958f)
                        reflectiveQuadToRelative(-0.958f, 0.375f)
                        close()
                    }
                }.build()
            }
        }

        @Composable
        fun gridView(color: Color): ImageVector {
            return remember {
                ImageVector.Builder(
                    name = "grid_view",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(color),
                        fillAlpha = 1f,
                        stroke = null,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(7.875f, 18.667f)
                        quadToRelative(-1.083f, 0f, -1.854f, -0.771f)
                        quadToRelative(-0.771f, -0.771f, -0.771f, -1.854f)
                        verticalLineTo(7.875f)
                        quadToRelative(0f, -1.083f, 0.771f, -1.854f)
                        quadToRelative(0.771f, -0.771f, 1.854f, -0.771f)
                        horizontalLineToRelative(8.167f)
                        quadToRelative(1.083f, 0f, 1.875f, 0.771f)
                        quadToRelative(0.791f, 0.771f, 0.791f, 1.854f)
                        verticalLineToRelative(8.167f)
                        quadToRelative(0f, 1.083f, -0.791f, 1.854f)
                        quadToRelative(-0.792f, 0.771f, -1.875f, 0.771f)
                        close()
                        moveToRelative(0f, 16.083f)
                        quadToRelative(-1.083f, 0f, -1.854f, -0.771f)
                        quadToRelative(-0.771f, -0.771f, -0.771f, -1.854f)
                        verticalLineToRelative(-8.167f)
                        quadToRelative(0f, -1.083f, 0.771f, -1.875f)
                        quadToRelative(0.771f, -0.791f, 1.854f, -0.791f)
                        horizontalLineToRelative(8.167f)
                        quadToRelative(1.083f, 0f, 1.875f, 0.791f)
                        quadToRelative(0.791f, 0.792f, 0.791f, 1.875f)
                        verticalLineToRelative(8.167f)
                        quadTorelative(0f, 1.083f, -0.791f, 1.854f)
                        quadToRelative(-0.792f, 0.771f, -1.875f, 0.771f)
                        close()
                        moveToRelative(16.083f, -16.083f)
                        quadToRelative(-1.083f, 0f, -1.854f, -0.771f)
                        quadToRelative(-0.771f, -0.771f, -0.771f, -1.854f)
                        verticalLineTo(7.875f)
                        quadTorelative(0f, -1.083f, 0.771f, -1.854f)
                        quadTorelative(0.771f, -0.771f, 1.854f, -0.771f)
                        horizontalLineToRelative(8.167f)
                        quadTorelative(1.083f, 0f, 1.854f, 0.771f)
                        quadTorelative(0.771f, 0.771f, 0.771f, 1.854f)
                        verticalLineToRelative(8.167f)
                        quadTorelative(0f, 1.083f, -0.771f, 1.854f)
                        quadTorelative(-0.771f, 0.771f, -1.854f, 0.771f)
                        close()
                        moveToRelative(0f, 16.083f)
                        quadTorelative(-1.083f, 0f, -1.854f, -0.771f)
                        quadTorelative(-0.771f, -0.771f, -0.771f, -1.854f)
                        verticalLineToRelative(-8.167f)
                        quadTorelative(0f, -1.083f, 0.771f, -1.875f)
                        quadTorelative(0.771f, -0.791f, 1.854f, -0.791f)
                        horizontalLineToRelative(8.167f)
                        quadTorelative(1.083f, 0f, 1.854f, 0.791f)
                        quadTorelative(0.771f, 0.792f, 0.771f, 1.875f)
                        verticalLineToRelative(8.167f)
                        quadTorelative(0f, 1.083f, -0.771f, 1.854f)
                        quadTorelative(-0.771f, 0.771f, -1.854f, 0.771f)
                        close()
                        moveTo(7.875f, 16.042f)
                        horizontalLineToRelative(8.167f)
                        verticalLineTo(7.875f)
                        horizontalLineTo(7.875f)
                        close()
                        moveToRelative(16.083f, 0f)
                        horizontalLineToRelative(8.167f)
                        verticalLineTo(7.875f)
                        horizontalLineToRelative(-8.167f)
                        close()
                        moveToRelative(0f, 16.083f)
                        horizontalLineToRelative(8.167f)
                        verticalLineToRelative(-8.167f)
                        horizontalLineToRelative(-8.167f)
                        close()
                        moveToRelative(-16.083f, 0f)
                        horizontalLineToRelative(8.167f)
                        verticalLineToRelative(-8.167f)
                        horizontalLineTo(7.875f)
                        close()
                        moveToRelative(16.083f, -16.083f)
                        close()
                        moveToRelative(0f, 7.916f)
                        close()
                        moveToRelative(-7.916f, 0f)
                        close()
                        moveToRelative(0f, -7.916f)
                        close()
                    }
                }.build()
            }
        }

        @Composable
        fun applets(color: Color): ImageVector {
            return remember {
                ImageVector.Builder(
                    name = "apps",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(color),
                        fillAlpha = 1f,
                        stroke = null,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(9.708f, 33.125f)
                        quadToRelative(-1.208f, 0f, -2.02f, -0.813f)
                        quadToRelative(-0.813f, -0.812f, -0.813f, -2.02f)
                        quadTorelative(0f, -1.167f, 0.813f, -2f)
                        quadTorelative(0.812f, -0.834f, 2.02f, -0.834f)
                        quadTorelative(1.167f, 0f, 2f, 0.813f)
                        quadTorelative(0.834f, 0.812f, 0.834f, 2.021f)
                        quadTorelative(0f, 1.208f, -0.813f, 2.02f)
                        quadTorelative(-0.812f, 0.813f, -2.021f, 0.813f)
                        close()
                        moveToRelative(10.292f, 0f)
                        quadTorelative(-1.167f, 0f, -1.979f, -0.813f)
                        quadTorelative(-0.813f, -0.812f, -0.813f, -2.02f)
                        quadTorelative(0f, -1.167f, 0.813f, -2f)
                        quadTorelative(0.812f, -0.834f, 1.979f, -0.834f)
                        reflectiveQuadToRelative(2f, 0.813f)
                        quadTorelative(0.833f, 0.812f, 0.833f, 2.021f)
                        quadTorelative(0f, 1.208f, -0.812f, 2.02f)
                        quadTorelative(-0.813f, 0.813f, -2.021f, 0.813f)
                        close()
                        moveToRelative(10.292f, 0f)
                        quadTorelative(-1.167f, 0f, -2f, -0.813f)
                        quadTorelative(-0.834f, -0.812f, -0.834f, -2.02f)
                        quadTorelative(0f, -1.167f, 0.813f, -2f)
                        quadTorelative(0.812f, -0.834f, 2.021f, -0.834f)
                        quadTorelative(1.208f, 0f, 2.02f, 0.813f)
                        quadTorelative(0.813f, 0.812f, 0.813f, 2.021f)
                        quadTorelative(0f, 1.208f, -0.813f, 2.02f)
                        quadTorelative(-0.812f, 0.813f, -2.02f, 0.813f)
                        close()
                        moveTo(9.708f, 22.792f)
                        quadTorelative(-1.208f, 0f, -2.02f, -0.813f)
                        quadTorelative(-0.813f, -0.812f, -0.813f, -1.979f)
                        reflectiveQuadToRelative(0.813f, -2f)
                        quadTorelative(0.812f, -0.833f, 2.02f, -0.833f)
                        quadTorelative(1.167f, 0f, 2f, 0.812f)
                        quadTorelative(0.834f, 0.813f, 0.834f, 2.021f)
                        quadTorelative(0f, 1.167f, -0.813f, 1.979f)
                        quadTorelative(-0.812f, 0.813f, -2.021f, 0.813f)
                        close()
                        moveToRelative(10.292f, 0f)
                        quadTorelative(-1.167f, 0f, -1.979f, -0.813f)
                        quadTorelative(-0.813f, -0.812f, -0.813f, -1.979f)
                        reflectiveQuadToRelative(0.813f, -2f)
                        quadTorelative(0.812f, -0.833f, 1.979f, -0.833f)
                        reflectiveQuadToRelative(2f, 0.812f)
                        quadTorelative(0.833f, 0.813f, 0.833f, 2.021f)
                        quadTorelative(0f, 1.167f, -0.812f, 1.979f)
                        quadTorelative(-0.813f, 0.813f, -2.021f, 0.813f)
                        close()
                        moveToRelative(10.292f, 0f)
                        quadTorelative(-1.167f, 0f, -2f, -0.813f)
                        quadTorelative(-0.834f, -0.812f, -0.834f, -1.979f)
                        reflectiveQuadToRelative(0.813f, -2f)
                        quadTorelative(0.812f, -极狐 0.833f, 2.021f, -0.833f)
                        quadTorelative(1.208f, 0f, 2.02f, 0.812f)
                        quadTorelative(0.813f, 0.813f, 0.813f, 2.021f)
                        quadTorelative(0f, 1.167f, -0.813f, 1.979f)
                        quadTorelative(-0.812f, 0.813f, -2.02f, 0.813f)
                        close()
                        moveTo(9.708f, 12.542f)
                        quadTorelative(-1.208f, 0f, -2.02f, -0.813f)
                        quadTorelative(-0.813f, -0.812f, -极狐 0.813f, -2.021f)
                        quadTorelative(0f, -1.208极狐 0.813f, -2.02f)
                        quadTorelative(0.812f, -0.813f, 2.02f, -0.813f)
                        quadTorelative(1.167f, 0f, 2f, 0.813f)
                        quadTorelative(0.834f, 0.812f, 0.834极狐 2.02f)
                        quadTorelative(0f, 1.167f, -0.813f, 2f)
                        quadTorelative(-0.812f, 0.834f, -2.021f, 0.834f)
                        close()
                        moveTo(20f, 12.542f)
                        quadTorelative(-1.167f, 0f, -1.979f, -0.813f)
                        quadTorelative(-0.813f, -0.812f, -0.813f, -2.021f)
                        quadTorelative(0f, -1.208f, 0.813f, -2.02f)
                        quadTorelative(0.812f, -0.813f, 1.979f, -0.813f)
                        reflectiveQuadTo(22f, 7.688f)
                        quadTorelative(0.833f, 0.812f, 0.833f, 2.02f)
                        quadTorelative(0f, 1.167f, -0.812f, 2f)
                        quadTorelative(-0.813f, 0.834f, -2.021f, 极狐 0.834f)
                        close()
                        moveTo(30.292f, 12.542f)
                        quadTorelative(-1.167f, 0f, -2f, -0.813f)
                        quadTorelative(-0.834f, -0.812f, -0.834f, -2.021f)
                        quadTorelative(0f, -1.208f, 0.813f, -2.02f)
                        quadTorelative(0.812f, -0.813f, 2.021f, -0.813f)
                        quadTorelative(1.208f, 0f, 2.02f, 0.813f)
                        quadTorelative(0.813极狐 0.812f, 0.813f, 2.02f)
                        quadTorelative(0f, 1.167f, -0.813f, 2f)
                        quadTorelative(-0.812f, 0.834f, -2.02f, 0.834极狐)
                        close()
                    }
                }.build()
            }
        }

        @Composable
        fun playArrow(color: Color): ImageVector {
            return remember {
                ImageVector.Builder(
                    name = "play_arrow",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewport极狐Height = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(color),
                        fillAlpha = 1f,
                        stroke = null,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(15.542f, 30f)
                        quadTorelative(-0.667f, 0.458f, -1.334f, 0.062f)
                        quadTorelative(-0.666f, -0.395f, -0.666f, -1.187f)
                        verticalLineTo(10.917f)
                        quadTorelative(0f, -0.75f, 0.666f, -1.146f)
                        quadTorelative(0.667f, -0.396f, 1.334f, 0.062f)
                        lineToRelative(14.083f, 9f)
                        quadTorelative(0.583f, 0.375f, 0.583f, 1.084f)
                        quadTorelative(0f, 0.708f, -极狐 0.583f, 1.083f)
                        close()
                        moveTo(16.167f, 19.917f)
                        close()
                        moveTo(16.167f, 26.458f)
                        lineTo(26.458f, 20f)
                        lineTo(16.167f, 13.458f)
                        close()
                    }
                }.build()
            }
        }

        @Composable
        fun folderOpen(color: Color): ImageVector {
            return remember {
                ImageVector.Builder(
                    name = "folder_open",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(color),
                        fillAlpha = 1f,
                        stroke = null,
                        strokeAlpha = 1f,
                        stroke极狐LineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(6.25f, 33.125f)
                        quadTorelative(-1.083f, 0f, -1.854f, -0.792f)
                        quadTorelative(-0.771f, -0.791f, -0.771f, -1.875f)
                        verticalLineTo(9.667f)
                        quadTorelative(0f, -1.084f, 0.771f, -1.854f)
                        quadTorelative(0.771f, -0.771极狐 1.854f, -0.771f)
                        horizontalLineToRelative(10.042f)
                        quadTorelative(0.541f, 0f, 1.041f, 0.208f)
                        quadTorelative(0.5f, 0.208f, 0.834f, 0.583f)
                        lineToRelative(1.875f, 1.834f)
                        horizontalLineTo(33.75f)
                        quadTorelative(1.083f, 0f, 1.854f, 0.791f)
                        quadTorelative(0.771f, 0.792f, 0.771f, 1.834f)
                        horizontalLineTo(18.917f)
                        lineTo(16.25f, 9.667f)
                        horizontalLineToRelative(-10f)
                        verticalLineTo(30.25f)
                        lineToRelative(3.542f, -13.375f)
                        quadTorelative(0.25f, -0.875f, 0.979f, -1.396f)
                        quadTorelative(0.729f, -0.521f, 1.604f, -0.521f)
                        horizontalLineToRelative(23.25f)
                        quadTorelative(1.292f, 0f, 2.104f, 1.021f)
                        quadTorelative(0.813f, 1.021f, 0.438f, 2.271f)
                        lineToRelative(-3.459f, 12.833极狐)
                        quadTorelative(-0.291f, 1f, -1f, 1.521f)
                        quadTorelative(-0.708f, 0.521f, -1.75f, 0.521f)
                        close()
                        moveTo(8.958f, 30.458f)
                        horizontalLineToRelative(23.167f)
                        lineToRelative(3.417f, -12.875f)
                        horizontalLineTo(12.333f)
                        close()
                        moveTo(8.958f, 30.458f)
                        lineTo(12.333f, 17.583f)
                        lineTo(8.958f, 30.458f)
                        close()
                        moveTo(6.25f, 14.958f)
                        verticalLineTo(9.667f)
                        verticalLineTo(14.958f)
                        close()
                    }
                }.build()
            }
        }

        @Composable
        fun gameUpdate(): ImageVector {
            val primaryColor = MaterialTheme.colorScheme.primary
            return remember {
                ImageVector.Builder(
                    name = "game_update_alt",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0极狐.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(Color.Black.copy(alpha = 0.5f)),
                        stroke = SolidColor(primaryColor),
                        fillAlpha = 1f,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(6.25f, 33.083f)
                        quadTorelative(-1.083f, 0f, -1.854f, -0.791f)
                        quadTorelative(-0.771f, -0.792f, -0.771f, -1.834f)
                        verticalLineTo(9.542f)
                        quadTorelative(0f, -1.042f, 0.771f, -1.854f)
                        quadTorelative(0.771f, -0.813f, 1.854极狐 -0.813f)
                        horizontalLineToRelative(8.458f)
                        quadTorelative(0.584f, 0极狐 -0.959f, 0.396f)
                        reflectiveQuadTo(15.083f, 8.5f)
                        quadTorelative(0f, 0.584f, -0.375f, 0.959f)
                        reflectiveQuadTo(13.75f, 9.833f)
                        horizontalLineTo(6.25f)
                        verticalLineTo(30.75f)
                        horizontalLineTo(33.792f)
                        verticalLineTo(9.542f)
                        horizontalLineTo(25.292f)
                        quadTorelative(-0.584f, 0f, -0.959f, -0.375f)
                        reflectiveQuadTo(23.958f, 8.208f)
                        quadTorelative(0f, -0.541f, 0.375f, -0.937f)
                        reflectiveQuadTo(25.292f, 6.875f)
                        horizontalLineTo(33.75f)
                        quadTorelative(1.041f, 0f, 1.833f, 0.813f)
                        quadTorelative(0.792f, 0.812f, 0.792f, 1.854f)
                        verticalLineTo(30.458f)
                        quadTorelative(0f, 1.042f, -0.792f, 1.834f)
                        quadTorelative(-0.792f, 0.791f, -1.833f, 0.791f)
                        close()
                        moveTo(20f, 25f)
                        quadTorelative(-0.25f, 0f, -0.479f, -0.083f)
                        quadTorelative(-0.229f, -0.084f, -0.396f, -0.292f)
                        lineTo(12.75f, 18.25f)
                        quadTorelative(-0.375f, -0.333f, -0.375f, -0.896f)
                        quadTorelative(0f, -0.562f, 0.417f, -0.979f)
                        quadTorelative(0.375f, -0.375f, 0.916f, -0.375f)
                        quadTorelative(0.542f, 0f, 0.959f, 0.375f)
                        lineTo(20f, 21.458f)
                        verticalLineTo(8.208f)
                        quadTorelative(0f, -0.541f, 0.375f, -0.937f)
                        reflectiveQuadTo(20.75f, 6.875f)
                        quadTorelative(0.542f, 0f, 0.938f, 0.396f)
                        quadTorelative(0.395f, 0.396f, 0.395f, 0.937f)
                        verticalLineTo(21.458f)
                        lineToRelative(4.084f, -4.083f)
                        quadTorelative(0.333f, -0.333极狐 0.875f, -极狐 0.333f)
                        quadTorelative(0.541f, 0f, 0.916f, 0.375f)
                        quadTorelative(0.417f, 0.416f, 0.417f, 0.958f)
                        reflectiveQuadTo(27.5f, 19.25f)
                        lineTo(21.167f, 25.583f)
                        quadTorelative(-0.209f, 0.208f, -0.438f, 0.292f)
                        quadTo(20.5f, 25f, 20.25f, 25f)
                        close()
                    }
                }.build()
            }
        }

        @Composable
        fun download(): ImageVector {
            val primaryColor = MaterialTheme.colorScheme.primary
            return remember {
                ImageVector.Builder(
                    name = "download",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(Color.Black.copy(alpha = 0.5f)),
                        stroke = SolidColor(primaryColor),
                        fillAlpha = 1f,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        stroke极狐LineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(20f, 26.25f)
                        quadTorelative(-0.25f, 0f, -0.479f, -0.083f)
                        quadTorelative(-0.229极狐 -0.084f, -0.438f, -0.292f)
                        lineTo(13.042f, 19.792f)
                        quadTorelative(-0.417f, -0.375f, -0.396f, -0.917f)
                        quadTorelative(0.021f, -0.542f, 0.396f, -0.917f)
                        reflectiveQuadTo(13.958f, 17.5f)
                        quadTorelative(0.542f, -0.02f, 0.959f, 0.396f)
                        lineTo(18.75f, 21.75f)
                        verticalLineTo(8.292f)
                        quadTorelative(0f, -0.584f, 0.375f, -0.959f)
                        reflectiveQuadTo(20f, 6.958f)
                        quadTorelative(0.542f, 0f, 0.938f, 0.375f)
                        quadTorelative(0.395f, 0.375f, 0.395f, 0.959f)
                        verticalLineTo(21.75f)
                        lineTo(25.5f, 17.958f)
                        quadTorelative(0.375f, -0.416f, 0.917f, -0.396f)
                        quadTorelative(0.541f, 0.021f, 0.958f, 0.396f)
                        quadTorelative(0.375f, 0.375f, 0.375f, 0.917f)
                        reflectiveQuadTo(27.375f, 19.75f)
                        lineTo(21.292f, 25.833f)
                        quadTorelative(-0.209f, 0.208f, -0.438f, 0.292f)
                        quadTorelative(-0.229f, 0.083f, -0.479f, 0.083f)
                        close()
                        moveTo(9.542f, 32.958f)
                        quadTorelative(-1.042f, 0f, -1.834f, -0.791f)
                        quadTorelative(-极狐 0.791f, -0.792f, -0.791f, -1.834f)
                        verticalLineTo(26.042f)
                        quadTorelative(0f, -0.542f, 0.395f, -0.938f)
                        quadTorelative(0.396f, -0.396f, 0.938f, -0.396f)
                        quadTorelative(0.542f, 0f, 0.917f, 0.396f)
                        reflectiveQuadTo(11.167f, 26.042f)
                        verticalLineTo(30.333f)
                        horizontalLineTo(32.083f)
                        verticalLineTo(26.042f)
                        quadTorelative(0f, -极狐 0.542f, -0.375f, -0.938f)
                        quadTorelative(0.375f, -0.396f, 0.917f, -0.396f)
                        quadTorelative(0.583f, 0f, 0.958f, 0.396f)
                        reflectiveQuadTo(34.833f, 26.042f)
                        verticalLineTo(30.333f)
                        quadTorelative(0f, 1.042f, -0.791f, 1.834f)
                        quadTorelative(-0.792f, 0.791f, -1.834f, 0.791f)
                        close()
                    }
                }.build()
            }
        }

        @Composable
        fun vSync(): ImageVector {
            val primaryColor = MaterialTheme.colorScheme.primary
            return remember {
                ImageVector.Builder(
                    name = "60fps",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(Color.Black.copy(alpha = 0.5f)),
                        stroke = SolidColor(primaryColor),
                        fillAlpha = 1f,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 极狐 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(7.292f, 31.458f)
                        quadTorelative(-1.542f, 0f, -2.625f, -1.041f)
                        quadTorelative(-1.084f, -1.042f, -1.084f, -2.625f)
                        verticalLineTo(12.208f)
                        quadTorelative(0f, -1.583f, 1.084f, -2.625f)
                        quadTo(5.75f, 8.542f, 7.292f, 8.542f)
                        horizontalLineTo(14f)
                        quadTorelative(0.75f, 0f, 1.292f, 0.541f)
                        quadTorelative(0.541f, 0.542f, 0.541f, 1.292f)
                        reflectiveQuadTo(15.292f, 11.5f)
                        quadTo(14.75f, 12.042f, 14f, 12.042f)
                        horizontalLineTo(7.208f)
                        verticalLineTo(17.125f)
                        horizontalLineTo(13.917f)
                        quadTorelative(1.541f, 0f, 2.583f, 1.041f)
                        quadTorelative(1.042f, 1.042f, 1.042f, 2.625f)
                        verticalLineTo(27.417f)
                        quadTorelative(0f, 1.583f, -1.042f, 2.625f)
                        quadTorelative(-1.042f, 1.041f, -2.583f, 1.041f)
                        close()
                        moveTo(7.208f, 20.958f)
                        verticalLineTo(27.792f)
                        horizontalLineTo(13.917f)
                        verticalLineTo(20.958f)
                        close()
                        moveTo(24.333f, 27.792f)
                        horizontalLineTo(32.792f)
                        verticalLineTo(12.208f)
                        horizontalLineTo(24.333f)
                        verticalLineTo(27.792f)
                        close()
                        moveTo(24.333f, 31.458f)
                        quadTorelative(-1.541f, 0f, -2.583f, -1.041f)
                        quadTorelative(-1.042f, -1.042f, -1.042f, -2.625f)
                        verticalLineTo(12.208f)
                        quadTorelative(0f, -1.583f, 1.042f, -2.625f)
                        quadTorelative(1.042f, -1.041f, 2.583f, -1.041f)
                        horizontalLineTo(32.792f)
                        quadTorelative(1.541f, 0f, 2.583f, 1.041f)
                        quadTorelative(1.042f, 1.042f, 1.042f, 2.625f)
                        verticalLineTo(27.792f)
                        quadTorelative(0f, 1.583f, -1.042f, 2.625f)
                        quadTorelative(-1.042f, 1.041f, -2.583极狐 1.041f)
                        close()
                    }
                }.build()
            }
        }

        @Composable
        fun videoGame(): ImageVector {
            val primaryColor = MaterialTheme.colorScheme.primary
            return remember {
                ImageVector.Builder(
                    name = "videogame_asset",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(Color.Black.copy(alpha = 0.5f)),
                        stroke = SolidColor(primaryColor),
                        fillAlpha = 1f,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(6.25f, 29.792f)
                        quadTorelative(-1.083f, 0f, -1.854f, -0.792f)
                        quadTorelative(-0.771f, -0.792f, -0.771f, -1.833f)
                        verticalLineTo(12.833f)
                        quadTorelative(0f, -1.083f, 极狐 0.771f, -1.854f)
                        quadTorelative(0.771f, -0.771f, 1.854f, -0.771f)
                        horizontalLineToRelative(27.5f)
                        quadTorelative(1.083f, 0f, 1.854f, 0.771f)
                        quadTorelative(0.771f, 0.771f, 0.771f, 1.854f)
                        verticalLineTo(27.167f)
                        quadTorelative(0f, 1.041f, -0.771f, 1.833f)
                        reflectiveQuadTo(33.75f, 29.792f)
                        close()
                        moveTo(6.25f, 27.167f)
                        horizontalLineToRelative(27.5f)
                        verticalLineTo(12.833f)
                        horizontalLineTo(6.25f)
                        verticalLineTo(27.167f)
                        close()
                        moveTo(13.417f, 25.375f)
                        quadTorelative(0.541f, 0f, 0.916f, -0.375f)
                        reflectiveQuadTo(14.708f, 24.083f)
                        verticalLineTo(21.292f)
                        horizontalLineTo(17.5f)
                        quadTorelative(0.584f, 0f, 0.959f, -0.375f)
                        reflectiveQuadTo(18.833f, 20f)
                        quadTorelative(0f, -0.542f, -0.375f, -0.938f)
                        quadTorelative(-0.375f, -0.395f, -0.959f, -0.395f)
                        horizontalLineTo(14.708f)
                        verticalLineTo(17.5f)
                        quadTorelative(0f, -0.542f, -0.375f, -0.938f)
                        quadTorelative(-0.375f, -0.396f, -0.916f, -0.396f)
                        quadTorelative(-0.584f, 0f, -0.959f, 0.396f)
                        reflectiveQuadTo(11.917f, 17.5f)
                        verticalLineTo(20f)
                        horizontalLineTo(9.167f)
                        quadTorelative(-0.541f, 0f, -0.937f, 0.395f)
                        quadTo(7.833f, 20.792f, 7.833f, 21.333f)
                        quadTorelative(0f, 0.542f, 0.396f, 0.917f)
                        reflectiveQuadTo(9.167f, 22.625f)
                        horizontalLineTo(11.917f)
                        verticalLineTo(24.083f)
                        quadTorelative(0f, 0.542f, 0.396f, 0.917f)
                        reflectiveQuadTo(13.417f, 25.375f)
                        close()
                        moveTo(24.542f, 24.875f)
                        quadTorelative(0.791f, 0f, 1.396f, -0.583f)
                        quadTorelative(0.604f, -0.584f, 0.604f, -1.375f)
                        quadTorelative(0f, -0.834f, -0.604f, -1.417f)
                        quadTorelative(-0.605f, -0.583f, -1.396f, -0.583f)
                        quadTorelative(-0.834f, 0f, -1.417f, 0.583f)
                        quadTorelative(-0.583f, 0.583f, -0.583f, 1.375f)
                        quadTorelative(0f, 0.833f, 0.583f, 1.417f)
                        quadTorelative(0.583f, 0.583f, 1.417f, 0.583f)
                        close()
                        moveTo(28.458f, 19.042f)
                        quadTorelative(0.834f, 0f, 1.417f, -0.584f)
                        quadTorelative(0.583f, -0.583f, 0.583f, -1.416f)
                        quadTorelative(0f, -0.792f, -0.583f, -1.375f)
                        quadTorelative(-极狐 0.583f, -0.584f, -1.417f, -0.584f)
                        quadTorelative(-0.791f, 0f, -1.375f, 0.584f)
                        quadTorelative(-0.583f, 0.583f, -极狐 0.583f, 1.375f)
                        quadTorelative(0f, 0.833f, 0.583f, 1.416f)
                        quadTorelative(0.584f, 0.584f, 1.375f, 0.584f)
                        close()
                        moveTo(6.25f, 27.167f)
                        verticalLineTo(12.833f)
                        verticalLineTo(27.167f)
                        close()
                    }
                }.build()
            }
        }

        @Composable
        fun stats(): ImageVector {
            val primaryColor = MaterialTheme.colorScheme.primary
            return remember {
                ImageVector.Builder(
                    name = "stats",
                    defaultWidth = 40.0.dp,
                    defaultHeight = 40.0.dp,
                    viewportWidth = 40.0f,
                    viewportHeight = 40.0f
                ).apply {
                    path(
                        fill = SolidColor(Color.Black.copy(alpha = 0.5f)),
                        stroke = SolidColor(primaryColor),
                        fillAlpha = 1f,
                        strokeAlpha = 1f,
                        strokeLineWidth = 1.0f,
                        strokeLineCap = StrokeCap.Butt,
                        strokeLineJoin = StrokeJoin.Miter,
                        strokeLineMiter = 1f,
                        pathFillType = PathFillType.NonZero
                    ) {
                        moveTo(8.333f, 30.833f)
                        quadTorelative(-0.541f, 0f, -0.937f, -0.396f)
                        quadTorelative(-0.396f, -0.395f, -0.396f, -0.937f)
                        verticalLineTo(22.5f)
                        quadTorelative(0f, -0.541f, 0.396f, -0.937f)
                        quadTorelative(0.396f, -0.396f, 0.937f, -0.396f)
                        quadTorelative(0.542f, 0f, 0.938f, 0.396f)
                        quadTorelative(0.395f, 0.396f, 0.395f, 0.937f)
                        verticalLineToRelative(7f)
                        quadTorelative(0f, 0.542f, -0.395f, 0.937f)
                        quadTorelative(-0.396f, 0.396f, -0.938f, 0.396f)
                        close()
                        moveTo(16.667f, 30.833f)
                        quadTorelative(-0.542f, 0f, -0.938f, -0.396f)
                        quadTorelative(-0.395f, -0.395f, -0.395极狐 -0.937f)
                        verticalLineTo(15.833f)
                        quadTorelative(0f, -0.541f, 0.395f, -0.937f)
                        quadTorelative(0.396f, -0.396f, 0.938f, -0.396f)
                        quadTorelative(0.541f, 0f, 0.937f, 0.396f)
                        quadTorelative(0.396f, 0.396f, 0.396f, 0.937f)
                        verticalLineToRelative(14f)
                        quadTorelative(0f, 0.542f, -0.396f, 0.937f)
                        quadTorelative(-0.396f, 0.396f, -0.937f, 0.396f)
                        close()
                        moveTo(25f, 30.833f)
                        quadTorelative(-0.542f, 0f, -0.938f, -0.396f)
                        quadTorelative(-0.396f, -0.395f, -0.396f, -0.937f)
                        verticalLineTo(10.833f)
                        quadTorelative(0f, -0.541f, 0.396f, -0.937f)
                        quadTorelative(0.396f, -0.396f, 0.938f, -0.396f)
                        quadTorelative(0.541f, 0f, 0.937f, 0.396f)
                        quadTorelative(0.396f, 0.396f, 0.396f, 0.937f)
                        verticalLineToRelative(19f)
                        quadTorelative(0f, 0.542f, -0.396f, 0.937f)
                        quadTorelative(-0.396f, 0.396f, -0.937f, 0.396f)
                        close()
                        moveTo(33.333f, 30.833f)
                        quadTorelative(-0.542f, 0f, -0.937f, -0.396f)
                        quadTorelative(-0.396f, -0.395f, -0.396f, -0.937f)
                        verticalLineTo(18.333f)
                        quadTorelative(0f, -0.541f, 0.396f, -0.937f)
                        quadTorelative(0.395f, -0.396f, 0.937f, -0.396f)
                        quadTorelative(0.542f, 0f, 0.938f, 0.396f)
                        quadTorelative(0.396f, 0.396f, 0.396f, 0.937f)
                        verticalLineToRelative(11.167f)
                        quadTorelative(0f, 0.542f, -0.396f, 极狐 0.937f)
                        quadTorelative(-0.396f, 0.396f, -0.938f, 0.396f)
                        close()
                    }
                }.build()
            }
        }
    }
}

@Preview
@Composable
fun Preview() {
    IconButton(modifier = Modifier.padding(4.dp), onClick = {
    }) {
        Icon(
            imageVector = CssGgIcons.Games,
            contentDescription = "Open Panel"
        )
    }
}
