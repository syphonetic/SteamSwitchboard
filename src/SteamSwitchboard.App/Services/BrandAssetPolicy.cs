using System.Security.Cryptography;

namespace SteamSwitchboard.Services;

internal static class BrandAssetPolicy
{
    private const int MaximumAppLogoBytes = 1024 * 1024;
    private static readonly byte[] ExpectedAppLogoSha256 = Convert.FromHexString(
        "B684FFBB817F43B3992B44D06EAA04DBFCADFA4CBDD1F2A86572317F4FB59993");
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    internal static bool TryOpenAppLogoForRendering(
        string applicationDirectory,
        out FileStream? readLease)
    {
        readLease = null;
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            return false;
        }

        FileStream? stream = null;
        try
        {
            var root = Path.GetFullPath(applicationDirectory);
            var path = Path.Combine(
                root,
                "Assets",
                "Branding",
                "SteamSwitchboard-app-logo.png");
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (!TryValidateAppLogo(stream))
            {
                return false;
            }

            readLease = stream;
            stream = null;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    internal static bool TryValidateAppLogo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            if (!stream.CanRead
                || !stream.CanSeek
                || stream.Length is < 8 or > MaximumAppLogoBytes)
            {
                return false;
            }

            stream.Position = 0;
            Span<byte> signature = stackalloc byte[PngSignature.Length];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(PngSignature))
            {
                return false;
            }

            stream.Position = 0;
            var actualHash = SHA256.HashData(stream);
            var isExpected = CryptographicOperations.FixedTimeEquals(
                actualHash,
                ExpectedAppLogoSha256);
            stream.Position = 0;
            return isExpected;
        }
        catch (Exception exception) when (
            exception is IOException
                or NotSupportedException
                or ObjectDisposedException)
        {
            return false;
        }
    }
}
