using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;

namespace PhotoOrganizer
{
    class Program
    {
        static void Main(string[] args)
        {
            // The directory containing the photos to convert
            string inputDirectory = "C:\\Photos";

            // The directory where the converted photos will be saved
            string outputDirectory = "C:\\Photos\\PNG_OUTPUT";

            // Create the output directory if it doesn't already exist
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            // Get a list of all JPG and HEIC files in the input directory,
            // including subdirectories
            string[] files = Directory.GetFiles(inputDirectory, "*.jpg", SearchOption.AllDirectories);
            string[] heicFiles = Directory.GetFiles(inputDirectory, "*.heic", SearchOption.AllDirectories);

            // Get a list of all PNG files in the input directory,
            // including subdirectories
            string[] pngFiles = Directory.GetFiles(inputDirectory, "*.png", SearchOption.AllDirectories);

            // Combine the three arrays of files
            string[] allFiles = new string[files.Length + heicFiles.Length + pngFiles.Length];
            Array.Copy(files, allFiles, files.Length);
            Array.Copy(heicFiles, 0, allFiles, files.Length, heicFiles.Length);
            Array.Copy(pngFiles, 0, allFiles, files.Length + heicFiles.Length, pngFiles.Length);

            // Loop through each file and convert it to PNG format if necessary
            foreach (string file in allFiles)
            {
                // If the file is a PNG, simply move it to the output directory
                if (Path.GetExtension(file) == ".png")
                {
                    File.Move(file, Path.Combine(outputDirectory, Path.GetFileName(file)));
                }
                else
                {
                    // Load the image from the file
                    using (Image image = Image.FromFile(file))
                    {

                        if (image.PropertyIdList.Any(x => x == 36867))
                        {
                            // Get the photo taken date/time from the EXIF metadata
                            PropertyItem propertyItem = image.GetPropertyItem(36867);
                            string dateTaken = System.Text.Encoding.UTF8.GetString(propertyItem.Value);

                            // Convert the date/time to a DateTime object
                            DateTime dateTime = DateTime.Parse(dateTaken);

                            // Create the subdirectory for the year and month
                            string yearDirectory = Path.Combine(outputDirectory, dateTime.Year.ToString());
                            string monthDirectory = Path.Combine(yearDirectory, dateTime.Month.ToString() + "_" + dateTime.ToString("MMM"));

                            if (!Directory.Exists(yearDirectory))
                            {
                                Directory.CreateDirectory(yearDirectory);
                            }
                            if (!Directory.Exists(monthDirectory))
                            {
                                Directory.CreateDirectory(monthDirectory);
                            }

                            // Format the date/time as YEAR_MONTH_DAY-HOUR_SECOND
                            string fileName = dateTime.ToString("yyyy_MM_dd-HH_mm_ss");

                            // Create the full path to the new PNG file
                            string outputFile = Path.Combine(monthDirectory, fileName + ".png");

                            // Save the image as a PNG file
                            image.Save(outputFile, System.Drawing.Imaging.ImageFormat.Png);


                        }
                    }
                }
            }
        }
    }
}