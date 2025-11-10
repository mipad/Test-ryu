using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    public unsafe class Surface : ISurface
    {
        public AVFrame* Frame { get; }

        public int RequestedWidth { get; }
        public int RequestedHeight { get; }

        public Plane YPlane => new((IntPtr)Frame->Data[0], Stride * Height);
        public Plane UPlane => new((IntPtr)Frame->Data[1], UvStride * UvHeight);
        public Plane VPlane => new((IntPtr)Frame->Data[2], UvStride * UvHeight);

        public FrameField Field => Frame->InterlacedFrame != 0 ? FrameField.Interlaced : FrameField.Progressive;

        public int Width => Frame->Width;
        public int Height => Frame->Height;
        public int Stride => Frame->LineSize[0];
        public int UvWidth => (Width + 1) >> 1;
        public int UvHeight => (Height + 1) >> 1;
        public int UvStride => Frame->LineSize[1];

        // 新增属性：帧格式
        public AVPixelFormat Format => (AVPixelFormat)Frame->Format;

        // 新增属性：检查是否为硬件帧
        public bool IsHardwareFrame => Format == AVPixelFormat.AV_PIX_FMT_MEDIACODEC;

        // 新增属性：时间戳
        public long Pts => Frame->Pts;

        // 新增属性：是否为关键帧
        public bool IsKeyFrame => Frame->KeyFrame != 0;

        // 新增属性：图片类型
        public AVPictureType PictureType => (AVPictureType)Frame->PictureType;

        public Surface(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;

            Frame = FFmpegApi.av_frame_alloc();
            
            if (Frame == null)
            {
                throw new OutOfMemoryException("Failed to allocate AVFrame");
            }
        }

        /// <summary>
        /// 从硬件解码数据初始化 Surface
        /// </summary>
        public bool InitializeFromHardwareData(int width, int height, AVPixelFormat format, int[] lineSizes)
        {
            if (Frame == null) return false;

            try
            {
                // 设置帧基本信息
                Frame->Width = width;
                Frame->Height = height;
                Frame->Format = (int)format;

                // 设置行大小
                for (int i = 0; i < 3 && i < lineSizes.Length; i++)
                {
                    if (i < Frame->LineSize.Length)
                    {
                        Frame->LineSize[i] = lineSizes[i];
                    }
                }

                // 对于硬件解码，我们通常需要分配缓冲区
                if (format != AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
                {
                    // 为软件格式分配图像缓冲区
                    return AllocateImageBuffer();
                }

                return true;
            }
            catch (Exception ex)
            {
                // 记录错误但不抛出异常，让调用者处理
                System.Diagnostics.Debug.WriteLine($"Failed to initialize surface from hardware data: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 分配图像缓冲区
        /// </summary>
        private bool AllocateImageBuffer()
        {
            if (Frame == null) return false;

            try
            {
                // 创建临时数组来存储数据指针和行大小
                byte*[] dataPointers = new byte*[8];
                int[] lineSizes = new int[8];

                // 使用 FFmpeg API 分配图像缓冲区
                fixed (byte** dataPtr = dataPointers)
                fixed (int* lineSizePtr = lineSizes)
                {
                    int result = FFmpegApi.av_image_alloc(
                        dataPtr,
                        lineSizePtr,
                        Frame->Width,
                        Frame->Height,
                        (AVPixelFormat)Frame->Format,
                        32 // 对齐参数
                    );

                    if (result >= 0)
                    {
                        // 将分配的数据复制回 Frame 结构
                        for (int i = 0; i < 8; i++)
                        {
                            Frame->Data[i] = (IntPtr)dataPointers[i];
                            Frame->LineSize[i] = lineSizes[i];
                        }
                    }

                    return result >= 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to allocate image buffer: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 复制平面数据到 Surface
        /// </summary>
        public bool CopyPlaneData(byte[] planeData, int planeIndex, int lineSize, int planeHeight)
        {
            if (Frame == null || planeData == null || planeData.Length == 0)
            {
                return false;
            }

            if (planeIndex < 0 || planeIndex >= Frame->Data.Length)
            {
                return false;
            }

            IntPtr destPtr = (IntPtr)Frame->Data[planeIndex];
            if (destPtr == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                int expectedDataSize = lineSize * planeHeight;
                if (planeData.Length < expectedDataSize)
                {
                    // 数据大小不匹配，使用较小的值
                    expectedDataSize = Math.Min(planeData.Length, expectedDataSize);
                }

                // 逐行复制数据（处理可能的行对齐）
                int sourceOffset = 0;
                for (int row = 0; row < planeHeight; row++)
                {
                    int bytesToCopy = Math.Min(lineSize, planeData.Length - sourceOffset);
                    if (bytesToCopy <= 0) break;

                    Marshal.Copy(planeData, sourceOffset, 
                               IntPtr.Add(destPtr, row * lineSize), 
                               bytesToCopy);
                    sourceOffset += bytesToCopy;
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to copy plane data: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 设置时间戳
        /// </summary>
        public void SetPts(long pts)
        {
            if (Frame != null)
            {
                Frame->Pts = pts;
            }
        }

        /// <summary>
        /// 设置帧类型
        /// </summary>
        public void SetFrameType(bool keyFrame, AVPictureType pictureType)
        {
            if (Frame != null)
            {
                Frame->KeyFrame = keyFrame ? 1 : 0;
                Frame->PictureType = (int)pictureType;
            }
        }

        /// <summary>
        /// 获取指定平面的数据指针
        /// </summary>
        public IntPtr GetPlanePointer(int planeIndex)
        {
            if (Frame == null || planeIndex < 0 || planeIndex >= Frame->Data.Length)
            {
                return IntPtr.Zero;
            }

            return (IntPtr)Frame->Data[planeIndex];
        }

        /// <summary>
        /// 获取指定平面的行大小
        /// </summary>
        public int GetLineSize(int planeIndex)
        {
            if (Frame == null || planeIndex < 0 || planeIndex >= Frame->LineSize.Length)
            {
                return 0;
            }

            return Frame->LineSize[planeIndex];
        }

        /// <summary>
        /// 检查 Surface 是否已正确初始化
        /// </summary>
        public bool IsValid => Frame != null && Frame->Width > 0 && Frame->Height > 0;

        /// <summary>
        /// 获取平面数量（基于格式）
        /// </summary>
        public int PlaneCount
        {
            get
            {
                switch (Format)
                {
                    case AVPixelFormat.AV_PIX_FMT_YUV420P:
                    case AVPixelFormat.AV_PIX_FMT_NV12:
                        return 3;
                    case AVPixelFormat.AV_PIX_FMT_GRAY8:
                        return 1;
                    default:
                        return 3; // 默认为 YUV 格式
                }
            }
        }

        /// <summary>
        /// 获取指定平面的高度
        /// </summary>
        public int GetPlaneHeight(int planeIndex)
        {
            // 对于 YUV420，Y平面是全高，UV平面是半高
            return planeIndex == 0 ? Height : (Height + 1) / 2;
        }

        /// <summary>
        /// 获取指定平面的宽度
        /// </summary>
        public int GetPlaneWidth(int planeIndex)
        {
            // 对于 YUV420，Y平面是全宽，UV平面是半宽
            return planeIndex == 0 ? Width : (Width + 1) / 2;
        }

        public void Dispose()
        {
            if (Frame != null)
            {
                // 首先取消引用
                FFmpegApi.av_frame_unref(Frame);
                
                // 然后释放帧 - 使用局部变量修复编译错误
                AVFrame* framePtr = Frame;
                FFmpegApi.av_frame_free(&framePtr);
            }
        }

        /// <summary>
        /// 获取帧信息用于调试
        /// </summary>
        public string GetFrameInfo()
        {
            if (Frame == null)
            {
                return "Frame: null";
            }

            return $"Format: {Format}, Size: {Width}x{Height}, " +
                   $"Stride: {Stride}, Linesize: [{Frame->LineSize[0]}, {Frame->LineSize[1]}, {Frame->LineSize[2]}], " +
                   $"PTS: {Pts}, KeyFrame: {IsKeyFrame}, Type: {PictureType}";
        }

        /// <summary>
        /// 获取简化的帧信息
        /// </summary>
        public string GetShortInfo()
        {
            if (Frame == null)
            {
                return "Invalid Frame";
            }

            return $"{Width}x{Height} {Format} (PTS: {Pts})";
        }
    }

    /// <summary>
    /// 图片类型枚举
    /// </summary>
    public enum AVPictureType
    {
        AV_PICTURE_TYPE_NONE = 0, // 未定义
        AV_PICTURE_TYPE_I,        // 帧内编码
        AV_PICTURE_TYPE_P,        // 预测编码
        AV_PICTURE_TYPE_B,        // 双向预测编码
        AV_PICTURE_TYPE_S,        // S(GMC)-VOP MPEG-4
        AV_PICTURE_TYPE_SI,       // 切换帧内
        AV_PICTURE_TYPE_SP,       // 切换预测
        AV_PICTURE_TYPE_BI,       // 双向帧内
    }
}