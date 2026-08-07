using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ScreenPaste.Editor;

namespace ScreenPaste.Rendering;

/// <summary>The kind and strength a blur region was drawn with; parked on the region's Tag.</summary>
public sealed record BlurSpec(BlurKind Kind, double Strength);

/// <summary>
/// Builds the visual for a blur region. A region is a clipping <see cref="Canvas"/> holding a
/// single <see cref="Image"/>; the image is re-sampled from whatever currently sits *beneath*
/// the blur layer, so a region hides the strokes and shapes under it and can be dragged to a
/// new spot without carrying its original pixels along.
/// </summary>
public static class BlurEffects
{
    /// <summary>Create an (initially empty) region visual; call <see cref="Resample"/> to fill it.</summary>
    public static FrameworkElement Create(BlurKind kind, Rect region, double strength)
    {
        var inner = new Image { Stretch = Stretch.Fill };
        if (kind == BlurKind.Mosaic)
            RenderOptions.SetBitmapScalingMode(inner, BitmapScalingMode.NearestNeighbor);
        else
            inner.Effect = new BlurEffect
            {
                Radius = Math.Max(0.1, strength),
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance,
            };

        var host = new Canvas
        {
            Width = Math.Max(1, region.Width),
            Height = Math.Max(1, region.Height),
            ClipToBounds = true,
            Tag = new BlurSpec(kind, strength),
        };
        host.Children.Add(inner);
        Canvas.SetLeft(host, region.X);
        Canvas.SetTop(host, region.Y);
        return host;
    }

    /// <summary>
    /// Re-read the pixels under <paramref name="host"/> from <paramref name="beneath"/> (the
    /// composite of everything below the blur layer, in region-local coords) and re-apply the
    /// effect. Safe to call on every drag frame.
    /// </summary>
    public static void Resample(FrameworkElement host, BitmapSource beneath)
    {
        if (host is not Canvas c || c.Tag is not BlurSpec spec) return;
        if (c.Children.Count == 0 || c.Children[0] is not Image inner) return;

        double x = Canvas.GetLeft(c), y = Canvas.GetTop(c);
        if (double.IsNaN(x)) x = 0;
        if (double.IsNaN(y)) y = 0;
        if (c.RenderTransform is TranslateTransform tt) { x += tt.X; y += tt.Y; }

        int w = (int)Math.Round(c.Width), h = (int)Math.Round(c.Height);
        if (w < 1 || h < 1) return;

        // Gaussian bleeds outward, so sample a margin and let the host's clip trim it; without
        // the margin the blurred edge fades to transparent and leaks what is underneath.
        int pad = spec.Kind == BlurKind.Mosaic ? 0 : (int)Math.Ceiling(Math.Max(0.1, spec.Strength)) + 1;
        var want = new Int32Rect((int)Math.Round(x) - pad, (int)Math.Round(y) - pad, w + 2 * pad, h + 2 * pad);

        var source = Sample(beneath, want);
        if (source == null) { inner.Source = null; return; }

        if (spec.Kind == BlurKind.Mosaic)
        {
            double block = Math.Clamp(spec.Strength, 2, Math.Min(want.Width, want.Height));
            var small = new TransformedBitmap(source, new ScaleTransform(1 / block, 1 / block));
            small.Freeze();
            source = small;
        }

        inner.Source = source;
        inner.Width = want.Width;
        inner.Height = want.Height;
        Canvas.SetLeft(inner, -pad);
        Canvas.SetTop(inner, -pad);
    }

    /// <summary>
    /// Crop <paramref name="want"/> out of <paramref name="src"/>. Where the rect runs past the
    /// bitmap (region dragged over the selection border) the available pixels are stretched to
    /// fill, which keeps the result fully opaque instead of punching a transparent hole.
    /// </summary>
    private static BitmapSource? Sample(BitmapSource src, Int32Rect want)
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
