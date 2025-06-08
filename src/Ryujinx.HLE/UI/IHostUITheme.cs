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
    
    public override string ToString() => $"rgba({R}, {G}, {B}, {A / 255.0:F2})";
}
