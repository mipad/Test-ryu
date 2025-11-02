namespace Ryujinx.Graphics.Vulkan
{
    internal enum BufferAllocationType
    {
        Auto = 0,

        HostMappedNoCache,
        HostMapped,
        DeviceLocal,
        DeviceLocalMapped,
        Sparse,
        VirtualMemory, // 添加虚拟内存类型
    }
}
