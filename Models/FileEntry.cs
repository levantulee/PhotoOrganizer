namespace PhotoOrganizer.Models;

public enum FileCategory
{
    Image,       // .jpg, .jpeg, .png, .tif, .tiff, .webp, .bmp, .avif
    Heic,        // .heic, .heif
    Video,       // .mov, .mp4, .avi, .mkv, .3gp, .wmv, .m4v, .mts, .m2ts, .webm, .flv, .ts
    Raw,         // camera RAW + source files: .raw, .cr2, .cr3, .nef, .arw, .dng, .orf, .rw2, .pef, .srw, .raf, .3fr, .psd, .xcf
    PassThrough, // copy as-is: .gif, .svg, .ai, .eps, .wmf (counted as "Other" in stats)
    Unknown
}

public class FileEntry
{
    public string SourcePath { get; set; } = "";
    public FileCategory Category { get; set; }
    public DateTime? ResolvedDate { get; set; }
    public bool DateIsUnknown { get; set; }
    /// <summary>Date was inferred from neighbouring files in the same folder, not from metadata.</summary>
    public bool DateIsEstimated { get; set; }
    /// <summary>Date was taken from the file's filesystem created/modified timestamp (fallback of last resort).</summary>
    public bool DateFromFilesystem { get; set; }
    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
    public double? GpsAltitude { get; set; }
    public string? JsonSidecarPath { get; set; }
}
