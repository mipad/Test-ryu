package org.ryujinx.android

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "controller_settings")

object ControllerTypeManager {
    private const val CONTROLLER_TYPES_KEY = "controller_types"
    
    suspend fun saveControllerType(context: Context, controllerId: String, type: ControllerType) {
        val currentMap = loadAllControllerTypes(context).toMutableMap()
        currentMap[controllerId] = type
        saveAllControllerTypes(context, currentMap)
    }
    
    fun getControllerType(context: Context, controllerId: String): Flow<ControllerType> {
        return context.dataStore.data.map { preferences ->
            val json = preferences[stringPreferencesKey(CONTROLLER_TYPES_KEY)] ?: "{}"
            val typeMap = Json.decodeFromString<Map<String, ControllerType>>(json)
            typeMap[controllerId] ?: ControllerType.PRO_CONTROLLER
        }
    }
    
    // ControllerTypeManager.kt
suspend fun loadAllControllerTypes(context: Context): Map<String, ControllerType> {
    val preferences = context.dataStore.data.firstOrNull() ?: return emptyMap()
    val json = preferences[stringPreferencesKey(CONTROLLER_TYPES_KEY)] ?: "{}"
    return try {
        Json.decodeFromString(json)
    } catch (e: Exception) {
        emptyMap()
    }
}
    
    private suspend fun saveAllControllerTypes(context: Context, typeMap: Map<String, ControllerType>) {
        context.dataStore.edit { preferences ->
            val json = Json.encodeToString(typeMap)
            preferences[stringPreferencesKey(CONTROLLER_TYPES_KEY)] = json
        }
    }
    
    suspend fun removeControllerType(context: Context, controllerId: String) {
        val currentMap = loadAllControllerTypes(context).toMutableMap()
        currentMap.remove(controllerId)
        saveAllControllerTypes(context, currentMap)
    }
}
