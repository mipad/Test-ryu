using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Ryujinx.Graphics.Vulkan
{
    class SyncManager
    {
        private class SyncHandle
        {
            public ulong ID;
            public MultiFenceHolder Waitable; // 使用传统的MultiFenceHolder
            public ulong FlushId;
            public bool Signalled;

            public bool NeedsFlush(ulong currentFlushId)
            {
                return (long)(FlushId - currentFlushId) >= 0;
            }
        }

        private ulong _firstHandle;

        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly List<SyncHandle> _handles;
        private ulong _flushId;
        private long _waitTicks;

        public SyncManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            _handles = [];
            
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"SyncManager初始化: 使用传统的栅栏同步机制");
        }

        public void RegisterFlush()
        {
            _flushId++;
        }

        public void Create(ulong id, bool strict)
        {
            ulong flushId = _flushId;

            SyncHandle handle = new()
            {
                ID = id,
                FlushId = flushId
            };

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"创建同步对象 ID={id}");

            // 所有同步对象都使用MultiFenceHolder
            handle.Waitable = new MultiFenceHolder();
            
            // 严格模式下，立即刷新所有命令并添加等待对象
            if (strict || _gd.InterruptAction == null)
            {
                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"严格模式: 刷新所有命令并添加等待对象");
                
                // 严格模式下，立即刷新所有命令
                _gd.FlushAllCommands();
                
                // 添加到命令缓冲区的等待对象
                _gd.CommandBufferPool.AddWaitable(handle.Waitable);
            }
            else
            {
                // 非严格模式：添加到使用中的命令缓冲区
                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"非严格模式: 使用栅栏机制");
                
                _gd.CommandBufferPool.AddInUseWaitable(handle.Waitable);
            }

            lock (_handles)
            {
                _handles.Add(handle);
            }
        }

        public ulong GetCurrent()
        {
            lock (_handles)
            {
                ulong lastHandle = _firstHandle;

                foreach (SyncHandle handle in _handles)
                {
                    lock (handle)
                    {
                        if (handle.Signalled)
                        {
                            if (handle.ID > lastHandle)
                            {
                                lastHandle = handle.ID;
                            }
                            continue;
                        }

                        bool signaled = false;
                        
                        if (handle.Waitable != null)
                        {
                            // 检查栅栏是否已发出信号
                            signaled = handle.Waitable.WaitForFences(_gd.Api, _device, 0);
                        }

                        if (signaled)
                        {
                            lastHandle = handle.ID;
                            handle.Signalled = true;
                        }
                    }
                }

                return lastHandle;
            }
        }

        public void Wait(ulong id)
        {
            SyncHandle result = null;

            lock (_handles)
            {
                if ((long)(_firstHandle - id) > 0)
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"同步对象 ID={id} 已发出信号或已删除，无需等待");
                    return; // 句柄已发出信号或已删除
                }

                foreach (SyncHandle handle in _handles)
                {
                    if (handle.ID == id)
                    {
                        result = handle;
                        break;
                    }
                }
            }

            if (result != null)
            {
                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"开始等待同步对象 ID={result.ID}");

                long beforeTicks = Stopwatch.GetTimestamp();

                if (result.NeedsFlush(_flushId))
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"同步对象需要刷新，当前FlushId={_flushId}，对象FlushId={result.FlushId}");
                    _gd.InterruptAction(() =>
                    {
                        if (result.NeedsFlush(_flushId))
                        {
                            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                $"中断GPU线程并刷新所有命令");
                            _gd.FlushAllCommands();
                        }
                    });
                }

                lock (result)
                {
                    if (result.Signalled)
                    {
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"同步对象 ID={result.ID} 已发出信号，无需等待");
                        return;
                    }

                    bool signaled = false;
                    
                    if (result.Waitable != null)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            "使用传统栅栏等待机制");
                        
                        signaled = result.Waitable.WaitForFences(_gd.Api, _device, 1000000000);
                    }
                    else
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, "同步对象没有栅栏");
                        signaled = true;
                    }

                    if (!signaled)
                    {
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"VK Sync Object {result.ID} failed to signal within 1000ms. Continuing...");
                    }
                    else
                    {
                        _waitTicks += Stopwatch.GetTimestamp() - beforeTicks;
                        result.Signalled = true;
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"同步对象 ID={result.ID} 等待成功，耗时={Stopwatch.GetTimestamp() - beforeTicks} ticks");
                    }
                }
            }
            else
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                    $"等待同步对象 ID={id} 未找到");
            }
        }

        public void Cleanup()
        {
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"开始清理同步对象，当前句柄数量={_handles.Count}");
            
            // 迭代句柄并删除任何已发出信号的句柄
            while (true)
            {
                SyncHandle first = null;
                lock (_handles)
                {
                    first = _handles.FirstOrDefault();
                }

                if (first == null || first.NeedsFlush(_flushId))
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"清理完成或需要刷新，当前FlushId={_flushId}");
                    break;
                }

                bool signaled = false;
                
                if (first.Waitable != null)
                {
                    signaled = first.Waitable.WaitForFences(_gd.Api, _device, 0);
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"检查同步对象 ID={first.ID}: 使用栅栏检查，已发出信号={signaled}");
                }
                else
                {
                    signaled = true;
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"同步对象 ID={first.ID} 没有同步机制，假设已发出信号");
                }

                if (signaled)
                {
                    // 删除同步对象
                    lock (_handles)
                    {
                        lock (first)
                        {
                            _firstHandle = first.ID + 1;
                            _handles.RemoveAt(0);
                            
                            // 清理MultiFenceHolder资源
                            if (first.Waitable != null)
                            {
                                Array.Clear(first.Waitable.Fences);
                                MultiFenceHolder.FencePool.Release(first.Waitable.Fences);
                                first.Waitable = null;
                            }
                            
                            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                $"删除同步对象 ID={first.ID}，新的_firstHandle={_firstHandle}");
                        }
                    }
                }
                else
                {
                    // 此同步句柄及后续的尚未到达
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"同步对象 ID={first.ID} 尚未发出信号，停止清理");
                    break;
                }
            }
            
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"清理完成，剩余句柄数量={_handles.Count}");
        }

        public long GetAndResetWaitTicks()
        {
            long result = _waitTicks;
            _waitTicks = 0;

            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"获取并重置等待ticks: {result}");
            
            return result;
        }
    }
}