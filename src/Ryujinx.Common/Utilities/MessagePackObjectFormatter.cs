using MessagePack;
using MessagePack.Formatters;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ryujinx.Common.Utilities
{
    public static class MessagePackObjectFormatter
    {
        public static string ToString(object obj, bool pretty)
        {
            if (obj == null)
            {
                return "null";
            }

            if (pretty)
            {
                return Format(obj);
            }

            return obj.ToString();
        }

        public static string Format(object obj)
        {
            var builder = new IndentedStringBuilder();

            FormatMsgPackObj(obj, builder);

            return builder.ToString();
        }

        private static void FormatMsgPackObj(object obj, IndentedStringBuilder builder)
        {
            if (obj == null)
            {
                builder.Append("null");
                return;
            }

            if (IsDictionary(obj))
            {
                FormatMsgPackMap(obj, builder);
            }
            else if (IsArrayOrList(obj))
            {
                FormatMsgPackArray(obj, builder);
            }
            else
            {
                var type = obj.GetType();

                if (type == typeof(string))
                {
                    builder.AppendQuotedString((string)obj);
                }
                else if (type == typeof(byte[]))
                {
                    FormatByteArray((byte[])obj, builder);
                }
                else if (type == typeof(ExtensionResult))
                {
                    var extObject = (ExtensionResult)obj;
                    builder.Append('{');

                    // Indent
                    builder.IncreaseIndent()
                           .AppendLine();

                    // Print TypeCode field
                    builder.AppendQuotedString("TypeCode")
                           .Append(": ")
                           .Append(extObject.TypeCode)
                           .AppendLine(",");

                    // Print Value field
                    builder.AppendQuotedString("Value")
                           .Append(": ");

                    FormatByteArrayAsString(extObject.Data, builder, true);

                    // Unindent
                    builder.DecreaseIndent()
                           .AppendLine();

                    builder.Append('}');
                }
                else if (type.IsPrimitive || type == typeof(decimal) || type == typeof(DateTime))
                {
                    builder.Append(obj.ToString());
                }
                else
                {
                    // For complex objects, use MessagePack to serialize to a string representation
                    try
                    {
                        var json = MessagePackSerializer.SerializeToJson(obj);
                        builder.AppendQuotedString(json);
                    }
                    catch
                    {
                        builder.AppendQuotedString(obj.ToString());
                    }
                }
            }
        }

        private static bool IsDictionary(object obj)
        {
            var type = obj.GetType();
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
                   obj is IDictionary;
        }

        private static bool IsArrayOrList(object obj)
        {
            var type = obj.GetType();
            return type.IsArray ||
                   (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) ||
                   obj is IList ||
                   obj is IEnumerable;
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

        private static void FormatMsgPackMap(object obj, IndentedStringBuilder builder)
        {
            builder.Append('{');

            // Indent
            builder.IncreaseIndent()
                   .AppendLine();

            if (obj is IDictionary dictionary)
            {
                var enumerator = dictionary.GetEnumerator();
                var hasItems = false;

                while (enumerator.MoveNext())
                {
                    hasItems = true;
                    var entry = enumerator.Entry;

                    FormatMsgPackObj(entry.Key, builder);
                    builder.Append(": ");
                    FormatMsgPackObj(entry.Value, builder);
                    builder.AppendLine(",");
                }

                // Remove the trailing new line and comma if there were items
                if (hasItems)
                {
                    builder.TrimLastLine()
                           .Remove(builder.Length - 1, 1);
                }
            }
            else if (obj.GetType().IsGenericType && obj.GetType().GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var dictType = obj.GetType();
                var keysProperty = dictType.GetProperty("Keys");
                var indexer = dictType.GetProperty("Item");

                if (keysProperty != null && indexer != null)
                {
                    var keys = keysProperty.GetValue(obj) as IEnumerable;
                    if (keys != null)
                    {
                        var keyList = keys.Cast<object>().ToList();
                        var hasItems = keyList.Count > 0;

                        foreach (var key in keyList)
                        {
                            var value = indexer.GetValue(obj, new[] { key });
                            FormatMsgPackObj(key, builder);
                            builder.Append(": ");
                            FormatMsgPackObj(value, builder);
                            builder.AppendLine(",");
                        }

                        // Remove the trailing new line and comma if there were items
                        if (hasItems)
                        {
                            builder.TrimLastLine()
                                   .Remove(builder.Length - 1, 1);
                        }
                    }
                }
            }

            // Unindent
            builder.DecreaseIndent()
                   .AppendLine();

            builder.Append('}');
        }

        private static void FormatMsgPackArray(object obj, IndentedStringBuilder builder)
        {
            builder.Append("[ ");

            if (obj is IEnumerable enumerable)
            {
                var items = new List<object>();
                foreach (var item in enumerable)
                {
                    items.Add(item);
                }

                foreach (var item in items)
                {
                    FormatMsgPackObj(item, builder);
                    builder.Append(", ");
                }

                // Remove trailing comma if there were items
                if (items.Count > 0)
                {
                    builder.Remove(builder.Length - 2, 2);
                }
            }
            else if (obj is Array array)
            {
                for (int i = 0; i < array.Length; i++)
                {
                    FormatMsgPackObj(array.GetValue(i), builder);
                    builder.Append(", ");
                }

                // Remove trailing comma if array is not empty
                if (array.Length > 0)
                {
                    builder.Remove(builder.Length - 2, 2);
                }
            }

            builder.Append(" ]");
        }

        private static char ToHexChar(int b)
        {
            if (b < 10)
            {
                return unchecked((char)('0' + b));
            }

            return unchecked((char)('A' + (b - 10)));
        }

        // ExtensionResult class for handling MessagePack extension types
        public class ExtensionResult
        {
            public sbyte TypeCode { get; }
            public byte[] Data { get; }

            public ExtensionResult(sbyte typeCode, byte[] data)
            {
                TypeCode = typeCode;
                Data = data;
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
                // Escape quotes in the string
                _builder.Append(value.Replace("\"", "\\\""));
                _builder.Append('"');

                return this;
            }

            public IndentedStringBuilder AppendLine()
            {
                _newLineIndex = _builder.Length;

                _builder.AppendLine();

                for (int i = 0; i < _indentCount; i++)
                    _builder.Append(IndentString);

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

    // 为保持向后兼容性，添加扩展方法
    public static class MessagePackObjectExtensions
    {
        public static string ToString(this object obj, bool pretty)
        {
            return MessagePackObjectFormatter.ToString(obj, pretty);
        }
    }
}
