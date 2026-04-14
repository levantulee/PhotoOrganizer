namespace PhotoOrganizer.Models;

public enum ResultStatus
{
    Success,
    /// <summary>File was organised successfully but date came from filesystem timestamps, not EXIF/metadata.</summary>
    DateInferred,
    Skipped,
    Failed
}

public class ProcessResult
{
    public FileEntry Entry { get; set; } = new();
    public ResultStatus Status { get; set; }
    public string OutputPath { get; set; } = "";
    public string Message { get; set; } = "";
    public bool WasConverted { get; set; }
    /// <summary>MD5 of the source file, set when checksum-based deduplication is enabled.</summary>
    public string? SourceChecksum { get; set; }
}
