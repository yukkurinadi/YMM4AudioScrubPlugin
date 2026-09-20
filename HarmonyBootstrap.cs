using System.Reflection;
using HarmonyLib;

namespace AudioScrub;

static class HarmonyBootstrap
{
    static readonly object Gate = new();
    static bool _done;

    public static void Apply()
    {
        if (_done)
            return;
        lock (Gate)
        {
            if (_done)
                return;
            try
            {
                var harmony = new Harmony("com.audioscrub.ymm4");
                // 手シーク / 矢印キーのみ。CurrentFrame や Player.Seek はプレビュー再生でも呼ばれる
                Patch(harmony, "YukkuriMovieMaker.ViewModels.PreviewViewModel", "SeekAsync", [typeof(int)]);
                PatchAudioPlayerCtor(harmony);
                PatchItemsSetter(harmony);
                _done = true;
                AudioScrubLog.Write("Harmony patches applied");
            }
            catch (Exception ex)
            {
                AudioScrubLog.Write("Harmony apply failed: " + ex);
            }
        }
    }

    static void Patch(Harmony harmony, string typeName, string method, Type[] args)
    {
        var type = AccessTools.TypeByName(typeName);
        var mi = type is null ? null : AccessTools.Method(type, method, args);
        if (mi is null)
        {
            AudioScrubLog.Write("method missing: " + typeName + "." + method);
            return;
        }
        var postfix = AccessTools.Method(typeof(SeekHooks), nameof(SeekHooks.AfterUserSeek));
        harmony.Patch(mi, postfix: new HarmonyMethod(postfix));
        AudioScrubLog.Write("patched " + mi.DeclaringType?.FullName + "." + mi.Name);
    }

    static void PatchAudioPlayerCtor(Harmony harmony)
    {
        var type = AccessTools.TypeByName("YukkuriMovieMaker.Player.TimelineAudioPlayer");
        var ctor = type?.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(c => c.GetParameters().Length == 2);
        if (ctor is null)
        {
            AudioScrubLog.Write("TimelineAudioPlayer ctor missing");
            return;
        }
        var postfix = AccessTools.Method(typeof(SeekHooks), nameof(SeekHooks.AfterPlayerCtor));
        harmony.Patch(ctor, postfix: new HarmonyMethod(postfix));
        AudioScrubLog.Write("patched TimelineAudioPlayer.ctor");
    }

    static void PatchItemsSetter(Harmony harmony)
    {
        var type = AccessTools.TypeByName("YukkuriMovieMaker.Project.Timeline");
        var setter = type is null ? null : AccessTools.Property(type, "Items")?.GetSetMethod(true);
        if (setter is null)
        {
            AudioScrubLog.Write("Timeline.Items setter missing");
            return;
        }
        var postfix = AccessTools.Method(typeof(SeekHooks), nameof(SeekHooks.AfterItemsChanged));
        harmony.Patch(setter, postfix: new HarmonyMethod(postfix));
        AudioScrubLog.Write("patched Timeline.Items");
    }
}

static class SeekHooks
{
    public static void AfterUserSeek(object __instance, int frame)
    {
        try
        {
            AudioScrubEngine.OnUserSeek(__instance, frame);
        }
        catch (Exception ex)
        {
            AudioScrubLog.Write("AfterUserSeek: " + ex);
        }
    }

    public static void AfterPlayerCtor(object __instance)
    {
        try
        {
            AudioScrubEngine.OnPlayerCreated(__instance);
        }
        catch (Exception ex)
        {
            AudioScrubLog.Write("AfterPlayerCtor: " + ex);
        }
    }

    public static void AfterItemsChanged()
    {
        try
        {
            MixCache.Invalidate();
        }
        catch (Exception ex)
        {
            AudioScrubLog.Write("AfterItemsChanged: " + ex);
        }
    }
}
