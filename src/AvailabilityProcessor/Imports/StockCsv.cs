using AvailabilityProcessor.Domain;
using CsvHelper;

namespace AvailabilityProcessor.Imports;

/// <summary>A problem that makes the whole upload unusable (rejected synchronously with 400).</summary>
public sealed class InvalidCsvException(string message) : Exception(message);

/// <summary>One data record. <see cref="LineNumber"/> is the line of the file where it starts (the header is line 1).</summary>
public sealed record CsvRecord(int LineNumber, string[] Fields, bool HasBrokenQuoting = false);

/// <summary>Column positions resolved from the header row. Names match case-insensitively and column order is free.</summary>
public sealed class CsvColumns(IReadOnlyDictionary<string, int> positions, int width)
{
    public int Width { get; } = width;

    public string? Get(CsvRecord record, string column) =>
        positions.TryGetValue(column, out var index) && index < record.Fields.Length ? record.Fields[index] : null;
}

public sealed record ParsedCsv(CsvColumns Columns, IReadOnlyList<CsvRecord> Rows);

public sealed record RowMapping(StockUpdate? Update, IReadOnlyList<string> Errors);

/// <summary>
/// The stock import CSV format: header validation and row mapping. CsvHelper does the RFC 4180 parsing
/// (quoted fields, escaped quotes, CRLF or LF line endings, empty lines skipped).
/// </summary>
public static class StockCsv
{
    public static readonly IReadOnlyList<string> RequiredColumns =
        ["sellerId", "productId", "sizeId", "countryCode", "availableQuantity", "sourceVersion"];

    /// <summary>Reads the header and all data records. Only header problems throw; bad rows are reported per row later.</summary>
    /// <exception cref="InvalidCsvException">The file is empty, or the header is missing or repeats a required column.</exception>
    public static ParsedCsv Parse(TextReader reader)
    {
        // A record with broken quoting is still returned by the reader. Its line is remembered here,
        // so that the row fails on its own instead of being read as something it is not.
        var linesWithBrokenQuoting = new HashSet<long>();
        var options = CsvReaderOptions.Default with
        {
            Separator = ',',
            TrackLineNumbers = true,
            OnMalformedRecord = malformed => linesWithBrokenQuoting.Add(malformed.LineNumber),
        };

        using var csv = CsvReader.Create(reader, options, leaveOpen: true);
        var header = csv.Headers;
        if (header.Count == 0)
        {
            throw new InvalidCsvException($"The file is empty. Expected the header: {string.Join(',', RequiredColumns)}");
        }

        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? repeatedColumn = null;
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].TrimStart('﻿').Trim();
            if (name.Length > 0 && !positions.TryAdd(name, i))
            {
                repeatedColumn ??= name;
            }
        }

        // Missing columns are reported first. A file without a header row then gets a message that says what is
        // expected, and not a complaint about a value that its first data row happens to repeat.
        var missing = RequiredColumns.Where(column => !positions.ContainsKey(column)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidCsvException(
                $"Missing required column(s): {string.Join(", ", missing)}. Expected the header: {string.Join(',', RequiredColumns)}");
        }

        if (repeatedColumn is not null)
        {
            throw new InvalidCsvException($"Column '{repeatedColumn}' appears more than once in the header.");
        }

        var rows = new List<CsvRecord>();
        while (csv.Read())
        {
            rows.Add(new CsvRecord((int)csv.LineNumber, csv.Current.ToArray(), linesWithBrokenQuoting.Contains(csv.LineNumber)));
        }

        return new ParsedCsv(new CsvColumns(positions, header.Count), rows);
    }

    /// <summary>Turns one data row into a <see cref="StockUpdate"/> with the same rules as inventory events.</summary>
    public static RowMapping MapRow(CsvRecord record, CsvColumns columns, string causationId)
    {
        if (record.HasBrokenQuoting)
        {
            return new RowMapping(null, ["Malformed CSV record: a quote is not closed or is in the wrong place."]);
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
