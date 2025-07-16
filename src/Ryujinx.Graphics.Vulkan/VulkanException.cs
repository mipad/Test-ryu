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

    class VulkanException : Exception
    {
        public Result Result { get; }  // 添加Result属性

        public VulkanException()
        {
        }

        public VulkanException(Result result) : base($"Unexpected API error \"{result}\".")
        {
            Result = result;  // 设置Result属性
        }

        public VulkanException(string message) : base(message)
        {
        }

        public VulkanException(string message, Exception innerException) : base(message, innerException)
        {
        }

        // 添加序列化支持
        protected VulkanException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            Result = (Result)info.GetValue(nameof(Result), typeof(Result));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(Result), Result);
        }
    }
}
