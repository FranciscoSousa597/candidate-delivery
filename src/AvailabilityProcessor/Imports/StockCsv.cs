using AvailabilityProcessor.Domain;

namespace AvailabilityProcessor.Imports;

/// <summary>A problem that makes the whole upload unusable (rejected synchronously with 400).</summary>
public sealed class CsvFormatException(string message) : Exception(message);

/// <summary>Column positions resolved from the header row. Names match case-insensitively and column order is free.</summary>
public sealed class CsvColumns(IReadOnlyDictionary<string, int> positions, int width)
{
    public int Width { get; } = width;

    public string? Get(CsvRecord record, string column) =>
        positions.TryGetValue(column, out var index) && index < record.Fields.Length ? record.Fields[index] : null;
}

public sealed record ParsedCsv(CsvColumns Columns, IReadOnlyList<CsvRecord> Rows);

public sealed record RowMapping(StockUpdate? Update, IReadOnlyList<string> Errors);

/// <summary>The stock import CSV format: header validation and row mapping.</summary>
public static class StockCsv
{
    public static readonly IReadOnlyList<string> RequiredColumns =
        ["sellerId", "productId", "sizeId", "countryCode", "availableQuantity", "sourceVersion"];

    /// <summary>Reads the header and all data records. Only header problems throw; bad rows are reported per row later.</summary>
    /// <exception cref="CsvFormatException">The file is empty or the header is missing required columns.</exception>
    public static ParsedCsv Parse(TextReader reader)
    {
        using var records = CsvReader.Read(reader).GetEnumerator();
        if (!records.MoveNext())
        {
            throw new CsvFormatException($"The file is empty. Expected the header: {string.Join(',', RequiredColumns)}");
        }

        var header = records.Current;
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Fields.Length; i++)
        {
            var name = header.Fields[i].TrimStart('﻿').Trim();
            if (name.Length > 0 && !positions.TryAdd(name, i))
            {
                throw new CsvFormatException($"Column '{name}' appears more than once in the header.");
            }
        }

        var missing = RequiredColumns.Where(column => !positions.ContainsKey(column)).ToList();
        if (missing.Count > 0)
        {
            throw new CsvFormatException(
                $"Missing required column(s): {string.Join(", ", missing)}. Expected the header: {string.Join(',', RequiredColumns)}");
        }

        var rows = new List<CsvRecord>();
        while (records.MoveNext())
        {
            rows.Add(records.Current);
        }

        return new ParsedCsv(new CsvColumns(positions, header.Fields.Length), rows);
    }

    /// <summary>Turns one data row into a <see cref="StockUpdate"/> with the same rules as inventory events.</summary>
    public static RowMapping MapRow(CsvRecord record, CsvColumns columns, string causationId)
    {
        if (record.IsMalformed)
        {
            return new RowMapping(null, ["Malformed CSV record: unterminated quoted field."]);
        }

        if (record.Fields.Length != columns.Width)
        {
            return new RowMapping(null, [$"Expected {columns.Width} fields but found {record.Fields.Length}."]);
        }

        var validation = StockUpdateValidator.Validate(
            columns.Get(record, "sellerId"),
            columns.Get(record, "productId"),
            columns.Get(record, "sizeId"),
            columns.Get(record, "countryCode"),
            IntegerInput.Parse(columns.Get(record, "availableQuantity")),
            IntegerInput.Parse(columns.Get(record, "sourceVersion")),
            causationId);

        return new RowMapping(validation.Update, validation.Errors);
    }
}
