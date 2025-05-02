namespace Ryujinx.Graphics.GAL
 {
public enum VSyncMode
{
    On,        
    Off        
}


public static VSyncMode ConvertLegacyMode(LegacyVSyncMode legacyMode)
{
    return legacyMode switch
    {
        LegacyVSyncMode.Switch    => VSyncMode.On,     // 自适应视为开启
        LegacyVSyncMode.Unbounded => VSyncMode.Off,    // 无限制视为关闭
        LegacyVSyncMode.Custom    => VSyncMode.On,     // 自定义模式默认开启
        _ => throw new ArgumentOutOfRangeException()
    };
}
}
