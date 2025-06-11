using System;
using Ryujinx.Graphics.Vulkan;

namespace Ryujinx.Graphics.Vulkan
{
    public class VkException : Exception
    {
        public VkException(Result result) : base($"Vulkan error: {result}")
        {
            Result = result;
        }

        public Result Result { get; }
    }
}
