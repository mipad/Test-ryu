using System;
using System.Collections.Generic;  // 添加这个

namespace Ryujinx.Graphics.Nvdec.MediaCodec
{
    // 基础接口
    public interface IDecoder : IDisposable
    {
        bool Initialize(int width, int height);
        void Decode(byte[] data);
        void Flush();
        void Reset();
    }

    // 基础类型
    public class ByteBuffer
    {
        public byte[] Data { get; set; }
        public int Position { get; set; }
        public int Limit { get; set; }
        public int Capacity => Data?.Length ?? 0;

        public ByteBuffer(byte[] data)
        {
            Data = data;
            Position = 0;
            Limit = data?.Length ?? 0;
        }

        public void Clear()
        {
            Position = 0;
            Limit = Data?.Length ?? 0;
        }
    }

    public class BufferInfo
    {
        public int Offset { get; set; }
        public int Size { get; set; }
        public long PresentationTimeUs { get; set; }
        public int Flags { get; set; }

        public bool IsEOS => (Flags & 0x4) != 0;
        public bool IsConfig => (Flags & 0x2) != 0;
        public bool IsCodecConfig => (Flags & 0x2) != 0;
        public bool IsKeyFrame => (Flags & 0x1) != 0;
    }

    // Android 相关的包装类
    public class AndroidJavaObject : IDisposable
    {
        private IntPtr _jniObject;
        private string _className;

        public AndroidJavaObject(string className, params object[] args)
        {
            _className = className;
            // 这里应该调用 JNI 创建对象，暂时简化
        }

        public T Call<T>(string methodName, params object[] args)
        {
            // 这里应该通过 JNI 调用方法，暂时返回默认值
            return default(T);
        }

        public void Call(string methodName, params object[] args)
        {
            // 这里应该通过 JNI 调用方法
        }

        public T Get<T>(string fieldName)
        {
            // 这里应该通过 JNI 获取字段值
            return default(T);
        }

        public void Dispose()
        {
            if (_jniObject != IntPtr.Zero)
            {
                // 释放 JNI 引用
                _jniObject = IntPtr.Zero;
            }
        }
    }
}
