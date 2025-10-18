using Ryujinx.HLE.HOS.Kernel.Common;
using System;

namespace Ryujinx.HLE
{
    public enum MemoryConfiguration
    {
        MemoryConfiguration4GiB = 0,
        MemoryConfiguration4GiBAppletDev = 1,
        MemoryConfiguration4GiBSystemDev = 2,
        MemoryConfiguration6GiB = 3,
        MemoryConfiguration6GiBAppletDev = 4,
        MemoryConfiguration8GiB = 5,
        MemoryConfiguration10GiB = 6,
        MemoryConfiguration10GiBAppletDev = 7,
        MemoryConfiguration12GiB = 8,
        MemoryConfiguration12GiBAppletDev = 9,
        MemoryConfiguration14GiB = 10,
        MemoryConfiguration14GiBAppletDev = 11,
        MemoryConfiguration16GiB = 12,
        MemoryConfiguration16GiBAppletDev = 13,
    }

    static class MemoryConfigurationExtensions
    {
        private const ulong GiB = 1024 * 1024 * 1024;

#pragma warning disable IDE0055 // Disable formatting
        public static MemoryArrange ToKernelMemoryArrange(this MemoryConfiguration configuration)
        {
            return configuration switch
            {
                MemoryConfiguration.MemoryConfiguration4GiB           => MemoryArrange.MemoryArrange4GiB,
                MemoryConfiguration.MemoryConfiguration4GiBAppletDev  => MemoryArrange.MemoryArrange4GiBAppletDev,
                MemoryConfiguration.MemoryConfiguration4GiBSystemDev  => MemoryArrange.MemoryArrange4GiBSystemDev,
                MemoryConfiguration.MemoryConfiguration6GiB           => MemoryArrange.MemoryArrange6GiB,
                MemoryConfiguration.MemoryConfiguration6GiBAppletDev  => MemoryArrange.MemoryArrange6GiBAppletDev,
                MemoryConfiguration.MemoryConfiguration8GiB           => MemoryArrange.MemoryArrange8GiB,
                MemoryConfiguration.MemoryConfiguration10GiB          => MemoryArrange.MemoryArrange10GiB,
                MemoryConfiguration.MemoryConfiguration10GiBAppletDev => MemoryArrange.MemoryArrange10GiBAppletDev,
                MemoryConfiguration.MemoryConfiguration12GiB          => MemoryArrange.MemoryArrange12GiB,
                MemoryConfiguration.MemoryConfiguration12GiBAppletDev => MemoryArrange.MemoryArrange12GiBAppletDev,
                MemoryConfiguration.MemoryConfiguration14GiB          => MemoryArrange.MemoryArrange14GiB,
                MemoryConfiguration.MemoryConfiguration14GiBAppletDev => MemoryArrange.MemoryArrange14GiBAppletDev,
                MemoryConfiguration.MemoryConfiguration16GiB          => MemoryArrange.MemoryArrange16GiB,
                MemoryConfiguration.MemoryConfiguration16GiBAppletDev => MemoryArrange.MemoryArrange16GiBAppletDev,
                _ => throw new AggregateException($"Invalid memory configuration \"{configuration}\"."),
            };
        }

        public static MemorySize ToKernelMemorySize(this MemoryConfiguration configuration)
        {
            return configuration switch
            {
                MemoryConfiguration.MemoryConfiguration4GiB or
                MemoryConfiguration.MemoryConfiguration4GiBAppletDev or
                MemoryConfiguration.MemoryConfiguration4GiBSystemDev => MemorySize.MemorySize4GiB,
                MemoryConfiguration.MemoryConfiguration6GiB or
                MemoryConfiguration.MemoryConfiguration6GiBAppletDev => MemorySize.MemorySize6GiB,
                MemoryConfiguration.MemoryConfiguration8GiB           => MemorySize.MemorySize8GiB,
                MemoryConfiguration.MemoryConfiguration10GiB or
                MemoryConfiguration.MemoryConfiguration10GiBAppletDev => MemorySize.MemorySize10GiB,
                MemoryConfiguration.MemoryConfiguration12GiB or
                MemoryConfiguration.MemoryConfiguration12GiBAppletDev => MemorySize.MemorySize12GiB,
                MemoryConfiguration.MemoryConfiguration14GiB or
                MemoryConfiguration.MemoryConfiguration14GiBAppletDev => MemorySize.MemorySize14GiB,
                MemoryConfiguration.MemoryConfiguration16GiB or
                MemoryConfiguration.MemoryConfiguration16GiBAppletDev => MemorySize.MemorySize16GiB,
                _ => throw new AggregateException($"Invalid memory configuration \"{configuration}\"."),
            };
        }

        public static ulong ToDramSize(this MemoryConfiguration configuration)
        {
            return configuration switch
            {
                MemoryConfiguration.MemoryConfiguration4GiB or
                MemoryConfiguration.MemoryConfiguration4GiBAppletDev or
                MemoryConfiguration.MemoryConfiguration4GiBSystemDev => 4 * GiB,
                MemoryConfiguration.MemoryConfiguration6GiB or
                MemoryConfiguration.MemoryConfiguration6GiBAppletDev => 6 * GiB,
                MemoryConfiguration.MemoryConfiguration8GiB           => 8 * GiB,
                MemoryConfiguration.MemoryConfiguration10GiB or
                MemoryConfiguration.MemoryConfiguration10GiBAppletDev => 10 * GiB,
                MemoryConfiguration.MemoryConfiguration12GiB or
                MemoryConfiguration.MemoryConfiguration12GiBAppletDev => 12 * GiB,
                MemoryConfiguration.MemoryConfiguration14GiB or
                MemoryConfiguration.MemoryConfiguration14GiBAppletDev => 14 * GiB,
                MemoryConfiguration.MemoryConfiguration16GiB or
                MemoryConfiguration.MemoryConfiguration16GiBAppletDev => 16 * GiB,
                _ => throw new AggregateException($"Invalid memory configuration \"{configuration}\"."),
            };
        }
#pragma warning restore IDE0055
    }
}
