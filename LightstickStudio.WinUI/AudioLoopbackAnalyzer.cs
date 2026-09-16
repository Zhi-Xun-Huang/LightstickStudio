using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LightstickStudio;

internal sealed class AudioLoopbackAnalyzer : IDisposable
{
    private readonly object _sync = new();
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly WasapiLoopbackCapture _capture;
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
        _capture = new WasapiLoopbackCapture(_device);
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
            var samples = DecodeMono(eventArgs.Buffer, eventArgs.BytesRecorded,
                _capture.WaveFormat);
            if (samples.Length == 0) return;
            var level = Analyze(samples, _capture.WaveFormat.SampleRate);
            lock (_sync)
            {
                _smoothed = level;
                _updatedAt = Environment.TickCount64;
            }
        }
        catch
        {
            // A single malformed or discontinuous WASAPI buffer is harmless.
        }
    }

    private double Analyze(float[] samples, int sampleRate)
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
        _floor = Math.Min(rms, _floor * 0.995 + rms * 0.005);
        _peak = Math.Max(rms, _peak * 0.965);
        var normalized = Math.Clamp(
            (rms - _floor) / Math.Max(_peak - _floor, 0.002), 0, 1);

        var bass = 0.0;
        foreach (var frequency in new[] { 50.0, 80.0, 110.0, 140.0, 170.0 })
        {
            bass += Goertzel(samples, sampleRate, frequency, mean);
        }
        bass /= 5;
        _bassAverage = _bassAverage * 0.94 + bass * 0.06;
        var beat = Math.Clamp((bass / Math.Max(_bassAverage, 1e-6) - 1.10) * 0.9, 0, 1);
        var target = Math.Clamp(normalized * 0.82 + beat * 0.45, 0, 1);
        var coefficient = target > _smoothed ? 0.62 : 0.18;
        return _smoothed + (target - _smoothed) * coefficient;
    }

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
}
