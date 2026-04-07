namespace PhotoOrganizer.Models;

public enum ResultStatus
{
    Success,
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
}
