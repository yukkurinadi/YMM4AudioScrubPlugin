using HarmonyLib;
using NAudio.Wave;

namespace AudioScrub;

static class AudioScrubEngine
{
    static readonly object PlayLock = new();
    static IWavePlayer? _player;
    static ScrubWaveProvider? _buffer;
    static int _deviceHz;

    public static void OnPlayerCreated(object audioPlayer)
    {
        var timeline = GetMember(audioPlayer, "timeline");
        var scenes = GetMember(audioPlayer, "scenes");
        if (timeline is not null)
            MixCache.Attach(timeline, scenes);
        EnsureDevice(MixCache.Hz);
    }

    /// <summary>
    /// PreviewViewModel.SeekAsync(int) のみ。シークバー・矢印キー。再生ヘッド追従では呼ばれない。
    /// </summary>
    public static void OnUserSeek(object previewViewModel, int frame)
    {
        var player = GetMember(previewViewModel, "player");
        if (player is not null && IsPlaying(player))
            return;

        var timeline = GetMember(previewViewModel, "timeline");
        var scenes = GetMember(previewViewModel, "scenes")
            ?? (player is null ? null : GetMember(player, "scenes"));
        if (timeline is null)
            return;

        MixCache.Attach(timeline, scenes);
        QueuePlay(FrameToTime(timeline, frame));
    }

    static void QueuePlay(TimeSpan time)
    {
        var settings = TrySettings();
        if (settings is { Enabled: false })
            return;

        var volume = (settings?.Volume ?? 100) / 100.0;
        var frames = Math.Max(1, settings?.FrameCount ?? 1);

        _ = Task.Run(() =>
        {
            try
            {
                PlayFromCache(time, frames, volume);
            }
            catch (Exception ex)
            {
                AudioScrubLog.Write("PlayFromCache: " + ex);
            }
        });
    }

    static void PlayFromCache(TimeSpan time, int frames, double volume)
    {
        var hz = Math.Max(1, MixCache.Hz);
        var fps = Math.Max(1, MixCache.Fps);
        var want = hz * 2 / fps * Math.Max(1, frames);
        var play = MixCache.GetSamples(time, want);
        if (play is null || play.Length < 2)
        {
            AudioScrubLog.Write($"cache miss t={time} frames={frames}");
            return;
        }

        if (volume != 1.0)
        {
            for (var i = 0; i < play.Length; i++)
                play[i] = (float)(play[i] * volume);
        }
        var fadeMs = Math.Clamp(1000 / fps / 4, 2, 8);
        ApplyRaisedCosineFades(play, hz, fadeMs);

        var pcm = FloatToPcm16(play);
        lock (PlayLock)
        {
            EnsureDevice(hz);
            if (_buffer is null || _player is null)
                return;
            _buffer.SetClip(pcm);
            if (_player.PlaybackState != PlaybackState.Playing)
                _player.Play();
            AudioScrubLog.Write($"scrub frames={frames} samples={play.Length} hz={hz} fps={fps}");
        }
    }

    static void ApplyRaisedCosineFades(float[] stereo, int hz, int fadeMs)
    {
        var frames = stereo.Length / 2;
        var fade = Math.Max(1, hz * fadeMs / 1000);
        fade = Math.Min(fade, Math.Max(1, frames / 2));
        for (var i = 0; i < fade; i++)
        {
            var g = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / fade));
            stereo[i * 2] *= g;
            stereo[i * 2 + 1] *= g;
            var j = frames - 1 - i;
            stereo[j * 2] *= g;
            stereo[j * 2 + 1] *= g;
        }
    }

    static void EnsureDevice(int hz)
    {
        if (hz <= 0)
            hz = 48000;
        if (_player is not null && _buffer is not null && _deviceHz == hz)
            return;

        DisposeDevice();
        var format = new WaveFormat(hz, 16, 2);
        _buffer = new ScrubWaveProvider(format);
        try
        {
            var wasapi = new WasapiOut();
            wasapi.Init(_buffer);
            _player = wasapi;
        }
        catch (Exception ex)
        {
            AudioScrubLog.Write("WasapiOut failed: " + ex.Message);
            var waveOut = new WaveOutEvent();
            waveOut.Init(_buffer);
            _player = waveOut;
        }
        _deviceHz = hz;
        AudioScrubLog.Write($"device open hz={hz}");
    }

    static void DisposeDevice()
    {
        try { _player?.Stop(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;
        _buffer = null;
    }

    static AudioScrubSettings? TrySettings()
    {
        try { return AudioScrubSettings.Default; }
        catch { return null; }
    }

    static bool IsPlaying(object obj)
        => AccessTools.Property(obj.GetType(), "IsPlaying")?.GetValue(obj) is true;

    static object? GetMember(object instance, string name)
    {
        var t = instance.GetType();
        return AccessTools.Field(t, name)?.GetValue(instance)
            ?? AccessTools.Property(t, name)?.GetValue(instance)
            ?? AccessTools.Field(t, "<" + name + ">k__BackingField")?.GetValue(instance);
    }

    static TimeSpan FrameToTime(object? timeline, int frame)
    {
        if (timeline is null)
            return TimeSpan.Zero;
        var videoInfo = AccessTools.Property(timeline.GetType(), "VideoInfo")?.GetValue(timeline);
        var getTime = videoInfo is null ? null : AccessTools.Method(videoInfo.GetType(), "GetTimeFrom", [typeof(int)]);
        if (getTime?.Invoke(videoInfo, [frame]) is TimeSpan t)
            return t;
        return TimeSpan.FromSeconds(frame / (double)Math.Max(1, MixCache.Fps));
    }

    static byte[] FloatToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var s = Math.Clamp(samples[i], -1f, 1f);
            var v = (short)Math.Round(s * 32767);
            bytes[i * 2] = (byte)(v & 0xff);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return bytes;
    }
}

sealed class ScrubWaveProvider : IWaveProvider
{
    readonly object _gate = new();
    byte[] _clip = [];
    int _pos;

    public ScrubWaveProvider(WaveFormat format) => WaveFormat = format;

    public WaveFormat WaveFormat { get; }

    public void SetClip(byte[] pcm)
    {
        lock (_gate)
        {
            _clip = pcm;
            _pos = 0;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            var count = buffer.Length;
            var available = _clip.Length - _pos;
            if (available <= 0)
            {
                buffer.Clear();
                return count;
            }
            var n = Math.Min(count, available);
            _clip.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            if (n < count)
                buffer[n..].Clear();
            return count;
        }
    }
}
