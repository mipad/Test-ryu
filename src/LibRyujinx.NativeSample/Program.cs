using LibRyujinx.Sample;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using System;

namespace LibRyujinx.NativeSample
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // 参数检查
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: program <rom-path>");
                Console.WriteLine("Example: LibRyujinx.NativeSample \"C:\\games\\Zelda.xci\"");
                return;
            }

            try
            {
                // 核心初始化
                if (!LibRyujinxInterop.Initialize(IntPtr.Zero))
                {
                    throw new Exception("Core initialization failed");
                }

                // 图形初始化
                if (!LibRyujinxInterop.InitializeGraphics(new GraphicsConfiguration()))
                {
                    throw new Exception("Graphics initialization failed");
                }

                // 窗口配置
                var nativeWindowSettings = new NativeWindowSettings()
                {
                    ClientSize = new Vector2i(800, 600),
                    Title = $"Ryujinx Native: {System.IO.Path.GetFileName(args[0])}",
                    API = ContextAPI.NoAPI,
                    IsEventDriven = false,
                    Flags = ContextFlags.ForwardCompatible,
                };

                // 创建窗口
                using var window = new NativeWindow(nativeWindowSettings);
                window.IsVisible = true;
                
                // 启动模拟 - 添加异常处理
                window.Start(args[0]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL ERROR: {ex.Message}");
                Console.WriteLine("Stack trace:");
                Console.WriteLine(ex.StackTrace);
                
                #if DEBUG
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                #endif
            }
        }
    }
}
