using Silk.NET.Vulkan; 
using System;
using System.Runtime.Serialization;

namespace Ryujinx.Graphics.Vulkan
{
    static class ResultExtensions
    {
        public static bool IsError(this Result result)
        {
            // Only negative result codes are errors.
            return result < Result.Success;
        }

        public static void ThrowOnError(this Result result)
        {
            // Only negative result codes are errors.
            if (result.IsError())
            {
                throw new VulkanException(result);
            }
        }
    }

    [Serializable]
    class VulkanException : Exception
    {
        public Result Result { get; }
        public ulong AllocationSize { get; }

        public VulkanException() : base()
        {
        }

        public VulkanException(Result result) : base($"Unexpected API error \"{result}\".")
        {
            Result = result;
        }
        
        public VulkanException(Result result, string message) : base($"{result}: {message}")
        {
            Result = result;
        }
        
        public VulkanException(Result result, string message, ulong allocationSize) : base($"{result}: {message}")
        {
            Result = result;
            AllocationSize = allocationSize;
        }

        public VulkanException(string message) : base(message)
        {
        }

        public VulkanException(string message, Exception innerException) : base(message, innerException)
        {
        }

        [Obsolete("This API supports obsolete formatter-based serialization")]
        protected VulkanException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
