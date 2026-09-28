using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace VisyaDocs.App.Views;

/// <summary>
/// Keeps page rendering memory flat. Page bitmaps are large (tens of MB on high DPI screens), and a
/// dropped bitmap's native memory is only freed once .NET collects its wrapper, which the GC does not
/// see as pressure. So bitmaps of pages that scroll away are reused for the pages that scroll in,
/// discarded ones trigger a collection once enough piled up, and PDFium renders into one reused buffer
/// instead of a new array per page.
/// </summary>
internal static class BitmapPool
{
    private const long MaxPooledBytes = 96L << 20;
    private const long CollectAfterBytes = 128L << 20;

    private static readonly List<WriteableBitmap> s_free = [];
    private static long s_pooledBytes, s_discardedBytes;
    private static byte[]? s_buffer;

    private static long SizeOf(WriteableBitmap b) => (long)b.PixelWidth * b.PixelHeight * 4;

    /// <summary>A bitmap of exactly this size, reused when one is free.</summary>
    public static WriteableBitmap Rent(int width, int height)
    {
        int i = s_free.FindIndex(b => b.PixelWidth == width && b.PixelHeight == height);
        if (i < 0) return new WriteableBitmap(width, height);
        var bitmap = s_free[i];
        s_free.RemoveAt(i);
        s_pooledBytes -= SizeOf(bitmap);
        return bitmap;
    }

    /// <summary>Hands back a bitmap that is no longer shown.</summary>
    public static void Return(WriteableBitmap bitmap)
    {
        s_free.Add(bitmap);
        s_pooledBytes += SizeOf(bitmap);
        while (s_pooledBytes > MaxPooledBytes && s_free.Count > 0) Discard(0);
    }

    /// <summary>Frees every pooled bitmap and the render buffer (reading paused, or the window is hidden).</summary>
    public static void Trim()
    {
        s_trimTimer?.Stop();
        if (s_free.Count == 0 && s_buffer is null) return;
        while (s_free.Count > 0) Discard(0);
        s_buffer = null;
        Collect();
    }

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? s_trimTimer;

    /// <summary>
    /// Spare bitmaps only help while scrolling; once the view has been still for a moment they are
    /// freed, so an idle reader holds just the pages on screen.
    /// </summary>
    public static void TrimWhenIdle()
    {
        if (s_trimTimer is null)
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null) return;
            s_trimTimer = queue.CreateTimer();
            s_trimTimer.Interval = TimeSpan.FromSeconds(2);
            s_trimTimer.IsRepeating = false;
            s_trimTimer.Tick += (_, _) => Trim();
        }
        s_trimTimer.Stop();
        s_trimTimer.Start();
    }

    private static void Discard(int index)
    {
        s_discardedBytes += SizeOf(s_free[index]);
        s_pooledBytes -= SizeOf(s_free[index]);
        s_free.RemoveAt(index);
        if (s_discardedBytes >= CollectAfterBytes) Collect();
    }

    private static void Collect()
    {
        s_discardedBytes = 0;
        // Releases the native bitmaps of collected wrappers; the managed heap itself is small.
        GC.Collect();
    }

    /// <summary>Takes the shared render buffer (at least the given size); give it back with <see cref="ReturnBuffer"/>.</summary>
    public static byte[] RentBuffer(int bytes)
    {
        var buffer = Interlocked.Exchange(ref s_buffer, null);
        return buffer is not null && buffer.Length >= bytes ? buffer : new byte[bytes];
    }

    public static void ReturnBuffer(byte[] buffer)
    {
        var current = s_buffer;
        if (current is null || current.Length < buffer.Length) s_buffer = buffer;
    }

    /// <summary>Copies rendered BGRA pixels into a bitmap and shows them.</summary>
    public static void Fill(WriteableBitmap bitmap, byte[] pixels)
    {
        int count = bitmap.PixelWidth * bitmap.PixelHeight * 4;
        pixels.CopyTo(0, bitmap.PixelBuffer, 0, count);
        bitmap.Invalidate();
    }
}
