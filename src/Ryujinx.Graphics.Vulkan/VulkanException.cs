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
        public VkResult Result { get; }

        public VulkanException() : base()
        {
        }

        public VulkanException(VkResult result) : base($"Unexpected API error \"{result}\".")
        {
            Result = result;
        }

        public VulkanException(string message) : base(message)
        {
        }

        public VulkanException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected VulkanException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
