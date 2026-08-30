namespace SteamSwitchboard.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string _safeRoot;

    public TemporaryDirectory()
    {
        _safeRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
            .TrimEnd(System.IO.Path.DirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        Path = System.IO.Path.Combine(
            _safeRoot,
            "SteamSwitchboard.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(params string[] segments)
    {
        var path = segments.Aggregate(Path, System.IO.Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, string contents = "")
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        var target = System.IO.Path.GetFullPath(Path);
        if (!target.StartsWith(_safeRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                target.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                _safeRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove an unsafe test directory.");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
