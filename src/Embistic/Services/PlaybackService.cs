using LibVLCSharp.Shared;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>Audio-only native libVLC playback with Windows WASAPI output selection.</summary>
public sealed class PlaybackService : IDisposable
{
    private readonly LibVLC _vlc;
    private readonly MediaPlayer _player;
    private readonly object _callbackGate = new();
    private Media? _media;
    private volatile bool _disposed;
    private int _volume = 65;
    private string _deviceId = "";

    public event Action? PlaybackEnded;
    public event Action<string>? Error;

    public PlaybackService()
    {
        Core.Initialize();
        _vlc = new LibVLC("--no-video", "--no-video-title-show", "--no-spu", "--aout=mmdevice", "--network-caching=1000", "--quiet");
        _player = new MediaPlayer(_vlc);
        _player.SetAudioOutput("mmdevice");
        _player.EndReached += OnEnded;
        _player.EncounteredError += OnError;
        _player.Playing += OnPlaying;
    }

    public long PositionMs => _disposed ? 0 : Math.Max(0, _player.Time);
    public long DurationMs => _disposed ? 0 : Math.Max(0, _player.Length);
    public bool IsPlaying => !_disposed && _player.IsPlaying;
    public bool IsPaused => !_disposed && _player.State == VLCState.Paused;
    public bool HasEnded => !_disposed && _player.State == VLCState.Ended;
    public bool IsSeekable => !_disposed && _player.IsSeekable;
    public string CurrentDeviceId => _disposed ? "" : _player.OutputDevice ?? _deviceId;
    public int Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 100); if (!_disposed) { _player.Volume = _volume; _player.Mute = _volume == 0; } }
    }

    public IReadOnlyList<AudioDevice> GetAudioDevices()
    {
        ThrowIfDisposed();
        var result = new List<AudioDevice> { new("", "系统默认输出") };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var device in _player.AudioOutputDeviceEnum)
            if (!string.IsNullOrWhiteSpace(device.DeviceIdentifier) && seen.Add(device.DeviceIdentifier)) result.Add(new(device.DeviceIdentifier, device.Description));
        if (result.Count == 1)
            foreach (var device in WindowsAudioDevices.Enumerate())
                if (seen.Add(device.Id)) result.Add(device);
        return result;
    }

    public void SetAudioDevice(string id)
    {
        ThrowIfDisposed();
        id ??= "";
        if (id.Length > 0 && !GetAudioDevices().Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("所选音频设备已断开，请刷新并选择其他输出。");
        _deviceId = id;
        ApplyOutputDevice();
    }

    public void Play(string url)
    {
        ThrowIfDisposed();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file"))
            throw new ArgumentException("音频地址无效。", nameof(url));
        _player.Stop();
        _media?.Dispose();
        _media = new Media(_vlc, uri);
        _media.AddOption(":no-video");
        _media.AddOption(":no-spu");
        ApplyOutputDevice();
        _player.Volume = _volume;
        _player.Mute = _volume == 0;
        if (!_player.Play(_media)) throw new InvalidOperationException("无法开始播放，请检查音频地址与服务器连接。");
    }

    public void Pause() { ThrowIfDisposed(); _player.SetPause(true); }
    public void Resume() { ThrowIfDisposed(); _player.SetPause(false); }
    public void Stop() { if (!_disposed) _player.Stop(); }
    public void Seek(long positionMs)
    {
        ThrowIfDisposed();
        if (!_player.IsSeekable) return;
        var duration = DurationMs;
        _player.Time = duration > 0 ? Math.Clamp(positionMs, 0, Math.Max(0, duration - 1)) : Math.Max(0, positionMs);
    }

    private void ApplyOutputDevice()
    {
        // Persist the selection for the next audio output, then move a running output immediately.
        _player.SetOutputDevice(_deviceId, "mmdevice");
        _player.SetOutputDevice(_deviceId);
    }

    private void OnPlaying(object? sender, EventArgs args)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_callbackGate)
            {
                if (_disposed) return;
                _player.Volume = _volume;
                _player.Mute = _volume == 0;
                ApplyOutputDevice();
            }
        });
    }

    private void OnEnded(object? sender, EventArgs args) => ThreadPool.QueueUserWorkItem(_ => { if (!_disposed) PlaybackEnded?.Invoke(); });
    private void OnError(object? sender, EventArgs args) => ThreadPool.QueueUserWorkItem(_ => { if (!_disposed) Error?.Invoke("音频播放失败，请检查服务器连接、文件格式或输出设备。"); });
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_callbackGate)
        {
            if (_disposed) return;
            _disposed = true;
            _player.EndReached -= OnEnded;
            _player.EncounteredError -= OnError;
            _player.Playing -= OnPlaying;
            _player.Stop();
            _player.Dispose();
            _media?.Dispose();
            _vlc.Dispose();
        }
    }
}
