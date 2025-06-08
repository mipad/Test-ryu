namespace Ryujinx.HLE.UI
{
    public interface IHostUITheme
    {
        string FontFamily { get; }

        ThemeColor DefaultBackgroundColor { get; }
        ThemeColor DefaultForegroundColor { get; }
        ThemeColor DefaultBorderColor { get; }
        ThemeColor SelectionBackgroundColor { get; }
        ThemeColor SelectionForegroundColor { get; }
    }
    
    // 新增：ThemeColor 结构体实现
    public struct ThemeColor
    {
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public byte A { get; }

        public ThemeColor(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }
        
        // 转换为RGBA格式字符串
        public override string ToString() => $"rgba({R}, {G}, {B}, {A / 255.0:F2})";
    }
}
