using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    unsafe class PipelineHelperShader : PipelineBase
    {
        // 修改构造函数，使用新的静态方法创建PipelineCache
        public PipelineHelperShader(VulkanRenderer gd, Device device) : base(gd, device, CreateTemporaryPipelineCache(gd, device))
        {
        }

        // 创建一个临时PipelineCache的辅助方法
        private static PipelineCache CreateTemporaryPipelineCache(VulkanRenderer gd, Device device)
        {
            var pipelineCacheCreateInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
            };

            gd.Api.CreatePipelineCache(device, &pipelineCacheCreateInfo, null, out var pipelineCache).ThrowOnError();
            return pipelineCache;
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

        // 确保在销毁时清理临时PipelineCache
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 清理临时PipelineCache
                if (PipelineCache.Handle != 0)
                {
                    Gd.Api.DestroyPipelineCache(Device, PipelineCache, null);
                }
            }
            
            base.Dispose(disposing);
        }
    }
}