using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using System;

namespace Ryujinx.Graphics.Shader.StructuredIr
{
    readonly struct IoDefinition : IEquatable<IoDefinition>
    {
        public StorageKind StorageKind { get; }
        public IoVariable IoVariable { get; }
        public int Location { get; }
        public int Component { get; }
        public string Name { get; } // 新增 Name 属性

        public IoDefinition(StorageKind storageKind, IoVariable ioVariable, int location = 0, int component = 0, string name = null)
        {
            StorageKind = storageKind;
            IoVariable = ioVariable;
            Location = location;
            Component = component;
            Name = name; // 初始化 Name
        }

        public override bool Equals(object other)
        {
            return other is IoDefinition ioDefinition && Equals(ioDefinition);
        }

        public bool Equals(IoDefinition other)
        {
            return StorageKind == other.StorageKind &&
                   IoVariable == other.IoVariable &&
                   Location == other.Location &&
                   Component == other.Component &&
                   Name == other.Name; // 比较 Name
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(StorageKind, IoVariable, Location, Component, Name); // 包含 Name
        }

        public override string ToString()
        {
            return $"{StorageKind}.{IoVariable}.{Location}.{Component}.{Name}"; // 包含 Name
        }
    }
}
