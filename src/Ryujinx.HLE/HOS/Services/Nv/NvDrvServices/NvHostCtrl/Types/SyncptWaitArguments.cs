using Ryujinx.HLE.HOS.Services.Nv.Types;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl.Types
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SyncptWaitArguments
    {
        public NvFence Fence;
        public int Timeout;

        // 序列化方法（Span<byte> -> 结构体）
        public static SyncptWaitArguments FromSpan(byte[] data)
        {
            if (data.Length != Marshal.SizeOf<SyncptWaitArguments>())
            {
                throw new ArgumentException("Invalid data size");
            }

            unsafe
            {
                fixed (byte* ptr = data)
                {
                    return Marshal.PtrToStructure<SyncptWaitArguments>((IntPtr)ptr);
                }
            }
        }

        // 反序列化方法（结构体 -> byte[]）
        public byte[] ToSpan()
        {
            byte[] data = new byte[Marshal.SizeOf<SyncptWaitArguments>()];
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    Marshal.StructureToPtr(this, (IntPtr)ptr, false);
                }
            }
            return data;
        }

        // 兼容旧代码的二进制读写方法
        public static SyncptWaitArguments Read(BinaryReader reader)
        {
            return new SyncptWaitArguments
            {
                Fence = NvFence.Read(reader),
                Timeout = reader.ReadInt32()
            };
        }

        public void Write(BinaryWriter writer)
        {
            Fence.Write(writer);
            writer.Write(Timeout);
        }
    }
}
