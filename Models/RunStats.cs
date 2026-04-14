namespace PhotoOrganizer.Models;

public class RunStats
{
    public int Photos { get; set; }
    public int Videos { get; set; }
    public int Heic { get; set; }
    public int Raw { get; set; }
    public int Other { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }

    public void Reset()
    {
        Photos = 0;
        Videos = 0;
        Heic = 0;
        Raw = 0;
        Other = 0;
        Skipped = 0;
        Errors = 0;
    }
}
