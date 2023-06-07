using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FFmpeg.Helper {
    public class FFmpegBinariesHelper
    {
        public static void RegisterFFmpegBinaries() {
            ffmpeg.RootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FFmpeg");
            Console.WriteLine($"FFmpeg binaries found in: {ffmpeg.RootPath}");
            ffmpeg.avdevice_register_all();
            SetupLogging();
        }
        static unsafe void SetupLogging() {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_INFO);

            // do not convert to local function
            av_log_set_callback_callback logCallback = (p0, level, format, vl) =>
            {
                if (level > ffmpeg.av_log_get_level())
                    return;

                var lineSize = 1024;
                var lineBuffer = stackalloc byte[lineSize];
                var printPrefix = 1;
                ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
                var line = Marshal.PtrToStringUTF8((IntPtr)lineBuffer);
                Console.Write($"level:{level} +  : {line}");
            };
            ffmpeg.av_log_set_callback(logCallback);
        }
    }
}