using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nv.Types
{
    [StructLayout(LayoutKind.Sequential, Size = 0x8)]
    public struct NvFence
    {
        public const uint InvalidSyncPointId = uint.MaxValue;

        public uint Id;
        public uint Value;

        public readonly bool IsValid()
        {
            return Id != InvalidSyncPointId;
        }

        public void UpdateValue(NvHostSyncpt hostSyncpt)
        {
            Value = hostSyncpt.ReadSyncpointValue(Id);
        }

        public void Increment(GpuContext gpuContext)
        {
            Value = gpuContext.Synchronization.IncrementSyncpoint(Id);
        }

        public readonly bool Wait(GpuContext gpuContext, TimeSpan timeout)
        {
            if (IsValid())
            {
                return gpuContext.Synchronization.WaitOnSyncpoint(Id, Value, timeout);
            }

            return false;
        }
        

        public static NvFence Read(BinaryReader reader)
        {
            return new NvFence
            {
                Id = reader.ReadUInt32(),
                Value = reader.ReadUInt32()
            };
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(Id);
            writer.Write(Value);
        }
    }
}
