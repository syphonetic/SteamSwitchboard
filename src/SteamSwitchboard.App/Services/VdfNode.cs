namespace SteamSwitchboard.Services;

public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode>? _children;

    private VdfNode(string? value, Dictionary<string, VdfNode>? children)
    {
        Value = value;
        _children = children;
    }

    public string? Value { get; }

    public IReadOnlyDictionary<string, VdfNode> Children =>
        _children ?? EmptyChildren;

    public bool IsObject => _children is not null;

    private static IReadOnlyDictionary<string, VdfNode> EmptyChildren { get; } =
        new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);

    public static VdfNode Scalar(string value) => new(value, null);

    public static VdfNode Object(Dictionary<string, VdfNode> children) => new(null, children);

    public VdfNode? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _children is not null && _children.TryGetValue(key, out var node)
            ? node
            : null;
    }

    public string? GetValue(string key) => Get(key)?.Value;
}
