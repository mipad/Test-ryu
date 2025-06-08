using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        public bool IsHardwareAccelerated => false;

        private const int WorkBufferSize = 0x200;

        private readonly byte[] _workBuffer = new byte[WorkBufferSize];
        private FFmpegContext _context = new(AVCodecID.AV_CODEC_ID_H264);
        
        private int _oldOutputWidth;
        private int _oldOutputHeight;
        private int _frameSkipCounter;

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            if (_frameSkipCounter > 0)
            {
                _frameSkipCounter--;
                return false;
            }
            
            Surface outSurf = (Surface)output;

            if (outSurf.RequestedWidth != _oldOutputWidth ||
                outSurf.RequestedHeight != _oldOutputHeight)
            {
                ResetDecoder();
                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }

            Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));

            try
            {
                int result = _context.DecodeFrame(outSurf, bs);
                
                if (result != 0)
                {
                    throw new Exception($"Decode failed with error code {result}");
                }
                
                return true;
            }
            catch (Exception ex) when (ex.Message.Contains("illegal short term buffer") || 
                                       ex.Message.Contains("reference picture missing"))
            {
                ResetDecoder();
                _frameSkipCounter = 3;
                return false;
            }
        }

        private void ResetDecoder()
        {
            _context.Dispose();
            _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);
        }

        private static byte[] Prepend(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prep)
        {
            byte[] output = new byte[data.Length + prep.Length];

            prep.CopyTo(output);
            data.CopyTo(new Span<byte>(output)[prep.Length..]);

            return output;
        }

        public void Dispose()
        {
            ResetDecoder();
        }
    }
}
