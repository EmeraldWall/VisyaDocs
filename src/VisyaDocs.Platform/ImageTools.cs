using System.Runtime.InteropServices.WindowsRuntime;
using VisyaDocs.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace VisyaDocs.Platform;

public sealed record DecodedImage(byte[] Bgra, int Width, int Height);

/// <summary>Image encoding and decoding through Windows Imaging Component (no bundled codecs).</summary>
public static class ImageTools
{
    public static readonly string[] SupportedImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".webp"];

    /// <summary>Encodes BGRA pixels as JPEG. Matches <see cref="Core.Export.JpegEncoder"/>; call it off the UI thread.</summary>
    public static void EncodeJpeg(Stream output, byte[] bgra, int width, int height) =>
        EncodeJpegAsync(output, bgra, width, height).GetAwaiter().GetResult();

    public static async Task EncodeJpegAsync(Stream output, byte[] bgra, int width, int height, double quality = 0.9)
    {
        using var memory = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, memory, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        memory.Seek(0);
        await memory.AsStreamForRead().CopyToAsync(output);
    }

    public static async Task<DecodedImage> DecodeAsync(string path)
    {
        using var stream = await OpenAsync(path);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return new DecodedImage(data.DetachPixelData(), (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
    }

    /// <summary>
    /// Appends each image as a page. Upright JPEGs are embedded as is (no quality loss, small
    /// files); other formats and rotated photos are decoded first.
    /// </summary>
    public static async Task AppendImagesAsync(PdfDocument doc, IReadOnlyList<string> paths, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string path = paths[i];
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if ((ext == ".jpg" || ext == ".jpeg") && await IsUprightAsync(path))
            {
                byte[] jpeg = await File.ReadAllBytesAsync(path, ct);
                await Task.Run(() => doc.AppendJpegPage(jpeg), ct);
            }
            else
            {
                var image = await DecodeAsync(path);
                await Task.Run(() => doc.AppendImagePage(image.Bgra, image.Width, image.Height), ct);
            }
            progress?.Report((i + 1.0) / paths.Count);
        }
    }

    private static async Task<bool> IsUprightAsync(string path)
    {
        using var stream = await OpenAsync(path);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        try
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.Orientation"]);
            return !props.TryGetValue("System.Photo.Orientation", out var value) || Convert.ToInt32(value.Value) == 1;
        }
        catch
        {
            return true; // no metadata block: the pixels are stored upright
        }
    }

    private static async Task<IRandomAccessStream> OpenAsync(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        return stream;
    }
}
