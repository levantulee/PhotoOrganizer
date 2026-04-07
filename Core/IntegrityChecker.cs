using ImageMagick;
using System.Security.Cryptography;

namespace PhotoOrganizer.Core;

public class IntegrityChecker
{
    public static string ComputeMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    public static bool VerifyImageCopy(string sourcePath, string outputPath)
    {
        try
        {
            var srcHash = ComputeMd5(sourcePath);
            var outHash = ComputeMd5(outputPath);
            return string.Equals(srcHash, outHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool VerifyConvertedImage(string outputPath)
    {
        try
        {
            using var img = new MagickImage(outputPath);
            return img.Width > 0 && img.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> VerifyVideoAsync(string outputPath)
    {
        try
        {
            var info = await FFMpegCore.FFProbe.AnalyseAsync(outputPath);
            return info != null;
        }
        catch
        {
            return false;
        }
    }
}
