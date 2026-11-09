using AvailabilityProcessor.Domain;
using AvailabilityProcessor.Imports;
using AvailabilityProcessor.Tests.Support;

namespace AvailabilityProcessor.Tests;

public sealed class StockCsvTests
{
    private const string Header = "sellerId,productId,sizeId,countryCode,availableQuantity,sourceVersion";

    [Fact]
    public void Parses_crlf_files_and_maps_columns_by_header_name()
    {
        const string csv = "productId,sellerId,sizeId,countryCode,sourceVersion,availableQuantity\r\nstyle-1,seller-001,size-34,pt,4,7\r\n\r\n";

        var parsed = StockCsv.Parse(new StringReader(csv));

        var record = Assert.Single(parsed.Rows);
        Assert.Equal(2, record.LineNumber);
        var mapping = StockCsv.MapRow(record, parsed.Columns, "test");
        Assert.Empty(mapping.Errors);
        Assert.Equal(new StockKey("seller-001", "style-1", "size-34", "PT"), mapping.Update!.Key);
        Assert.Equal((7, 4L), (mapping.Update.AvailableQuantity, mapping.Update.SourceVersion));
    }

    [Fact]
    public void Provided_fixture_parses_into_10000_valid_rows()
    {
        using var reader = File.OpenText(TestData.BulkCsv);

        var parsed = StockCsv.Parse(reader);

        Assert.Equal(10_000, parsed.Rows.Count);
        Assert.All(parsed.Rows, row => Assert.Empty(StockCsv.MapRow(row, parsed.Columns, "test").Errors));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("sellerId,productId,sizeId,countryCode,availableQuantity\nseller-1,style-1,size-34,PT,1", "sourceVersion")]
    [InlineData("sellerId,sellerId,productId,sizeId,countryCode,availableQuantity,sourceVersion\n", "more than once")]
    public void Unusable_header_rejects_the_whole_upload(string csv, string expectedMessagePart)
    {
        var ex = Assert.Throws<InvalidCsvException>(() => StockCsv.Parse(new StringReader(csv)));

        Assert.Contains(expectedMessagePart, ex.Message);
    }

    [Theory]
    [InlineData("seller-1,style-1,size-34,PT,abc,1", "availableQuantity must be an integer.")]
    [InlineData("seller-1,style-1,size-34,PT,-1,1", "availableQuantity must be between 0")]
    [InlineData("seller-1,style-1,size-34,Portugal,1,1", "countryCode must be a two-letter")]
    [InlineData("seller-1,,size-34,PT,1,1", "productId is required.")]
    [InlineData("seller-1,style-1,size-34,PT,1", "Expected 6 fields but found 5.")]
    [InlineData("seller-1,style-1,size-34,PT,1,\"1", "Malformed CSV record")] // a quote that is never closed
    public void Invalid_row_becomes_a_row_level_error(string row, string expectedError)
    {
        var parsed = StockCsv.Parse(new StringReader(Header + "\n" + row));

        var mapping = StockCsv.MapRow(Assert.Single(parsed.Rows), parsed.Columns, "test");

        Assert.Null(mapping.Update);
        Assert.Contains(mapping.Errors, error => error.StartsWith(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void Quoted_fields_may_contain_commas_quotes_and_line_breaks()
    {
        const string csv = Header + "\n" +
            "\"seller, \"\"A\"\"\",style-1,size-34,PT,1,1\n" + // line 2: a comma and escaped quotes
            "\"seller\r\nB\",style-2,size-36,PT,2,2\n" +       // lines 3 and 4: a line break inside the quotes
            "seller-C,style-3,size-38,PT,3,3";                 // line 5

        var parsed = StockCsv.Parse(new StringReader(csv));

        Assert.Equal(new[] { 2, 3, 5 }, parsed.Rows.Select(row => row.LineNumber));
        Assert.Equal(new[] { "seller, \"A\"", "seller\r\nB", "seller-C" }, parsed.Rows.Select(row => row.Fields[0]));
        Assert.All(parsed.Rows, row => Assert.Empty(StockCsv.MapRow(row, parsed.Columns, "test").Errors));
    }
}
