using FFMpegCore;
using FFMpegCore.Enums;

namespace PhotoOrganizer.Core;

public class VideoConverter
{
    public static bool IsFfmpegAvailable { get; private set; }

    // Called once on a background thread after the window is visible
    public static Task<bool> CheckFfmpegAsync() => Task.Run(() =>
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                // Do NOT redirect streams — avoids buffer-fill deadlock
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            proc.Start();
            proc.WaitForExit(3000);
            IsFfmpegAvailable = proc.ExitCode == 0;
        }
        catch
        {
            IsFfmpegAvailable = false;
        }
        return IsFfmpegAvailable;
    });

    public async Task ConvertToMp4Async(string inputPath, string outputPath, DateTime? creationTime, CancellationToken ct)
    {
        if (!IsFfmpegAvailable)
            throw new InvalidOperationException("FFmpeg not found on PATH.");

        var args = FFMpegArguments
            .FromFileInput(inputPath)
            .OutputToFile(outputPath, true, options =>
            {
                options
                    .WithVideoCodec(VideoCodec.LibX264)
                    .WithAudioCodec(AudioCodec.Aac)
                    .WithFastStart();

                if (creationTime.HasValue)
                {
                    options.WithCustomArgument(
                        $"-metadata creation_time=\"{creationTime.Value:yyyy-MM-ddTHH:mm:ss}\"");
                }
            });

        await args.ProcessAsynchronously(true);
    }
}
