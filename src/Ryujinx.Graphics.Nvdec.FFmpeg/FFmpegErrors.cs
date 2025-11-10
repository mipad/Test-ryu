namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    /// <summary>
    /// FFmpeg 错误代码常量
    /// </summary>
    public static class FFmpegErrors
    {
        // 成功
        public const int SUCCESS = 0;
        
        // 常见错误代码
        public const int AVERROR_BSF_NOT_FOUND      = -1179861752; // Bitstream filter not found
        public const int AVERROR_BUG                = -558323010;  // Internal bug, also see AVERROR_BUG2
        public const int AVERROR_BUFFER_TOO_SMALL   = -1397118274; // Buffer too small
        public const int AVERROR_DECODER_NOT_FOUND  = -1128613112; // Decoder not found
        public const int AVERROR_DEMUXER_NOT_FOUND  = -1296385272; // Demuxer not found
        public const int AVERROR_ENCODER_NOT_FOUND  = -1129203192; // Encoder not found
        public const int AVERROR_EOF                = -541478725;  // End of file
        public const int AVERROR_EXIT               = -1414092869; // Immediate exit was requested; the called function should not be restarted
        public const int AVERROR_EXTERNAL           = -542398533;  // Generic error in an external library
        public const int AVERROR_FILTER_NOT_FOUND   = -1279870712; // Filter not found
        public const int AVERROR_INVALIDDATA        = -1094995529; // Invalid data found when processing input
        public const int AVERROR_MUXER_NOT_FOUND    = -1481985528; // Muxer not found
        public const int AVERROR_OPTION_NOT_FOUND   = -1414549496; // Option not found
        public const int AVERROR_PATCHWELCOME       = -1163346256; // Not yet implemented in FFmpeg, patches welcome
        public const int AVERROR_PROTOCOL_NOT_FOUND = -1330794744; // Protocol not found
        public const int AVERROR_STREAM_NOT_FOUND   = -1381258232; // Stream not found
        public const int AVERROR_BUG2               = -541545794;  // Internal bug, also see AVERROR_BUG
        public const int AVERROR_UNKNOWN            = -1313558101; // Unknown error, typically from an external library
        public const int AVERROR_EXPERIMENTAL       = -733130664;  // Requested feature is flagged experimental. Set strict_std_compliance if you really want to use it.
        public const int AVERROR_INPUT_CHANGED      = -1668179713; // Input changed between calls. Reconfiguration is required. (can be OR-ed with AVERROR_OUTPUT_CHANGED)
        public const int AVERROR_OUTPUT_CHANGED     = -1668179714; // Output changed between calls. Reconfiguration is required. (can be OR-ed with AVERROR_INPUT_CHANGED)
        public const int AVERROR_HTTP_BAD_REQUEST   = -808465656;  // HTTP or RTSP error: bad request(400)
        public const int AVERROR_HTTP_UNAUTHORIZED  = -825242872;  // HTTP or RTSP error: unauthorized(401)
        public const int AVERROR_HTTP_FORBIDDEN     = -858797304;  // HTTP or RTSP error: forbidden(403)
        public const int AVERROR_HTTP_NOT_FOUND     = -875574520;  // HTTP or RTSP error: not found(404)
        public const int AVERROR_HTTP_OTHER_4XX     = -1482175736; // HTTP or RTSP error: other error(4xx)
        public const int AVERROR_HTTP_SERVER_ERROR  = -1482175992; // HTTP or RTSP error: server error(5xx)

        // POSIX 错误代码（FFmpeg 使用负值）
        public const int AVERROR_EAGAIN = -11; // Resource temporarily unavailable

        /// <summary>
        /// 获取错误代码的描述
        /// </summary>
        public static string GetErrorDescription(int errorCode)
        {
            switch (errorCode)
            {
                case SUCCESS: return "Success";
                case AVERROR_BSF_NOT_FOUND: return "Bitstream filter not found";
                case AVERROR_BUG: return "Internal bug";
                case AVERROR_BUFFER_TOO_SMALL: return "Buffer too small";
                case AVERROR_DECODER_NOT_FOUND: return "Decoder not found";
                case AVERROR_DEMUXER_NOT_FOUND: return "Demuxer not found";
                case AVERROR_ENCODER_NOT_FOUND: return "Encoder not found";
                case AVERROR_EOF: return "End of file";
                case AVERROR_EXIT: return "Immediate exit requested";
                case AVERROR_INVALIDDATA: return "Invalid data found";
                case AVERROR_EAGAIN: return "Resource temporarily unavailable, try again";
                default: return $"Unknown error ({errorCode})";
            }
        }
    }
}