using System;
using Silk.NET.Vulkan; // 根据实际使用的库

namespace Ryujinx.Graphics.Vulkan
{
    public class VkException : Exception
    {
        public Result Result { get; }

        public VkException(Result result) 
            : base($"Vulkan API error: {result}") 
            => Result = result;

        public VkException(Result result, string message) 
            : base($"Vulkan error ({result}): {message}") 
            => Result = result;
    }
}
