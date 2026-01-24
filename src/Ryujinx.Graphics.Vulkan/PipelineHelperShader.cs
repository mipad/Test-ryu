using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineHelperShader : PipelineBase
    {
        // 添加两个参数的构造函数，内部创建PipelineCache
        public PipelineHelperShader(VulkanRenderer gd, Device device) : base(gd, device)
        {
        }

        // 修改构造函数，将PipelineCache参数传递给基类
        public PipelineHelperShader(VulkanRenderer gd, Device device, PipelineCache pipelineCache) : base(gd, device, pipelineCache)
        {
        }

        public void SetRenderTarget(TextureView view, uint width, uint height)
        {
            CreateFramebuffer(view, width, height);
            CreateRenderPass();
            SignalStateChange();
        }

        private void CreateFramebuffer(TextureView view, uint width, uint height)
        {
            FramebufferParams = new FramebufferParams(Device, view, width, height);
            UpdatePipelineAttachmentFormats();
        }

        public void SetCommandBuffer(CommandBufferScoped cbs)
        {
            CommandBuffer = (Cbs = cbs).CommandBuffer;

            // Restore per-command buffer state.

            if (Pipeline != null)
            {
                Gd.Api.CmdBindPipeline(CommandBuffer, Pbp, Pipeline.Get(CurrentCommandBuffer).Value);
            }

            SignalCommandBufferChange();
        }

        public void Finish()
        {
            EndRenderPass();
        }

        public void Finish(VulkanRenderer gd, CommandBufferScoped cbs)
        {
            Finish();

            if (gd.PipelineInternal.IsCommandBufferActive(cbs.CommandBuffer))
            {
                gd.PipelineInternal.Restore();
            }
        }
    }
}