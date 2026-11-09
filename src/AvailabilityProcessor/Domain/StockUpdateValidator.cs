using System.Globalization;
using System.Text.RegularExpressions;

namespace AvailabilityProcessor.Domain;

/// <summary>An integer input that may be missing or malformed (CSV cells arrive as text, JSON numbers as numbers).</summary>
public readonly record struct IntegerInput(long? Value, bool IsMalformed)
{
    public static IntegerInput From(long? value) => new(value, false);

    public static IntegerInput Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(null, false);
        }

        return long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? new(value, false)
            : new(null, true);
    }
}

public sealed record StockUpdateValidation(StockUpdate? Update, IReadOnlyList<string> Errors)
{
    public bool IsValid => Update is not null;
}

/// <summary>Field rules shared by inventory events and CSV rows, so both input paths accept exactly the same data.</summary>
public static partial class StockUpdateValidator
{
    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryCodeFormat();

    public static StockUpdateValidation Validate(
        string? sellerId,
        string? productId,
        string? sizeId,
        string? countryCode,
        IntegerInput availableQuantity,
        IntegerInput sourceVersion,
        string causationId)
    {
        var errors = new List<string>();

        var seller = RequiredText(sellerId, "sellerId", errors);
        var product = RequiredText(productId, "productId", errors);
        var size = RequiredText(sizeId, "sizeId", errors);

        // ISO 3166-1 alpha-2 codes are upper case. Normalising makes "pt" and "PT" the same stock stream.
        var country = RequiredText(countryCode, "countryCode", errors)?.ToUpperInvariant();
        if (country is not null && !CountryCodeFormat().IsMatch(country))
        {
            errors.Add("countryCode must be a two-letter ISO 3166-1 alpha-2 code.");
        }

        var quantity = RequiredInteger(availableQuantity, "availableQuantity", 0, int.MaxValue, errors);
        var version = RequiredInteger(sourceVersion, "sourceVersion", 0, long.MaxValue, errors);

        if (errors.Count > 0)
        {
            return new StockUpdateValidation(null, errors);
        }

        var key = new StockKey(seller!, product!, size!, country!);
        return new StockUpdateValidation(new StockUpdate(key, (int)quantity!.Value, version!.Value, causationId), errors);
    }

    private static string? RequiredText(string? value, string field, List<string> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errors.Add($"{field} is required.");
            return null;
        }

        return trimmed;
    }

    private static long? RequiredInteger(IntegerInput input, string field, long min, long max, List<string> errors)
    {
        if (input.IsMalformed)
        {
            errors.Add($"{field} must be an integer.");
            return null;
        }

        if (input.Value is not { } value)
        {
            errors.Add($"{field} is required.");
            return null;
        }

        if (value < min || value > max)
        {
            errors.Add($"{field} must be between {min} and {max}.");
            return null;
        }

        return value;
    }
}
