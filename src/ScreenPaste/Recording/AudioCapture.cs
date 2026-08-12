using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ScreenPaste.Recording;

/// <summary>
/// Captures system-audio loopback and/or the microphone via WASAPI, normalizes every
/// source to a common 48 kHz / stereo / 32-bit-float format, mixes them, and pushes the
/// result as a steady real-time PCM stream (f32le) to a consumer — the ffmpeg audio pipe.
///
/// Design notes:
/// - The mixer runs with ReadFully, so a silent or stalled source contributes silence
///   rather than a gap; the pump paces itself to wall-clock time, keeping audio roughly
///   aligned with the (also wall-clock-paced) video capture loop.
/// - Any source that fails to start (no device, in-use, elevated) is skipped. If none
///   start, <see cref="Active"/> is false and the caller records silently.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly List<IWaveIn> _captures = new();
    private readonly MixingSampleProvider _mixer;
    private Action<byte[], int>? _onPcm;
    private Thread? _pump;
    private volatile bool _stop;

    /// <summary>True once at least one source is live (devices opened and started).</summary>
    public bool Active { get; private set; }

    private AudioCapture()
    {
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true,   // dry sources become silence, never a gap
        };
    }

    /// <summary>
    /// Open and start capture for <paramref name="source"/> WITHOUT pumping yet. Returns an
    /// instance whose <see cref="Active"/> reports whether any device actually started, so
    /// the caller can decide whether to give the encoder an audio input before pumping.
    /// Always returns an instance (Dispose it either way).
    /// </summary>
    public static AudioCapture TryOpen(AudioSource source)
    {
        var ac = new AudioCapture();
        if (source.WantsSystem()) ac.TryAddSource(loopback: true);
        if (source.WantsMic()) ac.TryAddSource(loopback: false);
        if (ac._captures.Count == 0) return ac;   // nothing opened: caller records silently

        foreach (var c in ac._captures)
        {
            try { c.StartRecording(); }
            catch { /* one source failed to start; the others still play */ }
        }
        ac.Active = true;
        return ac;
    }

    /// <summary>Begin the real-time pump feeding mixed PCM to <paramref name="onPcm"/>.</summary>
    public void StartPump(Action<byte[], int> onPcm)
    {
        if (!Active || _pump != null) return;
        _onPcm = onPcm;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "AudioCapturePump" };
        _pump.Start();
    }

    private void TryAddSource(bool loopback)
    {
        try
        {
            IWaveIn capture = loopback ? new WasapiLoopbackCapture() : new WasapiCapture();
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                ReadFully = true,
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2),
            };
            capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

            _mixer.AddMixerInput(Normalize(buffer.ToSampleProvider()));
            _captures.Add(capture);
        }
        catch
        {
            // No such device / unavailable — skip this source.
        }
    }

    /// <summary>Force any source to 48 kHz / stereo / float so the mixer can sum them.</summary>
    private static ISampleProvider Normalize(ISampleProvider sp)
    {
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new TakeStereoSampleProvider(sp);

        if (sp.WaveFormat.SampleRate != SampleRate)
            sp = new WdlResamplingSampleProvider(sp, SampleRate);

        return sp;
    }

    private void PumpLoop()
    {
        const int frames = SampleRate / 50;        // 20 ms chunks
        int floats = frames * Channels;
        var samples = new float[floats];
        var bytes = new byte[floats * sizeof(float)];

        long interval = Stopwatch.Frequency / 50;  // ticks per 20 ms
        var sw = Stopwatch.StartNew();
        long next = 0;

        while (!_stop)
        {
            int n = _mixer.Read(samples, 0, floats);   // ReadFully → always == floats
            if (n > 0)
            {
                Buffer.BlockCopy(samples, 0, bytes, 0, n * sizeof(float));
                _onPcm?.Invoke(bytes, n * sizeof(float));
            }

            next += interval;
            long remaining = next - sw.ElapsedTicks;
            if (remaining > 0)
            {
                int ms = (int)(remaining * 1000 / Stopwatch.Frequency);
                if (ms > 0) Thread.Sleep(ms);
            }
            else
            {
                next = sw.ElapsedTicks;   // fell behind: rebase, don't burst
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _pump?.Join(1000);
        foreach (var c in _captures)
        {
            try { c.StopRecording(); } catch { /* already gone */ }
            try { c.Dispose(); } catch { /* ignore */ }
        }
        _captures.Clear();
    }

    /// <summary>Passes through the first two channels of a &gt;2-channel source (rare surround loopback).</summary>
    private sealed class TakeStereoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _srcCh;
        private float[] _scratch = Array.Empty<float>();

        public TakeStereoSampleProvider(ISampleProvider src)
        {
            _src = src;
            _srcCh = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int needSrc = frames * _srcCh;
            if (_scratch.Length < needSrc) _scratch = new float[needSrc];

            int gotSrc = _src.Read(_scratch, 0, needSrc);
            int gotFrames = gotSrc / _srcCh;
            for (int f = 0; f < gotFrames; f++)
            {
                buffer[offset + f * 2] = _scratch[f * _srcCh];
                buffer[offset + f * 2 + 1] = _scratch[f * _srcCh + 1];
            }
            return gotFrames * 2;
        }
    }
}
