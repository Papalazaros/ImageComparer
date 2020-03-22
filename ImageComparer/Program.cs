using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ImageComparer
{
    internal static class Program
    {
        public class ImageInfo
        {
            public string Path { get; set; }
            public Bitmap Image { get; set; }
            public long[,] R { get; set; }
            public long[,] G { get; set; }
            public long[,] B { get; set; }

            public int Width { get; set; }
            public int Height { get; set; }

            public List<string> MatchedImages { get; set; } = new List<string>();
        }

        public struct Point
        {
            public Point(int x, int y)
            {
                X = x;
                Y = y;
            }

            public override int GetHashCode()
            {
                uint h = 0x811c9dc5;

                h = (h ^ (uint)X) * 0x01000193;
                h = (h ^ (uint)Y) * 0x01000193;

                return (int)h;
            }

            private int X { get; }
            private int Y { get; }
        }

        public class Pixel
        {
            public Pixel(int x, int y, Color color)
            {
                Point = new Point(x, y);
                Color = color;
            }

            public override int GetHashCode()
            {
                return Point.GetHashCode();
            }

            public Point Point;
            public Color Color;
        }

        private const string imageDirectory = @"";

        private const double similarityThreshold = .85;

        private const int maxSize = 250;

        private const int splitQuadrants = 32;

        private const double similarPixelThreshold = .05;

        private static void Main()
        {
            string[] filePaths = Directory.GetFiles(imageDirectory);

            IEnumerable<string> filteredFilePaths = filePaths
                .Where(x => x.Contains(".jpeg") || x.Contains(".jpg"))
                .Take(1000);

            ConcurrentBag<ImageInfo> concurrentBag = new ConcurrentBag<ImageInfo>();

            Parallel.ForEach(filteredFilePaths, x => concurrentBag.Add(ExtractImageInformation(x)));
            Parallel.ForEach(concurrentBag, x => FindSimilarImages(x, concurrentBag));

            List<string> matchedImages = concurrentBag
                .Where(x => x.MatchedImages.Count > 1)
                .OrderByDescending(x => x.MatchedImages.Count)
                .Select(x => "<div style=\"display:inline-flex\">\n<br>" + string.Join("\n<br>\n", x.MatchedImages) + "\n<br>\n</div>")
                .Distinct()
                .ToList();

            string displayString = string.Join("\n<hr>\n", matchedImages);
        }

        private class ColorEqualityComparer : IEqualityComparer<Color>
        {
            public bool Equals(Color x, Color y)
            {
                return (x.R == y.R) && (x.G == y.G) && (x.B == y.B) && (x.A == y.A);
            }

            public int GetHashCode(Color color)
            {
                return color.GetHashCode();
            }
        }

        public static string GetImageHTML(ImageInfo image)
        {
            return $"<img {GetGreaterDimension(image)}=\"250px\" src=\"{image.Path}\"></img>";
        }

        public static (int, int) ResizeKeepAspect(int width, int height, int maxDimension)
        {
            float percent = Math.Min(
                maxDimension / (float)width,
                maxDimension / (float)height
            );

            return ((int)Math.Floor(width * percent), (int)Math.Floor(height * percent));
        }

        public static Bitmap ResizeImage(string filePath)
        {
            using Stream stream = File.OpenRead(filePath);
            using Image sourceImage = Image.FromStream(stream, false, false);

            (int newWidth, int newHeight) = ResizeKeepAspect(sourceImage.Width, sourceImage.Height, maxSize);

            Rectangle newRectangle = new Rectangle(0, 0, newWidth, newHeight);

            Bitmap resizedImage = new Bitmap(newWidth, newHeight);

            resizedImage.SetResolution(sourceImage.HorizontalResolution, sourceImage.VerticalResolution);

            using Graphics graphics = Graphics.FromImage(resizedImage);
            using ImageAttributes wrapMode = new ImageAttributes();

            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.InterpolationMode = InterpolationMode.Default;
            graphics.SmoothingMode = SmoothingMode.HighSpeed;
            graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            wrapMode.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(sourceImage, newRectangle, 0, 0, sourceImage.Width, sourceImage.Height, GraphicsUnit.Pixel, wrapMode);

            return resizedImage;
        }

        public static void FindSimilarImages(ImageInfo image, ConcurrentBag<ImageInfo> images)
        {
            image.MatchedImages.Add(GetImageHTML(image));

            image.MatchedImages = image.MatchedImages
                .Concat(images
                    .Where(x => !string.Equals(image.Path, x.Path)
                        && IsWithinSimilarityThreshold(image.R, x.R, similarityThreshold)
                        && IsWithinSimilarityThreshold(image.G, x.G, similarityThreshold)
                        && IsWithinSimilarityThreshold(image.B, x.B, similarityThreshold))
                    .Select(GetImageHTML)
                )
                .OrderByDescending(x => x)
                .ToList();
        }

        public static string GetGreaterDimension(ImageInfo image)
        {
            return image.Width > image.Height ? "width" : "height";
        }

        public static ImageInfo ExtractImageInformation(string filePath)
        {
            using Bitmap image = ResizeImage(filePath);

            ImageInfo imageInfo = new ImageInfo
            {
                Path = filePath,
                Width = image.Width,
                Height = image.Height
            };

            (imageInfo.R, imageInfo.G, imageInfo.B) = ExtractRGBByQuadrant(image, splitQuadrants);

            return imageInfo;
        }

        public static (long[,], long[,], long[,]) ExtractRGBByQuadrant(Bitmap image, int splitQuadrants)
        {
            List<Pixel> pixels = ExtractPixels(image).ToList();

            RemoveCommonPixels(pixels, similarPixelThreshold);

            Dictionary<int, Color> colorByPosition = pixels
                .ToDictionary(x => x.Point.GetHashCode(), x => x.Color);

            int elementsPerDimension = splitQuadrants / 2;

            List<int> quadrantW = DivideEvenly(image.Width, elementsPerDimension).ToList();
            List<int> quadrantH = DivideEvenly(image.Height, elementsPerDimension).ToList();

            long[,] R = new long[elementsPerDimension, elementsPerDimension];
            long[,] G = new long[elementsPerDimension, elementsPerDimension];
            long[,] B = new long[elementsPerDimension, elementsPerDimension];

            int curX = 0;
            int curY = 0;

            for (int i = 0; i < quadrantW.Count; i++)
            {
                for (int l = 0; l < quadrantH.Count; l++)
                {
                    for (int x = 0; x < quadrantW[i]; x++)
                    {
                        for (int y = 0; y < quadrantH[l]; y++)
                        {
                            Point point = new Point(x, y);

                            if (colorByPosition.TryGetValue(point.GetHashCode(), out Color color))
                            {
                                R[i, l] += color.R;
                                G[i, l] += color.G;
                                B[i, l] += color.B;
                            }
                        }
                    }

                    curX = 0;
                    curY += quadrantH[l];
                }

                curY = 0;
                curX += quadrantW[i];
            }

            return (R, G, B);
        }

        public static void RemoveCommonPixels(List<Pixel> pixels, double percentageToRemove)
        {
            Dictionary<Color, int> colorOccurrence = new Dictionary<Color, int>();

            foreach (Pixel pixel in pixels)
            {
                if (!colorOccurrence.ContainsKey(pixel.Color))
                {
                    colorOccurrence[pixel.Color] = 1;
                }
                else
                {
                    colorOccurrence[pixel.Color]++;
                }
            }

            List<Color> colorsToRemove = colorOccurrence
                .OrderByDescending(x => x.Value)
                .Select(x => x.Key)
                .Take((int)(colorOccurrence.Count * percentageToRemove))
                .ToList();

            pixels.RemoveAll(x => colorsToRemove.Contains(x.Color, new ColorEqualityComparer()));
        }

        public static IEnumerable<Pixel> ExtractPixels(Bitmap image)
        {
            for (int x = 0; x < image.Width; x++)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    yield return new Pixel(x, y, image.GetPixel(x, y));
                }
            }
        }

        public static int[] DivideEvenly(int numerator, int denominator)
        {
            int quotient = Math.DivRem(numerator, denominator, out int remainder);

            int[] results = new int[denominator];

            for (int i = 0; i < denominator; i++)
            {
                results[i] = i < remainder ? quotient + 1 : quotient;
            }

            return results;
        }

        public static bool IsWithinSimilarityThreshold(long[,] arr1, long[,] arr2, double threshold)
        {
            decimal acceptedPercentageDifference = (decimal)(1 - threshold);

            for (int y = 0; y < arr1.Rank; y++)
            {
                for (int x = 0; x < arr1.GetLength(y); x++)
                {
                    decimal acceptableDifference = Math.Max(arr1[x, y], arr2[x, y]) * acceptedPercentageDifference;

                    //if (arr2[x, y] == 0 || arr1[x, y]  == 0)
                    //{
                    //    continue;
                    //}
                    if (arr2[x, y] < arr1[x, y] - acceptableDifference
                        || arr2[x, y] > arr1[x, y] + acceptableDifference)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
