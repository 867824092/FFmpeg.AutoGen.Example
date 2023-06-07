// See https://aka.ms/new-console-template for more information

using FFmpeg.Audio;
using FFmpeg.Helper;

FFmpegBinariesHelper.RegisterFFmpegBinaries();
CancellationTokenSource source = new();
string inputUrl = "audio=麦克风 (Realtek(R) Audio)";
string outputUrl = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"out.aac");
_ = Task.Run(() => {
	try {
        FFmpegAudio.Run(inputUrl, outputUrl, source.Token);
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
