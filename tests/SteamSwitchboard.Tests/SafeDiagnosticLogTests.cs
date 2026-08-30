using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class SafeDiagnosticLogTests
{
    [TestMethod]
    public void CreateRecord_DoesNotIncludeMessagesPathsOrTokens()
    {
        const string secret = "token=super-secret&password=hunter2";
        var exception = new InvalidOperationException(
            $"https://example.com/?{secret} C:\\private\\account");

        var record = SafeDiagnosticLog.CreateRecord(exception);

        Assert.IsFalse(record.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(record.Contains("C:\\private", StringComparison.Ordinal));
        Assert.IsFalse(record.Contains("example.com", StringComparison.Ordinal));
        StringAssert.Contains(record, typeof(InvalidOperationException).FullName!);
    }

    [TestMethod]
    public void AppendBoundedRecord_RotatesOversizedLog()
    {
        using var temporary = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporary.Path, "crashes.log");
        using (var stream = File.Create(path))
        {
            stream.SetLength(SafeDiagnosticLog.MaximumLogBytes);
        }

        SafeDiagnosticLog.AppendBoundedRecord(
            path,
            new InvalidOperationException("private detail"));

        Assert.IsTrue(File.Exists($"{path}.previous"));
        Assert.IsLessThan(SafeDiagnosticLog.MaximumLogBytes, new FileInfo(path).Length);
    }

    [TestMethod]
    public void WriteSingleRecord_RejectsRemoteDestinations()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeDiagnosticLog.WriteSingleRecord(
                @"\\server\share\startup.log",
                new InvalidOperationException("private detail")));
    }
}
