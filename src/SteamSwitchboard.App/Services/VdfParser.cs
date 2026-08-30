using System.IO;
using System.Text;

namespace SteamSwitchboard.Services;

public static class VdfParser
{
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    public const int MaximumSourceCharacters = 8 * 1024 * 1024;
    public const int MaximumDepth = 64;
    public const int MaximumTokenCharacters = 1024 * 1024;
    public const int MaximumNodes = 100_000;

    public static VdfNode Parse(
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumSourceCharacters)
        {
            throw new InvalidDataException("The VDF document is too large.");
        }

        var tokenizer = new Tokenizer(source, cancellationToken);
        var budget = new ParseBudget();
        var children = ParseObject(
            tokenizer,
            budget,
            depth: 0,
            expectClosingBrace: false);
        return VdfNode.Object(children);
    }

    public static VdfNode ParseFile(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The VDF file is too large.");
        }

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        var source = new StringBuilder((int)Math.Min(stream.Length, MaximumSourceCharacters));
        var buffer = new char[16 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (source.Length + read > MaximumSourceCharacters)
            {
                throw new InvalidDataException("The VDF file is too large.");
            }

            source.Append(buffer, 0, read);
        }

        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The VDF file changed while it was being read.");
        }

        return Parse(source.ToString(), cancellationToken);
    }

    private static Dictionary<string, VdfNode> ParseObject(
        Tokenizer tokenizer,
        ParseBudget budget,
        int depth,
        bool expectClosingBrace)
    {
        if (depth > MaximumDepth)
        {
            throw tokenizer.Error("The VDF document is nested too deeply.");
        }

        var values = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            tokenizer.ThrowIfCancellationRequested();
            var keyToken = tokenizer.Next();
            if (keyToken.Kind == TokenKind.End)
            {
                if (expectClosingBrace)
                {
                    throw tokenizer.Error("The VDF object is missing a closing brace.");
                }

                return values;
            }

            if (keyToken.Kind == TokenKind.CloseBrace)
            {
                if (!expectClosingBrace)
                {
                    throw tokenizer.Error("The VDF document has an unexpected closing brace.");
                }

                return values;
            }

            if (keyToken.Kind != TokenKind.Text)
            {
                throw tokenizer.Error("A VDF key was expected.");
            }

            var valueToken = tokenizer.Next();
            budget.AddNode(tokenizer);
            var value = valueToken.Kind switch
            {
                TokenKind.Text => VdfNode.Scalar(valueToken.Value!),
                TokenKind.OpenBrace => VdfNode.Object(ParseObject(
                    tokenizer,
                    budget,
                    depth + 1,
                    expectClosingBrace: true)),
                _ => throw tokenizer.Error("A VDF value or object was expected after a key.")
            };

            if (!values.TryAdd(keyToken.Value!, value))
            {
                throw tokenizer.Error("The VDF object contains a duplicate key.");
            }
        }
    }

    private sealed class ParseBudget
    {
        private int _nodes;

        public void AddNode(Tokenizer tokenizer)
        {
            _nodes++;
            if (_nodes > MaximumNodes)
            {
                throw tokenizer.Error("The VDF document contains too many entries.");
            }
        }
    }

    private enum TokenKind
    {
        Text,
        OpenBrace,
        CloseBrace,
        End
    }

    private readonly record struct Token(TokenKind Kind, string? Value = null);

    private sealed class Tokenizer
    {
        private readonly string _source;
        private readonly CancellationToken _cancellationToken;
        private int _index;
        private int _line = 1;

        public Tokenizer(string source, CancellationToken cancellationToken)
        {
            _source = source;
            _cancellationToken = cancellationToken;
        }

        public Token Next()
        {
            SkipTrivia();
            if (_index >= _source.Length)
            {
                return new Token(TokenKind.End);
            }

            var current = _source[_index];
            if (current == '{')
            {
                _index++;
                return new Token(TokenKind.OpenBrace);
            }

            if (current == '}')
            {
                _index++;
                return new Token(TokenKind.CloseBrace);
            }

            return current == '"' ? ReadQuotedText() : ReadBareText();
        }

        public InvalidDataException Error(string message) =>
            new($"{message} (line {_line})");

        public void ThrowIfCancellationRequested() =>
            _cancellationToken.ThrowIfCancellationRequested();

        private void SkipTrivia()
        {
            while (_index < _source.Length)
            {
                CheckCancellationPeriodically();
                var current = _source[_index];
                if (char.IsWhiteSpace(current))
                {
                    if (current == '\n')
                    {
                        _line++;
                    }

                    _index++;
                    continue;
                }

                if (current == '/'
                    && _index + 1 < _source.Length
                    && _source[_index + 1] == '/')
                {
                    _index += 2;
                    while (_index < _source.Length && _source[_index] != '\n')
                    {
                        _index++;
                    }

                    continue;
                }

                return;
            }
        }

        private Token ReadQuotedText()
        {
            _index++;
            var builder = new StringBuilder();

            while (_index < _source.Length)
            {
                CheckCancellationPeriodically();
                var current = _source[_index++];
                if (current == '"')
                {
                    return new Token(TokenKind.Text, builder.ToString());
                }

                if (current == '\n')
                {
                    _line++;
                }

                if (current == '\\' && _index < _source.Length)
                {
                    var escaped = _source[_index];
                    if (escaped is '\\' or '"')
                    {
                        if (builder.Length >= MaximumTokenCharacters)
                        {
                            throw Error("A VDF token is too long.");
                        }

                        builder.Append(escaped);
                        _index++;
                        continue;
                    }
                }

                if (builder.Length >= MaximumTokenCharacters)
                {
                    throw Error("A VDF token is too long.");
                }

                builder.Append(current);
            }

            throw Error("A quoted VDF value was not terminated.");
        }

        private Token ReadBareText()
        {
            var start = _index;
            while (_index < _source.Length
                   && !char.IsWhiteSpace(_source[_index])
                   && _source[_index] is not '{' and not '}')
            {
                CheckCancellationPeriodically();
                if (_index - start >= MaximumTokenCharacters)
                {
                    throw Error("A VDF token is too long.");
                }

                _index++;
            }

            if (_index == start)
            {
                throw Error($"Unexpected character '{_source[_index]}'.");
            }

            return new Token(TokenKind.Text, _source[start.._index]);
        }

        private void CheckCancellationPeriodically()
        {
            if ((_index & 0x3FF) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
