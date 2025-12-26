using System;

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

    // 解码相关类型
    public class FrameQueue<T>
    {
        private readonly Queue<T> _queue = new Queue<T>();
        
        public void Enqueue(T frame) => _queue.Enqueue(frame);
        public T Dequeue() => _queue.Dequeue();
        public bool TryDequeue(out T frame) => _queue.TryDequeue(out frame);
        public int Count => _queue.Count;
        public void Clear() => _queue.Clear();
    }

    public class H264ParameterSets
    {
        public byte[] Sps { get; set; }
        public byte[] Pps { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class H264PictureInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] Data { get; set; }
        public long Timestamp { get; set; }
        public bool IsKeyFrame { get; set; }
    }

    // Surface 相关
    public interface ISurface
    {
        int Width { get; }
        int Height { get; }
        void Render();
    }

    // MediaFormat 相关
    public class MediaFormatConfig
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

        public void SetString(string key, string value) => _values[key] = value;
        public void SetInteger(string key, int value) => _values[key] = value;
        public void SetLong(string key, long value) => _values[key] = value;
        public object GetValue(string key) => _values.TryGetValue(key, out var value) ? value : null;
    }
}
