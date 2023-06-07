using FFmpeg.Helper;

namespace FFmpegMedia; 

public class Program {
    private static void Main(string[] args) {
        FFmpegBinariesHelper.RegisterFFmpegBinaries();
        CancellationTokenSource source = new();
        _ = Task.Run(() => FFmpegMedia.Start(source.Token));
        string s = Console.ReadLine();
        while (s.Trim() == "q")
        {
            Console.WriteLine($"输入内容：{s}");
            if (s.Trim() == "q")
            {
                break;
            }
            else
            {
                s = Console.ReadLine();
            }
        }
        source.Cancel();
        Console.WriteLine("录制完成");
    }
}