namespace PhotoOrganizer.Models;

public enum FileCategory
{
    Image,   // .jpg, .jpeg, .png
    Heic,    // .heic, .heif
    Video,   // .mov, .mp4, .avi, .mkv, .3gp
    Unknown
}

public class FileEntry
{
    public string SourcePath { get; set; } = "";
    public FileCategory Category { get; set; }
    public DateTime? ResolvedDate { get; set; }
    public bool DateIsUnknown { get; set; }
    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
    public double? GpsAltitude { get; set; }
    public string? JsonSidecarPath { get; set; }
}
