using Microsoft.Extensions.Logging.Abstractions;
using Sonari.Infrastructure.Soundboard;
using Sonari.Infrastructure.Storage;

namespace Sonari.UnitTests.Infrastructure;

public sealed class SoundboardStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sonari.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SoundboardStoreTests()
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

    private SoundboardStore CreateStore() =>
        new(_paths, new JsonFileStore(NullLogger<JsonFileStore>.Instance), NullLogger<SoundboardStore>.Instance);

    [Fact]
    public void WithoutAFile_StartsWithOneEmptyBank()
    {
        SoundboardLibrary library = CreateStore().Load();

        SoundBank bank = Assert.Single(library.Banks);
        Assert.Empty(bank.Sounds);
        Assert.NotEqual(Guid.Empty, bank.Id);
    }

    [Fact]
    public async Task Library_SurvivesARestartInOrder()
    {
        var sounds = new List<SoundEntry>
        {
            new(Guid.NewGuid(), "Aplausos", @"C:\Sonidos\aplausos.wav", "Público", -3f, false),
            new(Guid.NewGuid(), "Lluvia", @"C:\Sonidos\lluvia.mp3", null, -12f, true),
        };
        var library = new SoundboardLibrary { Banks = [new SoundBank(Guid.NewGuid(), "Directo", sounds)] };

        await CreateStore().SaveAsync(library, TestContext.Current.CancellationToken);
        SoundboardLibrary reloaded = CreateStore().Load();

        SoundBank bank = Assert.Single(reloaded.Banks);
        Assert.Equal("Directo", bank.Name);
        Assert.Equal(sounds, bank.Sounds);
    }

    [Fact]
    public void Normalize_FixesOutOfRangeAndEmptyValues()
    {
        var entry = new SoundEntry(Guid.Empty, "  ", @"C:\Sonidos\golpe.wav", "   ", 99f, false);

        SoundEntry normalized = entry.Normalize();

        Assert.NotEqual(Guid.Empty, normalized.Id);
        Assert.Equal("golpe", normalized.Name);
        Assert.Null(normalized.Category);
        Assert.Equal(SoundEntry.MaxGainDb, normalized.GainDb);
    }
}
