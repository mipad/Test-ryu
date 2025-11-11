package org.ryujinx.android

import android.app.Application
import android.content.Context
import java.io.File

class KaryujinxApplication : Application() {
    init {
        instance = this
    }

    fun getPublicFilesDir(): File = getExternalFilesDir(null) ?: filesDir

    companion object {
        lateinit var instance: KaryujinxApplication
            private set

        val context: Context get() = instance.applicationContext
    }
}
