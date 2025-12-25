namespace Ryujinx.Graphics.Nvdec.MediaCodec.Native
{
    public static class AndroidJniWrapper
    {
        // MediaCodec 类和方法
        public static class MediaCodec
        {
            private static readonly AndroidJavaClass _mediaCodecClass;
            
            static MediaCodec()
            {
                _mediaCodecClass = new AndroidJavaClass("android.media.MediaCodec");
            }
            
            public static AndroidJavaObject CreateDecoderByType(string mimeType)
            {
                return _mediaCodecClass.CallStatic<AndroidJavaObject>(
                    "createDecoderByType", mimeType);
            }
            
            public static AndroidJavaObject CreateByCodecName(string name)
            {
                return _mediaCodecClass.CallStatic<AndroidJavaObject>(
                    "createByCodecName", name);
            }
            
            public static AndroidJavaObject[] GetCodecsList()
            {
                return _mediaCodecClass.CallStatic<AndroidJavaObject[]>("getCodecsList");
            }
        }
        
        // MediaFormat 类和方法
        public static class MediaFormat
        {
            private static readonly AndroidJavaClass _mediaFormatClass;
            
            static MediaFormat()
            {
                _mediaFormatClass = new AndroidJavaClass("android.media.MediaFormat");
            }
            
            public static AndroidJavaObject CreateVideoFormat(
                string mimeType, int width, int height)
            {
                return _mediaFormatClass.CallStatic<AndroidJavaObject>(
                    "createVideoFormat", mimeType, width, height);
            }
            
            public static void SetInteger(AndroidJavaObject format, string key, int value)
            {
                format.Call("setInteger", key, value);
            }
            
            public static void SetLong(AndroidJavaObject format, string key, long value)
            {
                format.Call("setLong", key, value);
            }
            
            public static void SetByteBuffer(AndroidJavaObject format, string key, byte[] bytes)
            {
                using (var byteBuffer = AndroidJNI.NewByteArray(bytes.Length))
                {
                    AndroidJNI.SetByteArrayRegion(byteBuffer, 0, bytes.Length, bytes);
                    using (var bufferClass = new AndroidJavaClass("java.nio.ByteBuffer"))
                    {
                        var buffer = bufferClass.CallStatic<AndroidJavaObject>(
                            "wrap", byteBuffer);
                        format.Call("setByteBuffer", key, buffer);
                    }
                }
            }
        }
        
        // SurfaceTexture 封装
        public static class SurfaceTexture
        {
            public static AndroidJavaObject Create(int texName)
            {
                return new AndroidJavaObject(
                    "android.graphics.SurfaceTexture", texName);
            }
            
            public static void UpdateTexImage(AndroidJavaObject surfaceTexture)
            {
                surfaceTexture.Call("updateTexImage");
            }
            
            public static void GetTransformMatrix(
                AndroidJavaObject surfaceTexture, float[] matrix)
            {
                surfaceTexture.Call("getTransformMatrix", matrix);
            }
        }
        
        // Surface 封装
        public static class Surface
        {
            public static AndroidJavaObject Create(AndroidJavaObject surfaceTexture)
            {
                return new AndroidJavaObject("android.view.Surface", surfaceTexture);
            }
        }
    }
}
