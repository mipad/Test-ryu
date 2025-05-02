namespace Ryujinx.Graphics.GAL
{
    // 
    public enum VSyncMode
    {
        Enabled,    
        Disabled 
    }

    // 
    public static class VSyncModeHelper
    {
        // 
        public static VSyncMode ConvertLegacyMode(string configvalue)
        {
            return configvalue switch
            {
                "Switch"    => VSyncMode.On,
                "Unbounded" => VSyncMode.Off,
                "Custom"    => VSyncMode.On,
                _ => VSyncMode.On 
            };
        }
    }
}
