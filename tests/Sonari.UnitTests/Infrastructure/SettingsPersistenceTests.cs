using Microsoft.Extensions.Logging.Abstractions;
using Sonari.Infrastructure.Settings;
using Sonari.Infrastructure.Storage;

namespace Sonari.UnitTests.Infrastructure;

public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sonari.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SettingsPersistenceTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "logs"));
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SettingsService CreateService() =>
        new(_paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<SettingsService>.Instance);

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        AppSettings settings = CreateService().Current;

        Assert.Equal(AudioSettings.DefaultSampleRate, settings.Audio.SampleRate);
        Assert.Equal(AudioSettings.DefaultBufferMilliseconds, settings.Audio.BufferMilliseconds);
        Assert.Null(settings.Ui.LastPage);
    }

    [Fact]
    public async Task SavedSettings_SurviveARestart()
    {
        SettingsService first = CreateService();
        AppSettings changed = first.Current with
        {
            Audio = first.Current.Audio with { SampleRate = 44_100, BufferMilliseconds = 20, MonitorDeviceId = "{0.0.0.00000000}.{abc}" },
            Ui = new UiSettings { LastPage = "devices" },
        };

        await first.SaveAsync(changed, TestContext.Current.CancellationToken);
        AppSettings reloaded = CreateService().Current;

        Assert.Equal(44_100, reloaded.Audio.SampleRate);
        Assert.Equal(20, reloaded.Audio.BufferMilliseconds);
        Assert.Equal("{0.0.0.00000000}.{abc}", reloaded.Audio.MonitorDeviceId);
        Assert.Equal("devices", reloaded.Ui.LastPage);
        Assert.False(File.Exists(_paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public async Task Save_RaisesChangedWithNormalizedValues()
    {
        SettingsService service = CreateService();
        AppSettings? received = null;
        service.Changed += (_, s) => received = s;

        await service.SaveAsync(service.Current with { Audio = new AudioSettings { SampleRate = 12_345 } },
            TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Equal(AudioSettings.DefaultSampleRate, received.Audio.SampleRate);
        Assert.Same(received, service.Current);
    }

    [Fact]
    public void CorruptFile_IsQuarantinedAndDefaultsAreUsed()
    {
        File.WriteAllText(_paths.SettingsFile, "{ esto no es json");

        AppSettings settings = CreateService().Current;

        Assert.Equal(AudioSettings.DefaultSampleRate, settings.Audio.SampleRate);
        Assert.False(File.Exists(_paths.SettingsFile));
        Assert.Single(Directory.GetFiles(_paths.ConfigDirectory, "settings.corrupt-*.json"));
    }

    [Fact]
    public void OutOfRangeValues_AreNormalizedOnLoad()
    {
        File.WriteAllText(_paths.SettingsFile, """
            {
              "schemaVersion": 1,
              "audio": { "sampleRate": 11025, "bufferMilliseconds": 3 },
              "propiedadDeUnaVersionFutura": true
            }
            """);

        AppSettings settings = CreateService().Current;

        Assert.Equal(AudioSettings.DefaultSampleRate, settings.Audio.SampleRate);
        Assert.Equal(AudioSettings.DefaultBufferMilliseconds, settings.Audio.BufferMilliseconds);
    }

    [Fact]
    public void NullSections_InFile_FallBackToDefaults()
    {
        File.WriteAllText(_paths.SettingsFile, """{ "audio": null, "ui": null }""");

        AppSettings settings = CreateService().Current;

        Assert.NotNull(settings.Audio);
        Assert.NotNull(settings.Ui);
    }
}
