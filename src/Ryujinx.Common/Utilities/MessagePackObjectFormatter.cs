using MessagePack;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ryujinx.Common.Utilities
{
    public static class MessagePackObjectFormatter
    {
        public static string Format(byte[] msgPackData)
        {
            if (msgPackData == null || msgPackData.Length == 0)
            {
                return "null";
            }

            var builder = new IndentedStringBuilder();
            FormatMessagePack(msgPackData, builder);
            return builder.ToString();
        }

        private static void FormatMessagePack(byte[] data, IndentedStringBuilder builder)
        {
            try
            {
                var reader = new MessagePackReader(data);
                FormatMessagePackValue(ref reader, builder);
            }
            catch (Exception ex)
            {
                builder.Append("Error formatting MessagePack: ");
                builder.Append(ex.Message);
            }
        }

        private static void FormatMessagePackValue(ref MessagePackReader reader, IndentedStringBuilder builder)
        {
            if (reader.End)
            {
                builder.Append("null");
                return;
            }

            // 使用 MessagePackCode 来检查格式
            var code = reader.NextCode;

            // 判断是否为有符号整数
            if (code >= MessagePackCode.Int8 && code <= MessagePackCode.Int64 ||
                (code >= MessagePackCode.FixedIntMin && code <= MessagePackCode.FixedIntMax))
            {
                builder.Append(reader.ReadInt64());
                return;
            }

            // 判断是否为无符号整数
            if (code == MessagePackCode.UInt8 || code == MessagePackCode.UInt16 || 
                code == MessagePackCode.UInt32 || code == MessagePackCode.UInt64)
            {
                builder.Append(reader.ReadUInt64());
                return;
            }

            // 处理其他类型
            switch (code)
            {
                case MessagePackCode.Nil:
                    reader.ReadNil();
                    builder.Append("null");
                    break;

                case MessagePackCode.True:
                case MessagePackCode.False:
                    builder.Append(reader.ReadBoolean());
                    break;

                case MessagePackCode.Float32:
                    builder.Append(reader.ReadSingle());
                    break;

                case MessagePackCode.Float64:
                    builder.Append(reader.ReadDouble());
                    break;

                case MessagePackCode.Str8:
                case MessagePackCode.Str16:
                case MessagePackCode.Str32:
                case MessagePackCode.FixStr:
                    builder.AppendQuotedString(reader.ReadString());
                    break;

                case MessagePackCode.Bin8:
                case MessagePackCode.Bin16:
                case MessagePackCode.Bin32:
                    var bytes = reader.ReadBytes();
                    if (bytes.HasValue)
                    {
                        FormatByteArray(bytes.Value.ToArray(), builder);
                    }
                    else
                    {
                        builder.Append("null");
                    }
                    break;

                case MessagePackCode.FixArray:
                case MessagePackCode.Array16:
                case MessagePackCode.Array32:
                    var arrayLength = reader.ReadArrayHeader();
                    
                    if (arrayLength == 0)
                    {
                        builder.Append("[ ]");
                        break;
                    }
                    
                    builder.Append("[ ");
                    
                    for (int i = 0; i < arrayLength; i++)
                    {
                        FormatMessagePackValue(ref reader, builder);
                        if (i < arrayLength - 1)
                        {
                            builder.Append(", ");
                        }
                    }
                    
                    builder.Append(" ]");
                    break;

                case MessagePackCode.FixMap:
                case MessagePackCode.Map16:
                case MessagePackCode.Map32:
                    var mapLength = reader.ReadMapHeader();
                    
                    if (mapLength == 0)
                    {
                        builder.Append("{ }");
                        break;
                    }
                    
                    builder.Append('{')
                           .IncreaseIndent()
                           .AppendLine();
                    
                    for (int i = 0; i < mapLength; i++)
                    {
                        // Key
                        FormatMessagePackValue(ref reader, builder);
                        builder.Append(": ");
                        
                        // Value
                        FormatMessagePackValue(ref reader, builder);
                        
                        if (i < mapLength - 1)
                        {
                            builder.AppendLine(",");
                        }
                        else
                        {
                            builder.AppendLine();
                        }
                    }
                    
                    builder.DecreaseIndent()
                           .Append('}');
                    break;

                case MessagePackCode.FixExt1:
                case MessagePackCode.FixExt2:
                case MessagePackCode.FixExt4:
                case MessagePackCode.FixExt8:
                case MessagePackCode.FixExt16:
                case MessagePackCode.Ext8:
                case MessagePackCode.Ext16:
                case MessagePackCode.Ext32:
                    var extHeader = reader.ReadExtensionFormatHeader();
                    var extData = reader.ReadRaw(extHeader.Length);
                    
                    builder.Append('{')
                           .IncreaseIndent()
                           .AppendLine();
                    
                    builder.AppendQuotedString("TypeCode")
                           .Append(": ")
                           .Append(extHeader.TypeCode)
                           .AppendLine(",");
                    
                    builder.AppendQuotedString("Value")
                           .Append(": ");
                    
                    FormatByteArrayAsString(extData.ToArray(), builder, true);
                    
                    builder.DecreaseIndent()
                           .AppendLine()
                           .Append('}');
                    break;

                default:
                    // 未知类型，跳过
                    builder.Append("(unknown)");
                    reader.Skip();
                    break;
            }
        }

        private static void FormatByteArray(byte[] arr, IndentedStringBuilder builder)
        {
            if (arr == null)
            {
                builder.Append("null");
                return;
            }

            builder.Append("[ ");

            foreach (var b in arr)
            {
                builder.Append("0x");
                builder.Append(ToHexChar(b >> 4));
                builder.Append(ToHexChar(b & 0xF));
                builder.Append(", ");
            }

            // Remove trailing comma if array is not empty
            if (arr.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            builder.Append(" ]");
        }

        private static void FormatByteArrayAsString(byte[] arr, IndentedStringBuilder builder, bool withPrefix)
        {
            if (arr == null)
            {
                builder.Append("null");
                return;
            }

            builder.Append('"');

            if (withPrefix)
            {
                builder.Append("0x");
            }

            foreach (var b in arr)
            {
                builder.Append(ToHexChar(b >> 4));
                builder.Append(ToHexChar(b & 0xF));
            }

            builder.Append('"');
        }

        private static char ToHexChar(int b)
        {
            if (b < 10)
            {
                return unchecked((char)('0' + b));
            }

            return unchecked((char)('A' + (b - 10)));
        }

        // 为了保持向后兼容性，提供一个辅助方法来转换对象为MessagePack字节数组
        public static byte[] ToMessagePackBytes(object obj)
        {
            if (obj == null)
            {
                return new byte[] { 0xC0 }; // MessagePack nil value
            }

            return MessagePackSerializer.Serialize(obj);
        }

        // 扩展方法，用于保持旧的API调用方式
        public static class Extensions
        {
            public static string ToString(byte[] msgPackData, bool pretty)
            {
                if (msgPackData == null || msgPackData.Length == 0)
                {
                    return "null";
                }

                if (pretty)
                {
                    return Format(msgPackData);
                }

                // 非pretty模式，尝试反序列化为对象然后ToString
                try
                {
                    var obj = MessagePackSerializer.Deserialize<object>(msgPackData);
                    return obj?.ToString() ?? "null";
                }
                catch
                {
                    return BitConverter.ToString(msgPackData).Replace("-", "");
                }
            }
        }

        internal class IndentedStringBuilder
        {
            const string DefaultIndent = "    ";

            private int _indentCount;
            private int _newLineIndex;
            private readonly StringBuilder _builder;

            public string IndentString { get; set; } = DefaultIndent;

            public IndentedStringBuilder(StringBuilder builder)
            {
                _builder = builder;
            }

            public IndentedStringBuilder()
                : this(new StringBuilder())
            { }

            public IndentedStringBuilder(string str)
                : this(new StringBuilder(str))
            { }

            public IndentedStringBuilder(int length)
                : this(new StringBuilder(length))
            { }

            public int Length { get => _builder.Length; }

            public IndentedStringBuilder IncreaseIndent()
            {
                _indentCount++;

                return this;
            }

            public IndentedStringBuilder DecreaseIndent()
            {
                if (_indentCount > 0)
                {
                    _indentCount--;
                }

                return this;
            }

            public IndentedStringBuilder Append(char value)
            {
                _builder.Append(value);

                return this;
            }

            public IndentedStringBuilder Append(string value)
            {
                _builder.Append(value);

                return this;
            }

            public IndentedStringBuilder Append(object value)
            {
                _builder.Append(value?.ToString() ?? "null");

                return this;
            }

            public IndentedStringBuilder AppendQuotedString(string value)
            {
                if (value == null)
                {
                    _builder.Append("null");
                    return this;
                }

                _builder.Append('"');
                // 转义字符串中的特殊字符
                var escapedValue = value.Replace("\\", "\\\\")
                                        .Replace("\"", "\\\"")
                                        .Replace("\n", "\\n")
                                        .Replace("\r", "\\r")
                                        .Replace("\t", "\\t");
                _builder.Append(escapedValue);
                _builder.Append('"');

                return this;
            }

            public IndentedStringBuilder AppendLine()
            {
                _newLineIndex = _builder.Length;

                _builder.AppendLine();

                for (int i = 0; i < _indentCount; i++)
                {
                    _builder.Append(IndentString);
                }

                return this;
            }

            public IndentedStringBuilder AppendLine(string value)
            {
                _builder.Append(value);

                this.AppendLine();

                return this;
            }

            public IndentedStringBuilder TrimLastLine()
            {
                if (_newLineIndex < _builder.Length)
                {
                    _builder.Remove(_newLineIndex, _builder.Length - _newLineIndex);
                }

                return this;
            }

            public IndentedStringBuilder Remove(int startIndex, int length)
            {
                if (startIndex >= 0 && startIndex + length <= _builder.Length)
                {
                    _builder.Remove(startIndex, length);
                }

                return this;
            }

            public override string ToString()
            {
                return _builder.ToString();
            }
        }
    }
}
