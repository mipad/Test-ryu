namespace Ryujinx.Graphics.GAL
{
    // 
    public enum VSyncMode
    {
        On,
        Off
    }

    // 
    public static class VSyncModeHelper
    {
        // 
        public static VSyncMode ConvertLegacyMode(string legacyMode)
        {
            return legacyMode switch
            {
                "Switch"    => VSyncMode.On,
                "Unbounded" => VSyncMode.Off,
                "Custom"    => VSyncMode.On,
                _ => throw new ArgumentOutOfRangeException(nameof(legacyMode))
            };
        }
    }
}
