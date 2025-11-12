using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineHelperShader : PipelineBase
    {
        public PipelineHelperShader(VulkanRenderer gd, Device device) : base(gd, device)
        {
        }

        public void SetRenderTarget(TextureView view, uint width, uint height)
        {
            CreateFramebuffer(view, width, height);
            CreateRenderPass();
            SignalStateChange();
        }

        // 添加重载方法：只传入TextureView，使用其内部尺寸
        public void SetRenderTarget(TextureView view)
        {
            CreateFramebuffer(view, (uint)view.Width, (uint)view.Height);
            CreateRenderPass();
            SignalStateChange();
        }

        // 添加重载方法：支持Auto<DisposableImageView>
        public void SetRenderTarget(Auto<DisposableImageView> imageView)
        {
            // 这里需要根据你的实际实现来处理Auto<DisposableImageView>
            // 暂时使用默认尺寸，你可能需要从其他地方获取实际尺寸
            CreateFramebufferForImageView(imageView, 1920, 1080); // 临时默认尺寸
            CreateRenderPass();
            SignalStateChange();
        }

        // 添加：为ImageView创建帧缓冲区的方法
        private void CreateFramebufferForImageView(Auto<DisposableImageView> imageView, uint width, uint height)
        {
            // 这里需要根据你的Vulkan实现来创建帧缓冲区
            // 暂时使用一个简单的实现
            FramebufferParams = new FramebufferParams(Device, imageView, width, height);
            UpdatePipelineAttachmentFormats();
        }

        private void CreateFramebuffer(TextureView view, uint width, uint height)
        {
            FramebufferParams = new FramebufferParams(Device, view, width, height);
            UpdatePipelineAttachmentFormats();
        }

        // 添加：设置视口的方法
        public void SetViewport(int x, int y, int width, int height)
        {
            // 设置视口
            var viewport = new Viewport
            {
                X = x,
                Y = y,
                Width = width,
                Height = height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            
            // 根据你的Vulkan实现调用相应的API
            Gd.Api.CmdSetViewport(CommandBuffer, 0, 1, viewport);
        }

        // 添加：设置裁剪区域的方法
        public void SetScissor(int x, int y, int width, int height)
        {
            // 设置裁剪矩形
            var scissor = new Rect2D
            {
                Offset = new Offset2D(x, y),
                Extent = new Extent2D((uint)width, (uint)height)
            };
            
            // 根据你的Vulkan实现调用相应的API
            Gd.Api.CmdSetScissor(CommandBuffer, 0, 1, scissor);
        }

        // 添加：设置着色器程序的方法（单程序版本）
        public void SetProgram(ShaderCollection program)
        {
            // 设置单个着色器程序
            if (program != null)
            {
                // 这里需要根据你的实际实现来绑定着色器程序
                program.Bind(CommandBuffer);
            }
        }

        // 添加：设置分离的顶点和片段着色器程序的方法
        public void SetProgram(ShaderCollection vertexProgram, ShaderCollection fragmentProgram)
        {
            // 设置分离的顶点和片段着色器
            // 这里需要根据你的实际实现来处理
            // 暂时使用顶点程序，假设它是完整的程序
            if (vertexProgram != null)
            {
                vertexProgram.Bind(CommandBuffer);
            }
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