using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace AudioScrub;

/// <summary>
/// タイムラインのミックス PCM を 1 秒チャンクでメモリキャッシュする。
/// シークのたびに TimelineSource を組まない。
/// </summary>
static class MixCache
{
    const int ChunkSeconds = 1;
    const int MaxChunks = 48;

    static readonly object BakeLock = new();
    static readonly ConcurrentDictionary<int, float[]> Chunks = new();
    static readonly ConcurrentQueue<int> Lru = new();

    static object? _timeline;
    static object? _scenes;
    static int _hz = 48000;
    static int _fps = 30;
    static int _epoch;
    static long _stamp;

    public static int Hz => _hz;
    public static int Fps => _fps;

    public static void Attach(object timeline, object? scenes)
    {
        lock (BakeLock)
        {
            var sameTimeline = ReferenceEquals(_timeline, timeline);
            if (sameTimeline && (scenes is null || ReferenceEquals(_scenes, scenes)))
                return;
            if (scenes is null)
                return;

            _timeline = timeline;
            _scenes = scenes;
            Chunks.Clear();
            Interlocked.Increment(ref _epoch);
            _stamp = ComputeStamp(timeline);
            _fps = Reflect.GetFps(timeline);
            _hz = Reflect.GetHz(timeline);
            AudioScrubLog.Write($"cache attach hz={_hz} fps={_fps} stamp={_stamp}");
        }
    }

    public static void Invalidate()
    {
        lock (BakeLock)
        {
            Chunks.Clear();
            Interlocked.Increment(ref _epoch);
        }
    }

    public static float[]? GetFrame(TimeSpan time, int frameCount)
        => GetSamples(time, FrameSampleCount(frameCount));

    public static float[]? GetSamples(TimeSpan time, int stereoCount)
    {
        if (_timeline is null || _scenes is null)
            return null;
        if (stereoCount < 2)
            stereoCount = 2;
        if (stereoCount % 2 != 0)
            stereoCount++;

        InvalidateIfStale();
        EnsureChunk(SecondIndex(time));
        PrefetchNeighbors(time);
        var end = time + TimeSpan.FromSeconds(stereoCount / 2.0 / Math.Max(1, _hz));
        EnsureChunk(SecondIndex(end));

        var startSample = (long)(time.TotalSeconds * _hz) * 2;
        var dest = new float[stereoCount];
        var written = CopyFromCache(startSample, dest);
        if (written < 2)
            return null;
        if (written != dest.Length)
            Array.Resize(ref dest, written);
        return dest;
    }

    static int FrameSampleCount(int frameCount)
    {
        var fps = Math.Max(1, _fps);
        return Math.Max(2, _hz * 2 / fps) * Math.Max(1, frameCount);
    }

    static void PrefetchNeighbors(TimeSpan time)
    {
        var i = SecondIndex(time);
        _ = Task.Run(() =>
        {
            try
            {
                EnsureChunk(i - 1);
                EnsureChunk(i + 1);
                EnsureChunk(i + 2);
            }
            catch (Exception ex)
            {
                AudioScrubLog.Write("prefetch: " + ex.Message);
            }
        });
    }

    static int SecondIndex(TimeSpan time) => Math.Max(0, (int)time.TotalSeconds);

    static void InvalidateIfStale()
    {
        var timeline = _timeline;
        if (timeline is null)
            return;
        var stamp = ComputeStamp(timeline);
        if (stamp == _stamp)
            return;
        lock (BakeLock)
        {
            if (stamp == _stamp)
                return;
            Chunks.Clear();
            Interlocked.Increment(ref _epoch);
            _stamp = stamp;
            AudioScrubLog.Write($"cache invalidated stamp={stamp}");
        }
    }

    static long ComputeStamp(object timeline)
    {
        var itemsObj = AccessTools.Property(timeline.GetType(), "Items")?.GetValue(timeline);
        if (itemsObj is not System.Collections.IEnumerable items)
            return 0;
        long h = 17;
        var n = 0;
        foreach (var item in items)
        {
            if (item is null)
                continue;
            n++;
            var t = item.GetType();
            h = h * 31 + RuntimeHelpers.GetHashCode(item);
            h = h * 31 + Convert.ToInt32(AccessTools.Property(t, "Frame")?.GetValue(item) ?? 0);
            h = h * 31 + Convert.ToInt32(AccessTools.Property(t, "Length")?.GetValue(item) ?? 0);
            h = h * 31 + Convert.ToInt32(AccessTools.Property(t, "Layer")?.GetValue(item) ?? 0);
            h = h * 31 + (AccessTools.Property(t, "IsHidden")?.GetValue(item) is true ? 1 : 0);
        }
        h = h * 31 + n;
        h = h * 31 + LayerStamp(timeline);
        return h;
    }

    static long LayerStamp(object timeline)
    {
        var settings = AccessTools.Property(timeline.GetType(), "LayerSettings")?.GetValue(timeline);
        var itemsObj = settings is null ? null : AccessTools.Property(settings.GetType(), "Items")?.GetValue(settings);
        if (itemsObj is not System.Collections.IEnumerable layers)
            return 0;
        long h = 13;
        foreach (var layer in layers)
        {
            if (layer is null)
                continue;
            var t = layer.GetType();
            h = h * 31 + Convert.ToInt32(AccessTools.Property(t, "Layer")?.GetValue(layer) ?? 0);
            h = h * 31 + (AccessTools.Property(t, "IsHidden")?.GetValue(layer) is true ? 1 : 0);
            var vol = AccessTools.Property(t, "Volume")?.GetValue(layer);
            if (vol is not null)
                h = h * 31 + Convert.ToInt32(Convert.ToDouble(vol) * 10);
        }
        return h;
    }

    static void EnsureChunk(int index)
    {
        if (index < 0)
            return;
        if (Chunks.ContainsKey(index))
            return;

        lock (BakeLock)
        {
            if (Chunks.ContainsKey(index))
                return;
            var timeline = _timeline;
            var scenes = _scenes;
            if (timeline is null || scenes is null)
                return;

            var epoch = Volatile.Read(ref _epoch);
            var baked = Reflect.BakeSecond(timeline, scenes, index, out var hz, out var fps);
            if (Volatile.Read(ref _epoch) != epoch)
                return;
            if (hz > 0)
                _hz = hz;
            if (fps > 0)
                _fps = fps;
            if (baked is null || baked.Length < 2)
                return;

            Chunks[index] = baked;
            Lru.Enqueue(index);
            Trim();
            AudioScrubLog.Write($"cached sec={index} samples={baked.Length}");
        }
    }

    static void Trim()
    {
        while (Chunks.Count > MaxChunks && Lru.TryDequeue(out var old))
            Chunks.TryRemove(old, out _);
    }

    static int CopyFromCache(long startSample, float[] dest)
    {
        var hz = Math.Max(1, _hz);
        var samplesPerSec = hz * 2;
        var written = 0;
        while (written < dest.Length)
        {
            var abs = startSample + written;
            if (abs < 0)
            {
                written++;
                continue;
            }
            var sec = (int)(abs / samplesPerSec);
            var offset = (int)(abs % samplesPerSec);
            if (!Chunks.TryGetValue(sec, out var chunk) || chunk.Length == 0)
                break;
            if (offset >= chunk.Length)
                break;
            var n = Math.Min(dest.Length - written, chunk.Length - offset);
            Array.Copy(chunk, offset, dest, written, n);
            written += n;
        }
        return written;
    }
}

static class Reflect
{
    public static int GetFps(object timeline)
    {
        var videoInfo = AccessTools.Property(timeline.GetType(), "VideoInfo")?.GetValue(timeline);
        return Convert.ToInt32(AccessTools.Property(videoInfo?.GetType(), "FPS")?.GetValue(videoInfo) ?? 30);
    }

    public static int GetHz(object timeline)
    {
        var videoInfo = AccessTools.Property(timeline.GetType(), "VideoInfo")?.GetValue(timeline);
        return Convert.ToInt32(AccessTools.Property(videoInfo?.GetType(), "Hz")?.GetValue(videoInfo) ?? 48000);
    }

    public static float[]? BakeSecond(object timeline, object scenes, int second, out int hz, out int fps)
    {
        hz = GetHz(timeline);
        fps = GetFps(timeline);
        var sceneType = AccessTools.TypeByName("YukkuriMovieMaker.Project.Scene");
        var sourceType = AccessTools.TypeByName("YukkuriMovieMaker.Player.Audio.TimelineSource");
        if (sceneType is null || sourceType is null)
        {
            AudioScrubLog.Write($"types scene={sceneType} source={sourceType}");
            return null;
        }

        var scene = Activator.CreateInstance(sceneType, timeline, scenes, Array.Empty<Guid>());
        if (scene is null)
            return null;
        var source = Activator.CreateInstance(sourceType, scene);
        if (source is not IDisposable disposable)
            return null;

        using (disposable)
        {
            hz = Convert.ToInt32(GetProp(source, "Hz") ?? hz);
            Seek(source, TimeSpan.FromSeconds(second), hz);
            var count = hz * 2;
            var buffer = new float[count];
            var read = FindRead(source);
            if (read is null)
                return null;
            var got = Convert.ToInt32(read.Invoke(source, [buffer, 0, buffer.Length]) ?? 0);
            if (got <= 0)
                return Array.Empty<float>();
            if (got != buffer.Length)
                Array.Resize(ref buffer, got);
            return buffer;
        }
    }

    static object? GetProp(object obj, string name)
    {
        for (var t = obj.GetType(); t is not null; t = t.BaseType)
        {
            var p = AccessTools.Property(t, name);
            if (p is not null)
                return p.GetValue(obj);
        }
        return null;
    }

    static void Seek(object source, TimeSpan time, int hz)
    {
        for (var t = source.GetType(); t is not null; t = t.BaseType)
        {
            var m = AccessTools.Method(t, "Seek", [typeof(TimeSpan)]);
            if (m is not null)
            {
                m.Invoke(source, [time]);
                return;
            }
            m = AccessTools.Method(t, "Seek", [typeof(long)]);
            if (m is not null)
            {
                m.Invoke(source, [(long)(time.TotalSeconds * hz * 2)]);
                return;
            }
        }
    }

    static System.Reflection.MethodInfo? FindRead(object source)
    {
        for (var t = source.GetType(); t is not null; t = t.BaseType)
        {
            var m = AccessTools.Method(t, "Read", [typeof(float[]), typeof(int), typeof(int)]);
            if (m is not null)
                return m;
        }
        return null;
    }
}
