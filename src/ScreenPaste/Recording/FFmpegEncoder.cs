using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace ScreenPaste.Recording;

/// <summary>Audio muxed alongside the video: raw PCM (f32le) fed over a named pipe.</summary>
public readonly record struct AudioStreamInfo(int SampleRate, int Channels);

/// <summary>
/// Pipes raw BGRA frames into a bundled ffmpeg process, which encodes them either to a
/// final format (GIF/MP4/WebP) or — when no format is given — to a near-lossless
/// intermediate MP4 that the post-recording editor previews and re-encodes from.
/// When <see cref="AudioStreamInfo"/> is supplied, a second (audio) input is fed as raw
/// f32le PCM over a Windows named pipe and muxed into the output.
/// One instance per recording.
/// </summary>
public sealed class FFmpegEncoder : IDisposable
{
    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly StringBuilder _stderrTail = new();
    private readonly NamedPipeServerStream? _audioPipe;
    private volatile bool _audioConnected;
    private bool _faulted;
    private bool _finished;

    public string OutputPath { get; }

    /// <summary>The tail of ffmpeg's stderr — useful for diagnosing an encode failure.</summary>
    public string Diagnostics => _stderrTail.ToString();

    public FFmpegEncoder(string ffmpegPath, int width, int height, int fps,
                         RecordingFormat? format, string outputPath, AudioStreamInfo? audio = null)
    {
        OutputPath = outputPath;

        string? audioPipePath = null;
        if (audio is { } a)
        {
            // Async server so WriteAudio can block-write while ffmpeg drains the pipe.
            string name = "screenpaste_aud_" + Guid.NewGuid().ToString("N");
            _audioPipe = new NamedPipeServerStream(name, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            audioPipePath = @"\\.\pipe\" + name;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in BuildArguments(width, height, fps, format, outputPath, audio, audioPipePath))
            psi.ArgumentList.Add(arg);

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            // Keep only the last ~4 KB so a long run does not grow unbounded.
            _stderrTail.AppendLine(e.Data);
            if (_stderrTail.Length > 4096) _stderrTail.Remove(0, _stderrTail.Length - 4096);
        };
        _proc.Start();
        _proc.BeginErrorReadLine();
        _stdin = _proc.StandardInput.BaseStream;

        // ffmpeg opens the audio pipe as a client right after launch; accept it in the
        // background so leading audio (dropped until connected) never blocks startup.
        if (_audioPipe != null)
        {
            _ = _audioPipe.WaitForConnectionAsync().ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) _audioConnected = true;
            });
        }
    }

    private static IEnumerable<string> BuildArguments(int w, int h, int fps,
        RecordingFormat? format, string output, AudioStreamInfo? audio, string? audioPipePath)
    {
        var args = new List<string>
        {
            "-y",
            // Video input (input 0): raw BGRA over stdin.
            "-f", "rawvideo",
            "-pixel_format", "bgra",
            "-video_size", $"{w}x{h}",
            "-framerate", fps.ToString(),
            "-i", "pipe:0",
        };

        if (audio is { } a)
        {
            // Audio input (input 1): raw float PCM over the named pipe.
            args.AddRange(new[]
            {
                "-f", "f32le", "-ar", a.SampleRate.ToString(), "-ac", a.Channels.ToString(),
                "-i", audioPipePath!,
                "-map", "0:v:0", "-map", "1:a:0",
            });
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(format is { } f ? OutputArgs(f, fps) : IntermediateArgs);

        if (audio != null)
        {
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        }

        args.Add(output);
        return args;
    }

    /// <summary>
    /// Near-lossless, very fast H.264 for the editor's intermediate file: ultrafast keeps
    /// the live capture loop cheap, and MediaElement can play it back for preview.
    /// </summary>
    private static readonly string[] IntermediateArgs =
    {
        "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12",
        "-vf", "format=yuv420p",
        "-movflags", "+faststart",
    };

    /// <summary>Codec/filter arguments for a final export; shared with <see cref="RecordingExporter"/>.</summary>
    public static IReadOnlyList<string> OutputArgs(RecordingFormat format, int fps)
    {
        var args = new List<string>();
        if (FilterFragment(format) is { } frag)
        {
            args.Add("-vf");
            args.Add(frag);
        }
        args.AddRange(CodecArgs(format, fps));
        return args;
    }

    /// <summary>
    /// Format-specific filtergraph tail (no in/out labels), or null. Kept separate from
    /// <see cref="CodecArgs"/> so the exporter can splice it into a -filter_complex graph
    /// after blur/overlay stages.
    /// </summary>
    public static string? FilterFragment(RecordingFormat format) => format switch
    {
        RecordingFormat.Mp4 => "format=yuv420p",
        RecordingFormat.WebP => null,
        // GIF — one-pass palette generation for good quality.
        _ => "split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a",
    };

    /// <summary>Encoder/muxer arguments for a final export (excluding any filtergraph).</summary>
    public static IReadOnlyList<string> CodecArgs(RecordingFormat format, int fps) => format switch
    {
        RecordingFormat.Mp4 => new[]
        {
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-movflags", "+faststart",
            "-r", fps.ToString(),
        },
        RecordingFormat.WebP => new[]
        {
            "-c:v", "libwebp", "-lossless", "0", "-q:v", "75",
            "-loop", "0", "-r", fps.ToString(),
        },
        _ => new[] { "-loop", "0" },
    };

    /// <summary>Feed one frame (raw BGRA, top-down). Silently drops frames once faulted.</summary>
    public void WriteFrame(byte[] bgra, int length)
    {
        if (_faulted || _finished) return;
        try
        {
            _stdin.Write(bgra, 0, length);
        }
        catch
        {
            // ffmpeg died (e.g. bad args) — stop feeding; failure surfaces at FinishAsync.
            _faulted = true;
        }
    }

    /// <summary>
    /// Feed PCM audio (raw f32le). Dropped until ffmpeg has connected to the pipe and after
    /// the encoder has faulted or finished. Once committed, the pump must keep calling this
    /// until <see cref="FinishAsync"/> so ffmpeg's audio input never stalls the mux.
    /// </summary>
    public void WriteAudio(byte[] pcm, int length)
    {
        if (_audioPipe == null || !_audioConnected || _faulted || _finished) return;
        try
        {
            _audioPipe.Write(pcm, 0, length);
        }
        catch
        {
            _faulted = true;
        }
    }

    /// <summary>Close the pipe and wait for ffmpeg to flush the file. Returns true on success.</summary>
    public async Task<bool> FinishAsync()
    {
        if (_finished) return !_faulted && _proc.ExitCode == 0;
        _finished = true;

        // EOF both inputs so ffmpeg finalizes: stdin (video) and the audio pipe.
        try { _stdin.Flush(); _stdin.Close(); } catch { /* already gone */ }
        try { _audioPipe?.Dispose(); } catch { /* already gone */ }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _proc.WaitForExitAsync(cts.Token);
        }
        catch
        {
            try { if (!_proc.HasExited) _proc.Kill(); } catch { /* ignore */ }
            return false;
        }

        return !_faulted && _proc.ExitCode == 0;
    }

    public void Dispose()
    {
        try { _audioPipe?.Dispose(); } catch { /* ignore */ }
        try { if (!_proc.HasExited) _proc.Kill(); } catch { /* ignore */ }
        _proc.Dispose();
    }
}
