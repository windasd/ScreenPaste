using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Size = System.Drawing.Size;

namespace ScreenPaste.Recording;

/// <summary>Thrown when no bundled or system ffmpeg executable can be located.</summary>
public sealed class FFmpegNotFoundException : Exception { }

/// <summary>
/// Captures a fixed screen region on a background thread at a target frame rate and
/// pipes each frame to <see cref="FFmpegEncoder"/>. Encoder-agnostic capture pipeline —
/// the chosen <see cref="RecordingFormat"/> only affects the ffmpeg output stage.
/// </summary>
public sealed class ScreenRecorder
{
    private readonly int _w, _h, _fps;
    private volatile int _x, _y;              // capture origin; movable mid-recording
    private readonly RecordingFormat? _format;
    private readonly bool _captureCursor;
    private readonly AudioSource _audioSource;

    private FFmpegEncoder? _encoder;
    private AudioCapture? _audio;
    private Bitmap? _bmp;
    private Graphics? _g;
    private byte[] _buffer = Array.Empty<byte>();
    private Thread? _thread;
    private volatile bool _stop;

    public string OutputPath { get; }

    /// <summary>Final format, or null when recording an intermediate MP4 for the editor.</summary>
    public RecordingFormat? Format => _format;
    public int Fps => _fps;
    public int FrameWidth => _w;
    public int FrameHeight => _h;

    /// <summary>True when audio was requested and at least one device is live; false means
    /// the recording is silent (either not requested, or degraded — no usable device).</summary>
    public bool AudioActive { get; private set; }

    /// <summary>True when audio was requested but could not be captured (degraded to silent).</summary>
    public bool AudioDegraded { get; private set; }

    /// <summary>
    /// Move the capture origin mid-recording (the frame SIZE is fixed by the encoder
    /// stream). The capture loop picks the new origin up on its next frame.
    /// </summary>
    public void MoveTo(int x, int y)
    {
        _x = x;
        _y = y;
    }

    /// <param name="format">Final output format, or null to record a near-lossless
    /// intermediate MP4 that the post-recording editor trims and re-encodes.</param>
    public ScreenRecorder(Int32Rect screenRegion, int fps, RecordingFormat? format,
                          string outputPath, bool captureCursor, AudioSource audioSource = AudioSource.None)
    {
        _x = screenRegion.X;
        _y = screenRegion.Y;
        // Force even dimensions — required by H.264 (mp4) and harmless for gif/webp.
        _w = Math.Max(2, screenRegion.Width) & ~1;
        _h = Math.Max(2, screenRegion.Height) & ~1;
        _fps = Math.Clamp(fps, 2, 30);
        _format = format;
        _captureCursor = captureCursor;
        _audioSource = audioSource;
        OutputPath = outputPath;
    }

    /// <summary>Locate ffmpeg, start the encoder, and begin capturing. Throws if ffmpeg is missing.</summary>
    public void Start()
    {
        var ffmpeg = FFmpegLocator.Find() ?? throw new FFmpegNotFoundException();

        // Audio only rides in a container that carries it: the intermediate MP4 (null
        // format) or a direct-save MP4. GIF/WebP are silent, so don't even open a device.
        bool audioContainer = _format is null or RecordingFormat.Mp4;
        AudioStreamInfo? audioInfo = null;
        if (_audioSource != AudioSource.None && audioContainer)
        {
            var probe = AudioCapture.TryOpen(_audioSource);
            if (probe.Active)
            {
                _audio = probe;
                AudioActive = true;
                audioInfo = new AudioStreamInfo(AudioCapture.SampleRate, AudioCapture.Channels);
            }
            else
            {
                probe.Dispose();
                AudioDegraded = true;   // requested, but no usable device → record silently
            }
        }

        _encoder = new FFmpegEncoder(ffmpeg, _w, _h, _fps, _format, OutputPath, audioInfo);
        _audio?.StartPump((buf, len) => _encoder!.WriteAudio(buf, len));

        _bmp = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);
        _g = Graphics.FromImage(_bmp);
        _buffer = new byte[_w * _h * 4];

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ScreenRecorder" };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        long interval = Stopwatch.Frequency / _fps;
        var sw = Stopwatch.StartNew();
        long next = 0;

        while (!_stop)
        {
            CaptureFrame();

            next += interval;
            long remaining = next - sw.ElapsedTicks;
            if (remaining > 0)
            {
                int ms = (int)(remaining * 1000 / Stopwatch.Frequency);
                if (ms > 0) Thread.Sleep(ms);
            }
            else
            {
                // Falling behind — rebase so we don't burst to catch up.
                next = sw.ElapsedTicks;
            }
        }
    }

    private void CaptureFrame()
    {
        if (_g == null || _bmp == null || _encoder == null) return;

        // Snapshot the origin once so the frame and its cursor overlay agree even if
        // the region is being dragged mid-frame.
        int x = _x, y = _y;
        _g.CopyFromScreen(x, y, 0, 0, new Size(_w, _h), CopyPixelOperation.SourceCopy);
        if (_captureCursor) CursorCapture.Draw(_g, x, y);

        var rect = new Rectangle(0, 0, _w, _h);
        BitmapData data = _bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = _w * 4;
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, _buffer, 0, _buffer.Length);
            }
            else
            {
                for (int row = 0; row < _h; row++)
                    Marshal.Copy(data.Scan0 + row * data.Stride, _buffer, row * rowBytes, rowBytes);
            }
        }
        finally
        {
            _bmp.UnlockBits(data);
        }

        _encoder.WriteFrame(_buffer, _buffer.Length);
    }

    /// <summary>Stop capturing, flush ffmpeg, and return whether the file was written.</summary>
    public async Task<bool> StopAsync()
    {
        _stop = true;
        _thread?.Join(3000);

        // Stop the audio pump first so no more PCM is written, then let FinishAsync EOF the
        // pipe and flush ffmpeg.
        _audio?.Dispose();
        _audio = null;

        bool ok = _encoder != null && await _encoder.FinishAsync();

        _g?.Dispose();
        _bmp?.Dispose();
        _encoder?.Dispose();
        _g = null;
        _bmp = null;
        _encoder = null;

        return ok;
    }
}
