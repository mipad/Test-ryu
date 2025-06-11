using System;
using Silk.NET.Vulkan; 

namespace Ryujinx.Graphics.Vulkan
{
    public class VkException : Exception
    {
        public VkException(Result result) 
            : base($"Vulkan API error: {result}")
        {
        }

        public VkException(Result result, string message) 
            : base($"Vulkan error ({result}): {message}")
        {
        }
    }
}
