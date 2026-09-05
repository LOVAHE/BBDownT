namespace BBDownT.Tests;

public class LegacyLocalFileMigrationTests
{
    [Theory]
    [InlineData("BBDown.config", "BBDownT.config")]
    [InlineData("BBDown.data", "BBDownT.data")]
    [InlineData("BBDownTV.data", "BBDownTTV.data")]
    [InlineData("BBDownApp.data", "BBDownTApp.data")]
    [InlineData("BBDown.archives", "BBDownT.archives")]
    public void ExplicitMigration_CopiesAndBacksUpEachSupportedFile(string oldName, string newName)
    {
        var directory = CreateDirectory();
        var old = Path.Combine(directory, oldName);
        var current = Path.Combine(directory, newName);
        var backup = old + ".migrated-fixture";
        try
        {
            File.WriteAllText(old, "synthetic legacy contents\n");

            Assert.Equal(0, LegacyLocalFileMigration.Run(directory, "fixture"));

            Assert.False(File.Exists(old));
            Assert.Equal("synthetic legacy contents\n", File.ReadAllText(current));
            Assert.Equal(File.ReadAllText(current), File.ReadAllText(backup));
            Assert.Equal(0, LegacyLocalFileMigration.Run(directory, "fixture"));
        }
        finally
        {
            File.Delete(old);
            File.Delete(current);
            File.Delete(backup);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void ExistingCurrentFile_IsNotOverwrittenAndLegacyFileIsKept()
    {
        var directory = CreateDirectory();
        var old = Path.Combine(directory, "BBDown.config");
        var current = Path.Combine(directory, "BBDownT.config");
        try
        {
            File.WriteAllText(old, "legacy");
            File.WriteAllText(current, "current");

            Assert.Equal(0, LegacyLocalFileMigration.Run(directory, "fixture"));

            Assert.Equal("legacy", File.ReadAllText(old));
            Assert.Equal("current", File.ReadAllText(current));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            File.Delete(old);
            File.Delete(current);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void ExistingBackup_IsPreservedWithANumberedBackup()
    {
        var directory = CreateDirectory();
        var old = Path.Combine(directory, "BBDown.data");
        var current = Path.Combine(directory, "BBDownT.data");
        var backup = old + ".migrated-fixture";
        var numbered = backup + "-1";
        try
        {
            File.WriteAllText(old, "legacy");
            File.WriteAllText(backup, "keep backup");

            Assert.Equal(0, LegacyLocalFileMigration.Run(directory, "fixture"));

            Assert.Equal("keep backup", File.ReadAllText(backup));
            Assert.Equal("legacy", File.ReadAllText(numbered));
        }
        finally
        {
            File.Delete(old);
            File.Delete(current);
            File.Delete(backup);
            File.Delete(numbered);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void FailedCopy_ReturnsFailureAndKeepsTheSource()
    {
        var directory = CreateDirectory();
        var old = Path.Combine(directory, "BBDown.config");
        var current = Path.Combine(directory, "BBDownT.config");
        try
        {
            File.WriteAllText(old, "legacy");
            Directory.CreateDirectory(current);

            Assert.Equal(1, LegacyLocalFileMigration.Run(directory, "fixture"));
            Assert.Equal("legacy", File.ReadAllText(old));
            Assert.True(Directory.Exists(current));
        }
        finally
        {
            File.Delete(old);
            Directory.Delete(current);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void FailedBackupMove_CleansTemporaryTargetAndKeepsSource()
    {
        var files = new FakeFileOperations { ["BBDown.config"] = "legacy" };
        files.FailMoveTo = "BBDown.config.migrated-fixture";

        Assert.Equal(1, LegacyLocalFileMigration.Run("", "fixture", files));
        Assert.Equal("legacy", files["BBDown.config"]);
        Assert.DoesNotContain(files.Paths, path => path.Contains(".migrating-", StringComparison.Ordinal));
        Assert.DoesNotContain(files.Paths, path => path == "BBDownT.config");
    }

    [Fact]
    public void FailedTargetCommit_RestoresSourceAndAllowsRetry()
    {
        var files = new FakeFileOperations { ["BBDown.config"] = "legacy" };
        files.FailMoveTo = "BBDownT.config";

        Assert.Equal(1, LegacyLocalFileMigration.Run("", "fixture", files));
        Assert.Equal("legacy", files["BBDown.config"]);
        Assert.DoesNotContain(files.Paths, path => path == "BBDownT.config");
        Assert.DoesNotContain(files.Paths, path => path.Contains(".migrating-", StringComparison.Ordinal));

        files.FailMoveTo = null;
        Assert.Equal(0, LegacyLocalFileMigration.Run("", "fixture", files));
        Assert.Equal("legacy", files["BBDownT.config"]);
        Assert.Equal("legacy", files["BBDown.config.migrated-fixture"]);
    }

    [Fact]
    public void ExternalTargetCreatedBeforeCommit_IsPreservedAndSourceIsRestored()
    {
        var files = new FakeFileOperations { ["BBDown.config"] = "legacy" };
        files.FailMoveTo = "BBDownT.config";
        files.CreateTargetBeforeFailure = true;

        Assert.Equal(1, LegacyLocalFileMigration.Run("", "fixture", files));
        Assert.Equal("legacy", files["BBDown.config"]);
        Assert.Equal("external", files["BBDownT.config"]);
        Assert.DoesNotContain(files.Paths, path => path.Contains(".migrating-", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidDirectory_ReturnsFailureInsteadOfThrowing()
    {
        Assert.Equal(1, LegacyLocalFileMigration.Run(null!));
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bbd-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeFileOperations : LegacyLocalFileMigration.IFileOperations
    {
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
        public string? FailMoveTo { get; set; }
        public bool CreateTargetBeforeFailure { get; set; }
        public IEnumerable<string> Paths => files.Keys;
        public string this[string path]
        {
            get => files[path];
            set => files[path] = value;
        }

        public bool FileExists(string path) => files.ContainsKey(path);
        public bool DirectoryExists(string path) => false;

        public void Copy(string sourceFileName, string destFileName)
        {
            files.Add(destFileName, files[sourceFileName]);
        }

        public void Move(string sourceFileName, string destFileName)
        {
            if (destFileName == FailMoveTo)
            {
                if (CreateTargetBeforeFailure) files[destFileName] = "external";
                throw new IOException("synthetic move failure");
            }
            var contents = files[sourceFileName];
            files.Remove(sourceFileName);
            files.Add(destFileName, contents);
        }

        public void Delete(string path) => files.Remove(path);

        public void Add(string path, string contents) => files.Add(path, contents);
    }
}
