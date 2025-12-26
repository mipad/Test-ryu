namespace Ryujinx.Graphics.Nvdec.MediaCodec
{
    public static class GL
    {
        public static void GenTextures(int n, int[] textures)
        {
            // 实际应该调用 OpenGL ES API
            // 这里只是模拟实现
            for (int i = 0; i < n; i++)
            {
                textures[i] = i + 1000; // 返回虚拟纹理ID
            }
        }
        
        public static void DeleteTexture(int texture)
        {
            // 删除纹理的实现
        }
        
        public static void BindTexture(int target, int texture)
        {
            // 绑定纹理
        }
        
        public static void TexImage2D(int target, int level, int internalFormat,
            int width, int height, int border, int format, int type, byte[] pixels)
        {
            // 设置纹理数据
        }
    }
}
