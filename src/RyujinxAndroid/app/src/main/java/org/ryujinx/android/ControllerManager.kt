// ControllerManager.kt
package org.ryujinx.android

import android.content.Context
import androidx.lifecycle.LiveData
import androidx.lifecycle.MutableLiveData
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

object ControllerManager {
    private val _connectedControllers = MutableLiveData<List<Controller>>(emptyList())
    val connectedControllers: LiveData<List<Controller>> = _connectedControllers
    
    fun addController(context: Context, controller: Controller) {
        val currentList = _connectedControllers.value?.toMutableList() ?: mutableListOf()
        
        if (!currentList.any { it.id == controller.id }) {
            currentList.add(controller)
            _connectedControllers.postValue(currentList)
            
            // 尝试加载保存的类型配置
            CoroutineScope(Dispatchers.IO).launch {
                val savedType = ControllerTypeManager.loadAllControllerTypes(context)[controller.id]
                savedType?.let {
                    val updatedList = currentList.map { 
                        if (it.id == controller.id) it.copy(controllerType = savedType) 
                        else it
                    }
                    _connectedControllers.postValue(updatedList)
                }
            }
        }
    }
    
    fun removeController(controllerId: String) {
        val currentList = _connectedControllers.value?.toMutableList() ?: return
        currentList.removeAll { it.id == controllerId }
        _connectedControllers.postValue(currentList)
    }
    
    fun updateControllerType(context: Context, controllerId: String, newType: ControllerType) {
        val currentList = _connectedControllers.value?.toMutableList() ?: return
        val updatedList = currentList.map { 
            if (it.id == controllerId) it.copy(controllerType = newType) 
            else it 
        }
        _connectedControllers.postValue(updatedList)
        
        // 保存到持久化存储
        CoroutineScope(Dispatchers.IO).launch {
            ControllerTypeManager.saveControllerType(context, controllerId, newType)
        }
    }
    
    // 添加缺失的 updateControllerId 方法
    fun updateControllerId(context: Context, controllerId: String, newId: Int) {
        val currentList = _connectedControllers.value?.toMutableList() ?: return
        val updatedList = currentList.map { 
            if (it.id == controllerId) {
                // 注意：Controller 类需要有一个 deviceId 字段
                // 如果 Controller 类没有 deviceId 字段，您需要先添加它
                it.copy(deviceId = newId)
            } else {
                it
            }
        }
        _connectedControllers.postValue(updatedList)
    }
}
