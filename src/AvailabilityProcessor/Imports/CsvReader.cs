using System.Text;

namespace AvailabilityProcessor.Imports;

/// <summary>One CSV record. <see cref="LineNumber"/> is the physical line where it starts (the header is line 1).</summary>
public sealed record CsvRecord(int LineNumber, string[] Fields, bool IsMalformed = false);

/// <summary>
/// Small RFC 4180 reader: comma separator, optional double-quoted fields ("" is an escaped quote),
/// CRLF or LF line endings, blank lines skipped. Kept dependency-free on purpose.
/// </summary>
public static class CsvReader
{
    public static IEnumerable<CsvRecord> Read(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordStartLine = 1;
        var inQuotes = false;
        var recordHasContent = false;

        int next;
        while ((next = reader.Read()) != -1)
        {
            var c = (char)next;

            if (inQuotes)
            {
                if (c == '"' && reader.Peek() == '"')
                {
                    field.Append('"');
                    reader.Read();
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    recordHasContent = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    recordHasContent = true;
                    break;
                case '\r':
                    break; // CRLF: the '\n' ends the record
                case '\n':
                    if (recordHasContent)
                    {
                        fields.Add(field.ToString());
                        yield return new CsvRecord(recordStartLine, fields.ToArray());
                    }

                    fields.Clear();
                    field.Clear();
                    recordHasContent = false;
                    line++;
                    recordStartLine = line;
                    break;
                default:
                    field.Append(c);
                    recordHasContent = true;
                    break;
            }
        }

        if (recordHasContent || inQuotes)
        {
            fields.Add(field.ToString());
            yield return new CsvRecord(recordStartLine, fields.ToArray(), IsMalformed: inQuotes);
        }
    }
}
