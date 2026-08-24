using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenPaste.Rendering;

/// <summary>
/// Shared pixel sampling for the layers that re-read what sits beneath them
/// (<see cref="BlurEffects"/> and <see cref="MagnifyEffects"/>).
/// </summary>
internal static class LayerSampler
{
    /// <summary>
    /// Crop <paramref name="want"/> out of <paramref name="src"/>. Where the rect runs past the
    /// bitmap (region dragged over the selection border) the available pixels are stretched to
    /// fill, which keeps the result fully opaque instead of punching a transparent hole.
    /// </summary>
    public static BitmapSource? Crop(BitmapSource src, Int32Rect want)
    {
        int x = Math.Clamp(want.X, 0, Math.Max(0, src.PixelWidth - 1));
        int y = Math.Clamp(want.Y, 0, Math.Max(0, src.PixelHeight - 1));
        int w = Math.Clamp(want.X + want.Width - x, 1, src.PixelWidth - x);
        int h = Math.Clamp(want.Y + want.Height - y, 1, src.PixelHeight - y);
        if (w < 1 || h < 1 || want.Width < 1 || want.Height < 1) return null;

        var crop = new CroppedBitmap(src, new Int32Rect(x, y, w, h));
        crop.Freeze();
        if (x == want.X && y == want.Y && w == want.Width && h == want.Height) return crop;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(crop, new Rect(0, 0, want.Width, want.Height));          // edge-extend
            dc.DrawImage(crop, new Rect(x - want.X, y - want.Y, w, h));           // exact pixels
        }
        var rtb = new RenderTargetBitmap(want.Width, want.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
