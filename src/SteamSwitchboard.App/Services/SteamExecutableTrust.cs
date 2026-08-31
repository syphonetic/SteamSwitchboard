using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SteamSwitchboard.Services;

public static class SteamExecutableTrust
{
    private const uint ErrorSuccess = 0;
    private const uint WindowStateActionIgnore = 0;
    private const uint UserInterfaceNone = 2;
    private const uint RevocationChecksWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint RevocationCheckChainExcludeRoot = 0x00000080;

    private static readonly Guid GenericVerifyAction =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static readonly HashSet<string> ValvePublisherNames = new(
        ["Valve Corp.", "Valve Corporation"],
        StringComparer.Ordinal);

    public static bool IsTrustedValveExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return false;
        }

        if (!HasExpectedSteamMetadata(path)
            || !HasExpectedInstallationLayout(path))
        {
            return false;
        }

        try
        {
            if (!HasValidAuthenticodeSignature(path))
            {
                return false;
            }
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException
                or ExternalException)
        {
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057 // Required to read the signer embedded in an Authenticode file.
            using var signer = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(signer);
#pragma warning restore SYSLIB0057
            var publisher = certificate.GetNameInfo(
                X509NameType.SimpleName,
                forIssuer: false);
            return ValvePublisherNames.Contains(publisher);
        }
        catch (Exception exception) when (
            exception is CryptographicException
                or IOException
                or ArgumentException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool HasExpectedSteamMetadata(
        string? originalFilename,
        string? productName,
        string? companyName) =>
        string.Equals(
            originalFilename,
            "steam.exe",
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            productName,
            "Steam",
            StringComparison.Ordinal)
        && companyName is not null
        && ValvePublisherNames.Contains(companyName);

    internal static bool HasExpectedInstallationLayout(string path)
    {
        try
        {
            var root = Path.GetDirectoryName(path);
            if (!LocalPathPolicy.TryNormalizeLocalPath(root, out var normalizedRoot))
            {
                return false;
            }

            return LocalPathPolicy.TryNormalizeLocalPath(
                    Path.Combine(normalizedRoot, "config"),
                    out var config)
                && Directory.Exists(config)
                && LocalPathPolicy.IsStrictDescendant(config, normalizedRoot)
                && LocalPathPolicy.TryNormalizeLocalPath(
                    Path.Combine(normalizedRoot, "steamapps"),
                    out var steamApps)
                && Directory.Exists(steamApps)
                && LocalPathPolicy.IsStrictDescendant(steamApps, normalizedRoot);
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

    private static bool HasExpectedSteamMetadata(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return HasExpectedSteamMetadata(
                version.OriginalFilename,
                version.ProductName,
                version.CompanyName);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FileNotFoundException
                or IOException
                or System.Security.SecurityException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasValidAuthenticodeSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path
        };
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        var wasMarshaled = false;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            wasMarshaled = true;
            var trustData = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UserInterfaceChoice = UserInterfaceNone,
                RevocationChecks = RevocationChecksWholeChain,
                UnionChoice = ChoiceFile,
                FileInformation = fileInfoPointer,
                StateAction = WindowStateActionIgnore,
                ProviderFlags = RevocationCheckChainExcludeRoot
            };

            return WinVerifyTrust(
                IntPtr.Zero,
                GenericVerifyAction,
                ref trustData) == ErrorSuccess;
        }
        finally
        {
            if (wasMarshaled)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            }

            Marshal.FreeCoTaskMem(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UserInterfaceChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInformation;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UserInterfaceContext;
    }
}
