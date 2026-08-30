using System.Globalization;
using System.Text;

namespace SteamSwitchboard.Services;

public static class SafeText
{
    public static string NormalizeIdentityInput(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        try
        {
            return trimmed.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
    }

    public static bool ContainsUnsafeIdentityCharacters(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var category = GetCategory(value, index, out var characterCount);
            if (IsUnsafeCategory(category))
            {
                return true;
            }

            index += characterCount - 1;
        }

        return false;
    }

    public static string SanitizeDisplayText(string? value, string fallback, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);

        var normalized = NormalizeIdentityInput(value);
        var builder = new StringBuilder(Math.Min(normalized.Length, maximumLength));
        for (var index = 0; index < normalized.Length; index++)
        {
            var category = GetCategory(normalized, index, out var characterCount);
            if (IsUnsafeCategory(category))
            {
                if (builder.Length > 0 && builder[^1] != ' ' && builder.Length < maximumLength)
                {
                    builder.Append(' ');
                }

                index += characterCount - 1;
                continue;
            }

            if (builder.Length + characterCount > maximumLength)
            {
                break;
            }

            builder.Append(normalized, index, characterCount);
            index += characterCount - 1;
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    private static UnicodeCategory GetCategory(string value, int index, out int characterCount)
    {
        var current = value[index];
        if (char.IsHighSurrogate(current)
            && index + 1 < value.Length
            && char.IsLowSurrogate(value[index + 1]))
        {
            characterCount = 2;
            return CharUnicodeInfo.GetUnicodeCategory(value, index);
        }

        characterCount = 1;
        return char.IsSurrogate(current)
            ? UnicodeCategory.Surrogate
            : CharUnicodeInfo.GetUnicodeCategory(current);
    }

    private static bool IsUnsafeCategory(UnicodeCategory category) =>
        category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate;
}
