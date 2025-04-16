using MessagePack;
using System;

namespace Ryujinx.Common.Utilities
{
    public static class MessagePackFormatter
    {
        public static string Format(byte[] msgpackData)
        {
            try
            {
                return MessagePackSerializer.ConvertToJson(msgpackData);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Serialization, 
                    $"MessagePack to JSON failed: {ex.Message}. Falling back to hex dump.");
                return BitConverter.ToString(msgpackData).Replace("-", "");
            }
        }
    }
}
