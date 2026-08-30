using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SteamSwitchboard.Services;

public static class SteamExecutableTrust
{
    private const uint ErrorSuccess = 0;
    private const uint WindowStateActionIgnore = 0;
    private const uint UserInterfaceNone = 2;
    private const uint RevocationChecksNone = 0;
    private const uint ChoiceFile = 1;
    private const uint CacheOnlyUrlRetrieval = 0x00001000;

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
                RevocationChecks = RevocationChecksNone,
                UnionChoice = ChoiceFile,
                FileInformation = fileInfoPointer,
                StateAction = WindowStateActionIgnore,
                ProviderFlags = CacheOnlyUrlRetrieval
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
