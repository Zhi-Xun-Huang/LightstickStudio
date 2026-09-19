using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LightstickStudio;

internal sealed class AudioLoopbackAnalyzer : IDisposable
{
    private const int CaptureBufferMilliseconds = 20;
    private const double AnalysisWindowSeconds = 0.040;
    private const double AnalysisHopSeconds = 0.020;
    private const double ReferenceWindowSeconds = 0.040;
    private static readonly double[] BassFrequencies =
        { 50, 75, 100, 125, 150, 175 };

    private readonly object _sync = new();
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly LowLatencyLoopbackCapture _capture;
    private float[] _pendingSamples = Array.Empty<float>();
    private int _pendingSampleCount;
    private double _floor = 0.002;
    private double _peak = 0.05;
    private double _bassAverage = 0.005;
    private double _smoothed;
    private long _updatedAt;
    private bool _disposed;

    public string DeviceName => _device.FriendlyName;

    public AudioLoopbackAnalyzer()
    {
        _enumerator = new MMDeviceEnumerator();
        _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            ?? throw new InvalidOperationException("Windows 沒有預設音訊輸出裝置");
        // WasapiLoopbackCapture uses a 100 ms buffer by default. At 25 FPS that
        // repeats one audio result for two or three light frames and makes the
        // brightness visibly step. Request the same 40 ms cadence used by the
        // original Python controller and analyze fixed-size windows below.
        _capture = new LowLatencyLoopbackCapture(
            _device, CaptureBufferMilliseconds);
        _capture.DataAvailable += Capture_DataAvailable;
    }

    public void Start() => _capture.StartRecording();

    public (double Level, double AgeSeconds) Read()
    {
        lock (_sync)
        {
            var age = _updatedAt == 0
                ? double.PositiveInfinity
                : (Environment.TickCount64 - _updatedAt) / 1000.0;
            return (_smoothed, age);
        }
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        try
        {
            var incoming = DecodeMono(eventArgs.Buffer, eventArgs.BytesRecorded,
                _capture.WaveFormat);
            if (incoming.Length == 0) return;

            var sampleRate = _capture.WaveFormat.SampleRate;
            var windowSamples = Math.Max(1,
                (int)Math.Round(sampleRate * AnalysisWindowSeconds));
            var hopSamples = Math.Max(1,
                (int)Math.Round(sampleRate * AnalysisHopSeconds));
            AppendSamples(incoming);

            while (_pendingSampleCount >= windowSamples)
            {
                var window = new float[windowSamples];
                Array.Copy(_pendingSamples, window, windowSamples);
                _pendingSampleCount -= hopSamples;
                if (_pendingSampleCount > 0)
                {
                    Array.Copy(_pendingSamples, hopSamples,
                        _pendingSamples, 0, _pendingSampleCount);
                }

                var analyzedAt = Environment.TickCount64;
                var elapsedSeconds = hopSamples / (double)sampleRate;
                var level = Analyze(window, sampleRate, elapsedSeconds);
                lock (_sync)
                {
                    _smoothed = level;
                    _updatedAt = analyzedAt;
                }
            }
        }
        catch
        {
            // A single malformed or discontinuous WASAPI buffer is harmless.
        }
    }

    private void AppendSamples(float[] incoming)
    {
        var required = _pendingSampleCount + incoming.Length;
        if (_pendingSamples.Length < required)
        {
            Array.Resize(ref _pendingSamples,
                Math.Max(required, Math.Max(4096, _pendingSamples.Length * 2)));
        }
        Array.Copy(incoming, 0, _pendingSamples, _pendingSampleCount,
            incoming.Length);
        _pendingSampleCount = required;
    }

    private double Analyze(float[] samples, int sampleRate, double elapsedSeconds)
    {
        double mean = 0;
        foreach (var sample in samples) mean += sample;
        mean /= samples.Length;

        double energy = 0;
        foreach (var sample in samples)
        {
            var centered = sample - mean;
            energy += centered * centered;
        }
        var rms = Math.Sqrt(energy / samples.Length + 1e-12);
        var floorAlpha = TimeAdjustedCoefficient(0.005, elapsedSeconds);
        var peakRetention = Math.Pow(0.965,
            elapsedSeconds / ReferenceWindowSeconds);
        _floor = Math.Min(rms, _floor * (1 - floorAlpha) + rms * floorAlpha);
        _peak = Math.Max(rms, _peak * peakRetention);
        var normalized = Math.Clamp(
            (rms - _floor) / Math.Max(_peak - _floor, 0.002), 0, 1);

        var bass = 0.0;
        foreach (var frequency in BassFrequencies)
        {
            bass += Goertzel(samples, sampleRate, frequency, mean);
        }
        bass /= BassFrequencies.Length;
        var bassAlpha = TimeAdjustedCoefficient(0.06, elapsedSeconds);
        _bassAverage = _bassAverage * (1 - bassAlpha) + bass * bassAlpha;
        var beat = Math.Clamp((bass / Math.Max(_bassAverage, 1e-6) - 1.10) * 0.9, 0, 1);
        var target = Math.Clamp(normalized * 0.82 + beat * 0.45, 0, 1);
        var coefficient = TimeAdjustedCoefficient(
            target > _smoothed ? 0.62 : 0.18, elapsedSeconds);
        return _smoothed + (target - _smoothed) * coefficient;
    }

    private static double TimeAdjustedCoefficient(
        double referenceCoefficient, double elapsedSeconds) =>
        1 - Math.Pow(1 - referenceCoefficient,
            elapsedSeconds / ReferenceWindowSeconds);

    private static double Goertzel(float[] samples, int sampleRate,
        double frequency, double mean)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / sampleRate);
        double previous = 0, previous2 = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            var window = 0.5 - 0.5 * Math.Cos(
                2 * Math.PI * index / Math.Max(1, samples.Length - 1));
            var value = (samples[index] - mean) * window +
                        coefficient * previous - previous2;
            previous2 = previous;
            previous = value;
        }
        var power = previous2 * previous2 + previous * previous -
                    coefficient * previous * previous2;
        return Math.Sqrt(Math.Max(0, power)) / samples.Length;
    }

    private static float[] DecodeMono(byte[] buffer, int byteCount, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameSize = channels * bytesPerSample;
        var frames = byteCount / frameSize;
        var result = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = frame * frameSize + channel * bytesPerSample;
                sum += bytesPerSample switch
                {
                    4 => BitConverter.ToSingle(buffer, offset),
                    3 => ReadInt24(buffer, offset) / 8388608.0,
                    2 => BitConverter.ToInt16(buffer, offset) / 32768.0,
                    1 => (buffer[offset] - 128) / 128.0,
                    _ => 0,
                };
            }
            result[frame] = (float)(sum / channels);
        }
        return result;
    }

    private static int ReadInt24(byte[] buffer, int offset)
    {
        var value = buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture.StopRecording(); } catch { }
        _capture.DataAvailable -= Capture_DataAvailable;
        _capture.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
    }

    private sealed class LowLatencyLoopbackCapture : WasapiCapture
    {
        public LowLatencyLoopbackCapture(MMDevice device, int bufferMilliseconds)
            : base(device, useEventSync: true, bufferMilliseconds)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback |
            base.GetAudioClientStreamFlags();
    }
}
