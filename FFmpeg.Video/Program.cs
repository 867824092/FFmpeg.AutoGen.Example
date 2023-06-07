
using FFmpeg.Helper;
using FFmpeg.Video;

FFmpegBinariesHelper.RegisterFFmpegBinaries();
CancellationTokenSource source = new();
string inputUrl = "video=Integrated Camera";
string outputUrl = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "out.h264");
_ = Task.Run(() => {
    try {
        FFmpegVideo.Run(inputUrl, outputUrl, source.Token);
    }
    catch (Exception ex) {
        source.Cancel();
        Console.WriteLine(ex.Message);
    }
});
string s = Console.ReadLine();
while (s.Trim() == "q" && !source.Token.IsCancellationRequested) {
    Console.WriteLine($"输入内容：{s}");
    if (s.Trim() == "q") {
        break;
    }
    else {
        s = Console.ReadLine();
    }
}
source.Cancel();
Console.WriteLine("采集完成");
