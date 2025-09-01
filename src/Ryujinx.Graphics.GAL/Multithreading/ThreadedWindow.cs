using Ryujinx.Graphics.GAL.Multithreading.Commands.Window;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using System;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    public class ThreadedWindow : IWindow
    {
        private readonly ThreadedRenderer _renderer;
        private readonly IRenderer _impl;

        public ThreadedWindow(ThreadedRenderer renderer, IRenderer impl)
        {
            _renderer = renderer;
            _impl = impl;
        }

        public void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            // 原有的 Present 方法实现
            _renderer.WaitForFrame();
            _renderer.New<WindowPresentCommand>().Set(new TableRef<ThreadedTexture>(_renderer, texture as ThreadedTexture), crop, new TableRef<Action>(_renderer, swapBuffersCallback));
            _renderer.QueueCommand();
        }

        public void SetSize(int width, int height)
        {
            _impl.Window.SetSize(width, height);
        }

        public void ChangeVSyncMode(bool vsyncEnabled) { }

        public void SetAntiAliasing(AntiAliasing effect) { }

        public void SetScalingFilter(ScalingFilter type) { }

        public void SetScalingFilterLevel(float level) { }

        public void SetColorSpacePassthrough(bool colorSpacePassthroughEnabled) { }

        // 新增的 SetAspectRatio 方法实现
        public void SetAspectRatio(AspectRatio aspectRatio)
        {
            // 如果底层实现支持设置画面比例，可以调用：
            // _impl.Window.SetAspectRatio(aspectRatio);
            // 或者根据你的实际需求实现相应的逻辑
            
            // 暂时留空或添加适当的实现
        }
    }
}
