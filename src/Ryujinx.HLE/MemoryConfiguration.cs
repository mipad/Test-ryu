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
        MemoryConfiguration12GiB = 7,        
        MemoryConfiguration14GiB = 8,       
        MemoryConfiguration16GiB = 9,
        
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
                
                MemoryConfiguration.MemoryConfiguration12GiB          => MemoryArrange.MemoryArrange12GiB,
                
                MemoryConfiguration.MemoryConfiguration14GiB          => MemoryArrange.MemoryArrange14GiB,
                
                MemoryConfiguration.MemoryConfiguration16GiB          => MemoryArrange.MemoryArrange16GiB,
                
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
                MemoryConfiguration.MemoryConfiguration10GiB => MemorySize.MemorySize10GiB,
                
                MemoryConfiguration.MemoryConfiguration12GiB => MemorySize.MemorySize12GiB,
                
                MemoryConfiguration.MemoryConfiguration14GiB => MemorySize.MemorySize14GiB,
                
                MemoryConfiguration.MemoryConfiguration16GiB => MemorySize.MemorySize16GiB,
                
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
                MemoryConfiguration.MemoryConfiguration10GiB => 10 * GiB,

                MemoryConfiguration.MemoryConfiguration12GiB => 12 * GiB,

                MemoryConfiguration.MemoryConfiguration14GiB => 14 * GiB,
                
                MemoryConfiguration.MemoryConfiguration16GiB => 16 * GiB,
                
                _ => throw new AggregateException($"Invalid memory configuration \"{configuration}\"."),
            };
        }
#pragma warning restore IDE0055
    }
}

