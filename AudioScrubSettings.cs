using System.Windows;
using YukkuriMovieMaker.Plugin;

namespace AudioScrub;

public sealed class AudioScrubSettings : SettingsBase<AudioScrubSettings>
{
    public override SettingsCategory Category => SettingsCategory.Other;
    public override string Name => "Audio Scrub";
    public override bool HasSettingView => true;
    public override FrameworkElement SettingView => new AudioScrubSettingsView { DataContext = this };

    bool _loading;
    bool _enabled = true;
    double _volume = 100;
    int _frameCount = 2;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            Set(ref _enabled, value);
            Persist();
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            Set(ref _volume, Math.Clamp(value, 0, 200));
            Persist();
        }
    }

    public int FrameCount
    {
        get => _frameCount;
        set
        {
            Set(ref _frameCount, Math.Clamp(value, 1, 6));
            Persist();
        }
    }

    public override void Initialize()
    {
        LoadFromJson();
        AudioScrubPlugin.EnsureInitialized();
        UpdateChecker.Start();
    }

    void LoadFromJson()
    {
        _loading = true;
        try
        {
            var dto = SettingsStore.Load();
            Enabled = dto.Enabled;
            Volume = dto.Volume;
            if (dto.FrameCount is int saved)
                FrameCount = saved;
        }
        finally
        {
            _loading = false;
        }
        Persist();
    }

    void Persist()
    {
        if (_loading)
            return;
        SettingsStore.Save(new SettingsDto
        {
            Enabled = _enabled,
            Volume = _volume,
            FrameCount = _frameCount
        });
    }
}
