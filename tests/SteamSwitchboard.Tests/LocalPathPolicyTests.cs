using System.Diagnostics;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class LocalPathPolicyTests
{
    [TestMethod]
    public void TryNormalizeLocalPath_AcceptsExistingLocalPaths()
    {
        using var temporary = new TemporaryDirectory();

        var accepted = LocalPathPolicy.TryNormalizeLocalPath(
            temporary.Path,
            out var normalized);

        Assert.IsTrue(accepted);
        Assert.AreEqual(System.IO.Path.GetFullPath(temporary.Path), normalized);
    }

    [TestMethod]
    [DataRow(@"\\attacker.invalid\share\file")]
    [DataRow(@"\\?\UNC\attacker.invalid\share\file")]
    [DataRow(@"\\.\GLOBALROOT\Device\HarddiskVolumeShadowCopy1")]
    [DataRow(@"relative\file")]
    public void TryNormalizeLocalPath_RejectsRemoteDeviceAndRelativePaths(string path)
    {
        Assert.IsFalse(LocalPathPolicy.TryNormalizeLocalPath(
            path,
            out _,
            requireExisting: false));
    }

    [TestMethod]
    public void TryNormalizeLocalPath_DistinguishesSafeFuturePathFromMissingRequiredPath()
    {
        using var temporary = new TemporaryDirectory();
        var futurePath = System.IO.Path.Combine(temporary.Path, "future", "file.txt");

        Assert.IsTrue(LocalPathPolicy.TryNormalizeLocalPath(
            futurePath,
            out _,
            requireExisting: false));
        Assert.IsFalse(LocalPathPolicy.TryNormalizeLocalPath(
            futurePath,
            out _,
            requireExisting: true));
    }

    [TestMethod]
    public void IsStrictDescendant_UsesAPathBoundary()
    {
        using var temporary = new TemporaryDirectory();
        var parent = System.IO.Path.Combine(temporary.Path, "root");
        var child = System.IO.Path.Combine(parent, "child");
        var siblingPrefix = $"{parent}-evil";

        Assert.IsTrue(LocalPathPolicy.IsStrictDescendant(child, parent));
        Assert.IsFalse(LocalPathPolicy.IsStrictDescendant(siblingPrefix, parent));
    }

    [TestMethod]
    public void TryNormalizeLocalPath_RejectsDanglingDirectoryLinksForFuturePaths()
    {
        using var temporary = new TemporaryDirectory();
        var missingTarget = System.IO.Path.Combine(temporary.Path, "missing-target");
        var link = System.IO.Path.Combine(temporary.Path, "linked-folder");
        Directory.CreateDirectory(missingTarget);
        var startInfo = new ProcessStartInfo(
            System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(missingTarget);
        using (var process = Process.Start(startInfo))
        {
            Assert.IsNotNull(process);
            Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, process.ExitCode);
        }

        Directory.Delete(missingTarget);
        try
        {
            Assert.IsFalse(LocalPathPolicy.TryNormalizeLocalPath(
                System.IO.Path.Combine(link, "future.log"),
                out _,
                requireExisting: false));
        }
        finally
        {
            Directory.CreateDirectory(missingTarget);
            Directory.Delete(link);
            Directory.Delete(missingTarget);
        }
    }
}
