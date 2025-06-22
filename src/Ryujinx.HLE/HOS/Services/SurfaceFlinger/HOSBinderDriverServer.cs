using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.SurfaceFlinger.Types; // 添加这个命名空间
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger
{
    class HOSBinderDriverServer : IHOSBinderDriver
    {
        private static readonly Dictionary<int, IBinder> _registeredBinderObjects = new();

        private static int _lastBinderId = 0;

        private static readonly object _lock = new();

        // 添加 Disconnect 事务代码常量（必须与 IGraphicBufferProducer 一致）
        private const uint TransactionCodeDisconnect = 11;

        public static int RegisterBinderObject(IBinder binder)
        {
            lock (_lock)
            {
                _lastBinderId++;

                _registeredBinderObjects.Add(_lastBinderId, binder);

                return _lastBinderId;
            }
        }

        public static void UnregisterBinderObject(int binderId)
        {
            lock (_lock)
            {
                _registeredBinderObjects.Remove(binderId);
            }
        }

        public static int GetBinderId(IBinder binder)
        {
            lock (_lock)
            {
                foreach (KeyValuePair<int, IBinder> pair in _registeredBinderObjects)
                {
                    if (ReferenceEquals(binder, pair.Value))
                    {
                        return pair.Key;
                    }
                }

                return -1;
            }
        }

        private static IBinder GetBinderObjectById(int binderId)
        {
            lock (_lock)
            {
                if (_registeredBinderObjects.TryGetValue(binderId, out IBinder binder))
                {
                    return binder;
                }

                return null;
            }
        }

        protected override ResultCode AdjustRefcount(int binderId, int addVal, int type)
        {
            IBinder binder = GetBinderObjectById(binderId);

            if (binder == null)
            {
                Logger.Error?.Print(LogClass.SurfaceFlinger, $"Invalid binder id {binderId}");
                return ResultCode.Success;
            }

            return binder.AdjustRefcount(addVal, type);
        }

        protected override void GetNativeHandle(int binderId, uint typeId, out KReadableEvent readableEvent)
        {
            IBinder binder = GetBinderObjectById(binderId);

            if (binder == null)
            {
                readableEvent = null;
                Logger.Error?.Print(LogClass.SurfaceFlinger, $"Invalid binder id {binderId}");
                return;
            }

            binder.GetNativeHandle(typeId, out readableEvent);
        }

        protected override ResultCode OnTransact(int binderId, uint code, uint flags, ReadOnlySpan<byte> inputParcel, Span<byte> outputParcel)
        {
            // 添加详细事务日志
            Logger.Debug?.Print(LogClass.SurfaceFlinger, 
                $"OnTransact: BinderId={binderId}, Code={code}, Flags={flags}");

            IBinder binder = GetBinderObjectById(binderId);

            if (binder == null)
            {
                Logger.Error?.Print(LogClass.SurfaceFlinger, $"Invalid binder id: {binderId}");
                // 使用 SurfaceFlinger 的错误码
                return (ResultCode)Status.BadValue;
            }

            // 使用正确的事务代码处理 Disconnect
            if (code == TransactionCodeDisconnect)
            {
                const int requiredSize = sizeof(int);
                if (inputParcel.Length < requiredSize)
                {
                    Logger.Error?.Print(LogClass.SurfaceFlinger, 
                        $"Disconnect parcel too small: {inputParcel.Length} < {requiredSize}");
                    // 使用 SurfaceFlinger 的错误码
                    return (ResultCode)Status.BadValue;
                }
                
                int api = BitConverter.ToInt32(inputParcel);
                Logger.Info?.Print(LogClass.SurfaceFlinger, 
                    $"Processing Disconnect: BinderId={binderId}, API={api}");
            }

            ResultCode result = binder.OnTransact(code, flags, inputParcel, outputParcel);
            
            if (result != ResultCode.Success)
            {
                Logger.Warning?.Print(LogClass.SurfaceFlinger, 
                    $"Transaction failed: BinderId={binderId}, Code={code}, Result={result}");
            }
            
            return result;
        }
    }
}
