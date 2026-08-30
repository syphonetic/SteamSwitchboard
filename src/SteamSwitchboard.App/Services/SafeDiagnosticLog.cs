using System.Text;

namespace SteamSwitchboard.Services;

public static class SafeDiagnosticLog
{
    public const long MaximumLogBytes = 256 * 1024;

    public static string CreateRecord(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"{DateTimeOffset.UtcNow:O}\t{exception.GetType().FullName}\t{exception.HResult}{Environment.NewLine}";
    }

    public static void WriteSingleRecord(string path, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var safePath = PrepareSafeLogPath(path);
        File.WriteAllText(
            safePath,
            CreateRecord(exception),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static void AppendBoundedRecord(string path, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var safePath = PrepareSafeLogPath(path);

        if (File.Exists(safePath)
            && new FileInfo(safePath).Length >= MaximumLogBytes)
        {
            var previousPath = PrepareSafeLogPath($"{safePath}.previous");
            File.Move(safePath, previousPath, overwrite: true);
        }

        File.AppendAllText(
            safePath,
            CreateRecord(exception),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string PrepareSafeLogPath(string path)
    {
        if (!LocalPathPolicy.TryNormalizeLocalPath(
                path,
                out var safePath,
                requireExisting: false))
        {
            throw new InvalidOperationException(
                "The diagnostic file must use a safe local path.");
        }

        var directory = Path.GetDirectoryName(safePath)
            ?? throw new InvalidOperationException("The diagnostic path has no parent folder.");
        Directory.CreateDirectory(directory);
        if (!LocalPathPolicy.TryNormalizeLocalPath(directory, out _)
            || (File.Exists(safePath)
                && !LocalPathPolicy.TryNormalizeLocalPath(safePath, out _)))
        {
            throw new InvalidOperationException(
                "The diagnostic file cannot use links or remote storage.");
        }

        return safePath;
    }
}
