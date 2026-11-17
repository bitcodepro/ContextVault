using KnowledgeMemory.Core;

namespace KnowledgeMemory.Tests;

[CollectionDefinition("LegacyDataDirectory", DisableParallelization = true)]
public sealed class LegacyDataDirectoryCollection;

/// <summary>Updating from 1.2.0 (data in %LOCALAPPDATA%) must keep the existing memory.</summary>
[Collection("LegacyDataDirectory")]
public sealed class UpgradeDataLocationTests : IDisposable
{
    private readonly string _dir = TestPaths.NewDir();
    private readonly string _savedLegacy = ConfigLoader.LegacyDataDirectory;
    private string Legacy => Path.Combine(_dir, "localappdata", "KnowledgeMemory");
    private string Install => Path.Combine(_dir, "install");
    private string Config => Path.Combine(Install, "appsettings.json");

    public UpgradeDataLocationTests()
    {
        ConfigLoader.LegacyDataDirectory = Legacy;
        Directory.CreateDirectory(Install);
        File.WriteAllText(Config, """{ "Server": { "Port": 47999 } }"""); // old config: no DataDirectory
    }

    public void Dispose()
    {
        ConfigLoader.LegacyDataDirectory = _savedLegacy;
        TestPaths.TryDelete(_dir);
    }

    [Fact]
    public void Old_config_without_a_data_folder_keeps_the_existing_legacy_memory()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "knowledge.db"), "");
        Assert.Equal(Path.GetFullPath(Legacy), ConfigLoader.Load(Config).ResolvedDataDirectory);
    }

    [Fact]
    public void New_install_without_legacy_memory_uses_data_next_to_the_program()
    {
        Assert.Equal(Path.Combine(Install, "data"), ConfigLoader.Load(Config).ResolvedDataDirectory);
    }

    [Fact]
    public void Data_next_to_the_program_wins_once_it_has_a_database()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "knowledge.db"), "");
        Directory.CreateDirectory(Path.Combine(Install, "data"));
        File.WriteAllText(Path.Combine(Install, "data", "knowledge.db"), "");
        Assert.Equal(Path.Combine(Install, "data"), ConfigLoader.Load(Config).ResolvedDataDirectory);
    }

    [Fact]
    public void An_explicit_data_folder_is_never_redirected()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "knowledge.db"), "");
        File.WriteAllText(Config, """{ "Storage": { "DataDirectory": "data" } }""");
        Assert.Equal(Path.Combine(Install, "data"), ConfigLoader.Load(Config).ResolvedDataDirectory);
    }
}
