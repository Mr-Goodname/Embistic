using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Embistic.Services;

/// <summary>
/// Reads the selected Windows render endpoint through WASAPI loopback.
/// It does not alter output volume or route audio. Callbacks occur on the capture thread.
/// </summary>
public sealed class AudioSpectrumService : IDisposable
{
    private readonly object _gate = new();
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private MMDeviceEnumerator? _enumerator;
    private AudioSpectrumAnalyzer? _analyzer;
    private bool _disposed;
    private int _generation;

    public int SampleRate { get; private set; } = 48000;
    public bool IsRunning { get { lock (_gate) return _capture is not null; } }
    public string EndpointId { get; private set; } = "";
    public event Action<float[]>? SpectrumReady;
    public event Action<string>? StatusChanged;

    public void Start(string endpointId = "")
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not null && string.Equals(EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)) return;
        }
        Stop();
        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WasapiLoopbackCapture? capture = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = string.IsNullOrWhiteSpace(endpointId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(endpointId);
            if (device.State != DeviceState.Active) throw new InvalidOperationException("音频输出设备当前不可用。");
            capture = new WasapiLoopbackCapture(device);
            var analyzer = new AudioSpectrumAnalyzer(capture.WaveFormat);
            int generation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                generation = ++_generation;
                _enumerator = enumerator; _device = device; _capture = capture; _analyzer = analyzer;
                EndpointId = endpointId;
                SampleRate = capture.WaveFormat.SampleRate;
            }
            capture.DataAvailable += (_, e) =>
            {
                if (generation != Volatile.Read(ref _generation)) return;
                try
                {
                    analyzer.ProcessPcm(e.Buffer.AsSpan(0, e.BytesRecorded), magnitudes =>
                    {
                        if (generation == Volatile.Read(ref _generation)) SpectrumReady?.Invoke(magnitudes);
                    });
                }
                catch { StatusChanged?.Invoke("暂时无法分析音频输出，请刷新输出设备。"); }
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (generation == Volatile.Read(ref _generation) && e.Exception is not null)
                {
                    StatusChanged?.Invoke("音频输出已变化，频谱捕获暂时停止。");
                    // Disposing from the native recording callback can wait on that same
                    // thread. Release the failed capture only after the callback returns.
                    _ = Task.Run(() =>
                    {
                        lock (_gate) { if (generation != _generation) return; }
                        Stop();
                    });
                }
            };
            capture.StartRecording();
            StatusChanged?.Invoke("频谱跟随当前本机音频输出");
        }
        catch (Exception ex)
        {
            // Avoid leaving a failed session registered as active.
            lock (_gate)
            {
                ++_generation;
                if (_capture == capture) { _capture = null; _device = null; _enumerator = null; _analyzer = null; }
            }
            capture?.Dispose(); device?.Dispose(); enumerator?.Dispose();
            StatusChanged?.Invoke(ex is InvalidOperationException ? ex.Message : "无法捕获此音频设备，请选择其他本机输出。");
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? capture;
        MMDevice? device;
        MMDeviceEnumerator? enumerator;
        lock (_gate)
        {
            ++_generation;
            capture = _capture; device = _device; enumerator = _enumerator;
            _capture = null; _device = null; _enumerator = null; _analyzer = null;
            EndpointId = "";
        }
        if (capture is null) return;
        try { capture.StopRecording(); } catch { }
        capture.Dispose(); device?.Dispose(); enumerator?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        Stop();
    }
}

/// <summary>Streaming PCM-to-FFT conversion. Fragmented input is carried across calls.</summary>
internal sealed class AudioSpectrumAnalyzer
{
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private readonly WaveFormat _format;
    private readonly bool _floating;
    private readonly int _bytesPerSample, _frameBytes, _fftSize, _fftExponent, _hop;
    private readonly float[] _samples, _window;
    private readonly Complex[] _fft;
    private readonly byte[] _carry;
    private int _carryCount, _write, _filled, _untilNext;
    public int SampleRate => _format.SampleRate;
    public int FftSize => _fftSize;

    public AudioSpectrumAnalyzer(WaveFormat format, int fftSize = 2048)
    {
        if (fftSize is < 256 or > 16384 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(fftSize));
        _format = format;
        _floating = format.Encoding == WaveFormatEncoding.IeeeFloat
            || format is WaveFormatExtensible ext && ext.SubFormat == FloatSubtype;
        var pcm = format.Encoding == WaveFormatEncoding.Pcm
            || format is WaveFormatExtensible pcmExt && pcmExt.SubFormat == PcmSubtype;
        if ((!_floating && !pcm) || format.Channels < 1 || format.Channels > 32
            || format.SampleRate < 8000 || (_floating && format.BitsPerSample is not (32 or 64))
            || (!_floating && format.BitsPerSample is not (8 or 16 or 24 or 32)))
            throw new NotSupportedException("此音频设备的采样格式不支持频谱分析。");
        _bytesPerSample = format.BitsPerSample / 8;
        _frameBytes = _bytesPerSample * format.Channels;
        if (format.BlockAlign != _frameBytes) throw new NotSupportedException("不支持的音频帧格式。");
        _fftSize = fftSize; _fftExponent = (int)Math.Log2(fftSize); _hop = fftSize / 2;
        _samples = new float[fftSize]; _window = new float[fftSize]; _fft = new Complex[fftSize];
        _carry = new byte[_frameBytes];
        for (var i = 0; i < fftSize; i++) _window[i] = (float)(.5 * (1 - Math.Cos(2 * Math.PI * i / (fftSize - 1))));
    }

    public void ProcessPcm(ReadOnlySpan<byte> buffer, Action<float[]> publish)
    {
        if (_carryCount > 0)
        {
            var take = Math.Min(_frameBytes - _carryCount, buffer.Length);
            buffer[..take].CopyTo(_carry.AsSpan(_carryCount));
            _carryCount += take; buffer = buffer[take..];
            if (_carryCount == _frameBytes) { AddFrame(_carry, publish); _carryCount = 0; }
        }
        while (buffer.Length >= _frameBytes) { AddFrame(buffer[.._frameBytes], publish); buffer = buffer[_frameBytes..]; }
        if (buffer.Length > 0) { buffer.CopyTo(_carry); _carryCount = buffer.Length; }
    }

    private void AddFrame(ReadOnlySpan<byte> frame, Action<float[]> publish)
    {
        double sum = 0;
        for (var channel = 0; channel < _format.Channels; channel++)
        {
            var sample = frame.Slice(channel * _bytesPerSample, _bytesPerSample);
            double value;
            if (_floating)
                value = _bytesPerSample == 4 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample))
                    : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(sample));
            else value = _bytesPerSample switch
            {
                1 => (sample[0] - 128) / 128.0,
                2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0,
                3 => ReadInt24(sample) / 8388608.0,
                _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0
            };
            if (double.IsFinite(value)) sum += Math.Clamp(value, -1, 1);
        }
        _samples[_write] = (float)(sum / _format.Channels);
        _write = (_write + 1) & (_fftSize - 1);
        if (_filled < _fftSize) { _filled++; if (_filled < _fftSize) return; }
        else if (--_untilNext > 0) return;
        _untilNext = _hop;
        for (var i = 0; i < _fftSize; i++)
        {
            _fft[i].X = _samples[(_write + i) & (_fftSize - 1)] * _window[i];
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, _fftExponent, _fft);
        var magnitudes = new float[_fftSize / 2 + 1];
        for (var i = 0; i < magnitudes.Length; i++)
            magnitudes[i] = 4 * MathF.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
        publish(magnitudes);
    }

    private static int ReadInt24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | sample[1] << 8 | sample[2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }
}
