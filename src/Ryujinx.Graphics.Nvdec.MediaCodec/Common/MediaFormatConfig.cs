using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Nvdec.MediaCodec.Common
{
    public class MediaFormatConfig
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

        public void SetString(string key, string value) => _values[key] = value;
        public void SetInteger(string key, int value) => _values[key] = value;
        public void SetLong(string key, long value) => _values[key] = value;
        public void SetFloat(string key, float value) => _values[key] = value;
        
        public void SetByteBuffer(string key, byte[] data)
        {
            _values[key] = data;
        }
        
        public object GetValue(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>(_values);
        }
    }
}
