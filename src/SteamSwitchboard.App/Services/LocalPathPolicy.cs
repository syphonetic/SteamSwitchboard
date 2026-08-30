namespace SteamSwitchboard.Services;

public static class LocalPathPolicy
{
    private const int MaximumPathCharacters = 32_767;

    public static bool TryNormalizeLocalPath(
        string? candidate,
        out string normalized,
        bool requireExisting = true)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > MaximumPathCharacters
            || SafeText.ContainsUnsafeIdentityCharacters(candidate))
        {
            return false;
        }

        try
        {
            var replaced = candidate.Replace('/', Path.DirectorySeparatorChar).Trim();
            if (!Path.IsPathFullyQualified(replaced)
                || IsNetworkOrDevicePath(replaced))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(replaced);
            if (IsNetworkOrDevicePath(fullPath)
                || !IsAllowedLocalDrive(fullPath)
                || ContainsReparsePoint(fullPath, requireExisting))
            {
                return false;
            }

            if (requireExisting && !Path.Exists(fullPath))
            {
                return false;
            }

            normalized = fullPath;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsStrictDescendant(string candidate, string parent)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(parent);
        var parentPrefix = parent.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static bool IsNetworkOrDevicePath(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal)
        || path.StartsWith("\\?\\", StringComparison.Ordinal)
        || path.StartsWith("\\.\\", StringComparison.Ordinal);

    private static bool IsAllowedLocalDrive(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var driveType = new DriveInfo(root).DriveType;
        return driveType is DriveType.Fixed or DriveType.Removable;
    }

    private static bool ContainsReparsePoint(string fullPath, bool requireExisting)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return true;
        }

        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return requireExisting;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
