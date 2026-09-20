using System.Windows;
using YukkuriMovieMaker.Plugin;

namespace AudioScrub;

public sealed class AudioScrubSettings : SettingsBase<AudioScrubSettings>
{
    public override SettingsCategory Category => SettingsCategory.Other;
    public override string Name => "Audio Scrub";
    public override bool HasSettingView => true;
    public override FrameworkElement SettingView => new AudioScrubSettingsView { DataContext = this };

    bool _enabled = true;
    double _volume = 100;
    int _frameCount = 1;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public double Volume
    {
        get => _volume;
        set => Set(ref _volume, Math.Clamp(value, 0, 200));
    }

    /// <summary>スクラブ時に再生するフレーム数（Premiere の 1f 相当は 1）。</summary>
    public int FrameCount
    {
        get => _frameCount;
        set => Set(ref _frameCount, Math.Clamp(value, 1, 6));
    }

    public override void Initialize()
    {
        AudioScrubPlugin.EnsureInitialized();
        UpdateChecker.Start();
    }
}
