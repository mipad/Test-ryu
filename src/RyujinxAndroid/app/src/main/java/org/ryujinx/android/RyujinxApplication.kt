package org.ryujinx.android

import android.app.Application
import android.content.Context
import java.io.File

class ryujinxApplication : Application() {
    init {
        instance = this
    }

    fun getPublicFilesDir(): File = getExternalFilesDir(null) ?: filesDir

    companion object {
        lateinit var instance: ryujinxApplication
            private set

        val context: Context get() = instance.applicationContext
    }
}
