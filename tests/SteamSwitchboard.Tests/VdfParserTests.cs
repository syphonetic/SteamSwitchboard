using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class VdfParserTests
{
    [TestMethod]
    public void Parse_ReadsNestedObjectsCommentsAndEscapes()
    {
        const string source = """
            // Steam-style fixture
            "root"
            {
                "name" "A \"quoted\" game"
                "path" "D:\\SteamLibrary"
                "apps"
                {
                    "123" "1"
                }
            }
            """;

        var result = VdfParser.Parse(source);

        var root = result.Get("root");
        Assert.IsNotNull(root);
        Assert.AreEqual("A \"quoted\" game", root.GetValue("name"));
        Assert.AreEqual(@"D:\SteamLibrary", root.GetValue("path"));
        Assert.AreEqual("1", root.Get("apps")?.GetValue("123"));
    }

    [TestMethod]
    public void Parse_AcceptsBareTokens()
    {
        var result = VdfParser.Parse("root { key value }");

        Assert.AreEqual("value", result.Get("root")?.GetValue("key"));
    }

    [TestMethod]
    public void Parse_RejectsTruncatedObjects()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => VdfParser.Parse("\"root\" { \"key\" \"value\""));

        StringAssert.Contains(exception.Message, "closing brace");
    }

    [TestMethod]
    public void Parse_RejectsDuplicateKeys()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => VdfParser.Parse("root { key one KEY two }"));

        StringAssert.Contains(exception.Message, "duplicate key");
    }

    [TestMethod]
    public void Parse_RejectsExcessiveNesting()
    {
        var source = string.Concat(
            Enumerable.Repeat("key { ", VdfParser.MaximumDepth + 1))
            + string.Concat(Enumerable.Repeat(" }", VdfParser.MaximumDepth + 1));

        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => VdfParser.Parse(source));

        StringAssert.Contains(exception.Message, "nested too deeply");
    }

    [TestMethod]
    public void Parse_RejectsOversizedTokens()
    {
        var source = $"key \"{new string('a', VdfParser.MaximumTokenCharacters + 1)}\"";

        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => VdfParser.Parse(source));

        StringAssert.Contains(exception.Message, "token is too long");
    }

    [TestMethod]
    public void Parse_AcceptsTokenAtExactSafetyBoundary()
    {
        var token = new string('a', VdfParser.MaximumTokenCharacters);

        var result = VdfParser.Parse($"key \"{token}\"");

        Assert.AreEqual(token.Length, result.GetValue("key")?.Length);
    }

    [TestMethod]
    public void ParseFile_RejectsOversizedFilesBeforeParsing()
    {
        using var temporary = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporary.Path, "large.vdf");
        using (var stream = File.Create(path))
        {
            stream.SetLength(VdfParser.MaximumFileBytes + 1L);
        }

        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => VdfParser.ParseFile(path));

        StringAssert.Contains(exception.Message, "too large");
    }

    [TestMethod]
    public void Parse_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => VdfParser.Parse("root { key value }", cancellation.Token));
    }
}
