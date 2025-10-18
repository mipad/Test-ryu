using Ryujinx.HLE.HOS.Kernel.Memory;
using System;

namespace Ryujinx.HLE.HOS.Kernel.Common
{
    static class KSystemControl
    {
        private const ulong KiB = 1024;
        private const ulong MiB = 1024 * KiB;
        private const ulong GiB = 1024 * MiB;

        private const ulong PageSize = 4 * KiB;

        private const ulong RequiredNonSecureSystemPoolSizeVi = 0x2238 * PageSize;
        private const ulong RequiredNonSecureSystemPoolSizeNvservices = 0x710 * PageSize;
        private const ulong RequiredNonSecureSystemPoolSizeOther = 0x80 * PageSize;

        private const ulong RequiredNonSecureSystemPoolSize =
            RequiredNonSecureSystemPoolSizeVi +
            RequiredNonSecureSystemPoolSizeNvservices +
            RequiredNonSecureSystemPoolSizeOther;

        public static ulong GetApplicationPoolSize(MemoryArrange arrange)
        {
            return arrange switch
            {
                MemoryArrange.MemoryArrange4GiB or
                MemoryArrange.MemoryArrange4GiBSystemDev or
                MemoryArrange.MemoryArrange6GiBAppletDev => 3285 * MiB,
                MemoryArrange.MemoryArrange4GiBAppletDev => 2048 * MiB,
                MemoryArrange.MemoryArrange6GiB => 4916 * MiB,
                MemoryArrange.MemoryArrange8GiB => 6964 * MiB,
                MemoryArrange.MemoryArrange10GiB => 9012 * MiB,
                MemoryArrange.MemoryArrange10GiBAppletDev => 9012 * MiB,
                MemoryArrange.MemoryArrange12GiB => 11060 * MiB,
                MemoryArrange.MemoryArrange12GiBAppletDev => 11060 * MiB,
                MemoryArrange.MemoryArrange14GiB => 13108 * MiB,
                MemoryArrange.MemoryArrange14GiBAppletDev => 13108 * MiB,
                MemoryArrange.MemoryArrange16GiB => 15156 * MiB,
                MemoryArrange.MemoryArrange16GiBAppletDev => 15156 * MiB,
                _ => throw new ArgumentException($"Invalid memory arrange \"{arrange}\"."),
            };
        }

        public static ulong GetAppletPoolSize(MemoryArrange arrange)
        {
            return arrange switch
            {
                MemoryArrange.MemoryArrange4GiB => 507 * MiB,
                MemoryArrange.MemoryArrange4GiBAppletDev => 1554 * MiB,
                MemoryArrange.MemoryArrange4GiBSystemDev => 448 * MiB,
                MemoryArrange.MemoryArrange6GiB => 562 * MiB,
                MemoryArrange.MemoryArrange6GiBAppletDev => 2193 * MiB,
                MemoryArrange.MemoryArrange8GiB => 562 * MiB,
                MemoryArrange.MemoryArrange10GiB => 562 * MiB,
                MemoryArrange.MemoryArrange10GiBAppletDev => 2193 * MiB,
                MemoryArrange.MemoryArrange12GiB => 562 * MiB,
                MemoryArrange.MemoryArrange12GiBAppletDev => 2193 * MiB,
                MemoryArrange.MemoryArrange14GiB => 562 * MiB,
                MemoryArrange.MemoryArrange14GiBAppletDev => 2193 * MiB,
                MemoryArrange.MemoryArrange16GiB => 562 * MiB,
                MemoryArrange.MemoryArrange16GiBAppletDev => 2193 * MiB,
                _ => throw new ArgumentException($"Invalid memory arrange \"{arrange}\"."),
            };
        }

        public static ulong GetMinimumNonSecureSystemPoolSize()
        {
            return RequiredNonSecureSystemPoolSize;
        }

        public static ulong GetDramEndAddress(MemorySize size)
        {
            return DramMemoryMap.DramBase + GetDramSize(size);
        }

        public static ulong GenerateRandom()
        {
            // TODO
            return 0;
        }

        public static ulong GetDramSize(MemorySize size)
        {
            return size switch
            {
                MemorySize.MemorySize4GiB => 4 * GiB,
                MemorySize.MemorySize6GiB => 6 * GiB,
                MemorySize.MemorySize8GiB => 8 * GiB,
                MemorySize.MemorySize10GiB => 10 * GiB,
                MemorySize.MemorySize12GiB => 12 * GiB,
                MemorySize.MemorySize14GiB => 14 * GiB,
                MemorySize.MemorySize16GiB => 16 * GiB,
                _ => throw new ArgumentException($"Invalid memory size \"{size}\"."),
            };
        }
    }
}
