using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Plugin;

namespace AudioScrub;

[PluginDetails(AuthorName = "AudioScrub")]
public sealed class AudioScrubPlugin : IPlugin
{
    public string Name => "Audio Scrub";

    public AudioScrubPlugin()
    {
        EnsureInitialized();
        UpdateChecker.Start();
    }

    [ModuleInitializer]
    internal static void ModuleInit() => EnsureInitialized();

    internal static void EnsureInitialized()
    {
        HarmonyBootstrap.Apply();
        AudioScrubLog.Write("plugin initialized");
    }
}
