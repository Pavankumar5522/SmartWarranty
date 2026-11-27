using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Models;
using Tesseract;
using UglyToad.PdfPig;

namespace SmartWarranty_2.Services;

public sealed record InvoiceRecognitionResult(
    string? ProductName,
    string? BrandName,
    string? CategoryName,
    string? ModelNumber,
    string? SerialNumber,
    DateTime? PurchaseDate,
    string? SellerName,
    int? WarrantyDurationMonths,
    bool HasRecognizedText,
    bool HasRecognizedFields);

public interface IInvoiceRecognitionService
{
    Task<InvoiceRecognitionResult> RecognizeAsync(Stream invoice, string fileExtension);
}

public sealed class InvoiceRecognitionService : IInvoiceRecognitionService
{
    private static readonly string[] DateFormats =
    {
        "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "dd/MM/yyyy",
        "M-d-yyyy", "MM-dd-yyyy", "d-M-yyyy", "dd-MM-yyyy", "d-MMM-yyyy", "dd-MMM-yyyy",
        "yyyy-MM-dd", "MMM d, yyyy", "MMMM d, yyyy"
    };

    public async Task<InvoiceRecognitionResult> RecognizeAsync(Stream invoice, string fileExtension)
    {
        string text;
        if (fileExtension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdfStream = new MemoryStream();
            await invoice.CopyToAsync(pdfStream);
            var pdfBytes = pdfStream.ToArray();
            using (var pdfTextStream = new MemoryStream(pdfBytes))
            using (var document = PdfDocument.Open(pdfTextStream))
            {
                text = string.Join(Environment.NewLine, document.GetPages().SelectMany(ExtractLines));
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                text = RecognizeScannedPdf(pdfBytes);
            }
        }
        else
        {
            using var memoryStream = new MemoryStream();
            await invoice.CopyToAsync(memoryStream);
            using var engine = CreateEngine();
            using var image = Pix.LoadFromMemory(memoryStream.ToArray());
            using var page = engine.Process(image);
            text = page.GetText() ?? string.Empty;
        }

        var productName = ExtractLabeledValue(text, @"(?:product|item)(?:\s+name)?|description");
        var brandName = ExtractLabeledValue(text, @"brand(?:\s*/\s*manufacturer)?|manufacturer");
        var categoryName = ExtractLabeledValue(text, @"category|product\s+category");
        var modelNumber = ExtractLabeledValue(text, @"model(?:\s*(?:no\.?|number))?|sku");
        var serialNumber = ExtractLabeledValue(text, @"serial(?:\s*(?:no\.?|number))?");
        var purchaseDate = ExtractDate(text);
        var sellerName = ExtractLabeledValue(text, @"seller\s+name|seller|sold\s+by|merchant|vendor|store");
        var warrantyDurationMonths = ExtractWarrantyDuration(text);
        var hasRecognizedFields = new[]
        {
            productName, brandName, categoryName, modelNumber, serialNumber, sellerName
        }.Any(value => !string.IsNullOrWhiteSpace(value))
            || purchaseDate.HasValue
            || warrantyDurationMonths.HasValue;

        return new InvoiceRecognitionResult(
            productName,
            brandName,
            categoryName,
            modelNumber,
            serialNumber,
            purchaseDate,
            sellerName,
            warrantyDurationMonths,
            !string.IsNullOrWhiteSpace(text),
            hasRecognizedFields);
    }

    private static IEnumerable<string> ExtractLines(UglyToad.PdfPig.Content.Page page)
    {
        var rows = new List<(double Baseline, List<(string Text, double Left, double Right)> Words)>();
        foreach (var word in page.GetWords().OrderByDescending(word => word.BoundingBox.Bottom))
        {
            var positionedWord = (word.Text, word.BoundingBox.Left, word.BoundingBox.Right);
            if (rows.Count == 0 || Math.Abs(rows[^1].Baseline - word.BoundingBox.Bottom) > 2.5)
            {
                rows.Add((word.BoundingBox.Bottom, new List<(string, double, double)> { positionedWord }));
            }
            else
            {
                rows[^1].Words.Add(positionedWord);
            }
        }

        var orderedRows = rows
            .OrderByDescending(row => row.Baseline)
            .Select(row => row.Words.OrderBy(word => word.Left).ToList())
            .ToList();

        for (var index = 0; index < orderedRows.Count; index++)
        {
            var words = orderedRows[index];
            if (index + 1 < orderedRows.Count
                && TryExtractProductTableRow(words, orderedRows[index + 1], out var productFields))
            {
                foreach (var field in productFields)
                {
                    yield return field;
                }

                index++;
                continue;
            }

            yield return ReconstructLine(words);
        }
    }

    private static bool TryExtractProductTableRow(
        List<(string Text, double Left, double Right)> headerWords,
        List<(string Text, double Left, double Right)> valueWords,
        out List<string> fields)
    {
        fields = new List<string>();
        var descriptionStart = FindColumnStart(headerWords, "description", "product", "item");
        var brandStart = FindColumnStart(headerWords, "brand");
        var modelStart = FindColumnStart(headerWords, "model");
        var serialStart = FindColumnStart(headerWords, "serial");
        if (!descriptionStart.HasValue || !brandStart.HasValue || !modelStart.HasValue || !serialStart.HasValue)
        {
            return false;
        }

        var columns = new[]
        {
            (Name: "Product Name", Start: descriptionStart.Value),
            (Name: "Brand", Start: brandStart.Value),
            (Name: "Model Number", Start: modelStart.Value),
            (Name: "Serial Number", Start: serialStart.Value)
        }.OrderBy(column => column.Start).ToArray();
        var columnBoundaries = columns
            .Select(column => column.Start)
            .Concat(new[]
            {
                FindColumnStart(headerWords, "qty", "quantity"),
                FindColumnStart(headerWords, "unit"),
                FindColumnStart(headerWords, "price")
            }.Where(start => start.HasValue).Select(start => start.GetValueOrDefault()))
            .OrderBy(start => start)
            .ToArray();

        for (var index = 0; index < columns.Length; index++)
        {
            var start = columns[index].Start;
            var end = columnBoundaries.Where(boundary => boundary > start).DefaultIfEmpty(double.MaxValue).Min();
            var value = string.Join(
                " ",
                valueWords
                    .Where(word => word.Left >= start - 1 && word.Left < end - 1)
                    .Select(word => word.Text));
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields.Add($"{columns[index].Name}: {value}");
            }
        }

        return fields.Count > 0;
    }

    private static double? FindColumnStart(
        IEnumerable<(string Text, double Left, double Right)> words,
        params string[] labels)
    {
        var labelSet = labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var word = words.FirstOrDefault(candidate =>
            labelSet.Contains(Regex.Replace(candidate.Text, @"[^A-Za-z]", string.Empty)));
        return word.Text == null ? null : word.Left;
    }

    private static string ReconstructLine(List<(string Text, double Left, double Right)> words)
    {
        var largestGap = 24d;
        var splitIndex = -1;
        for (var index = 1; index < words.Count; index++)
        {
            var gap = words[index].Left - words[index - 1].Right;
            if (gap > largestGap)
            {
                largestGap = gap;
                splitIndex = index;
            }
        }

        if (splitIndex < 0)
        {
            return string.Join(" ", words.Select(word => word.Text));
        }

        var label = string.Join(" ", words.Take(splitIndex).Select(word => word.Text));
        var value = string.Join(" ", words.Skip(splitIndex).Select(word => word.Text));
        return $"{label}: {value}";
    }

    private static string RecognizeScannedPdf(byte[] pdfBytes)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            throw new PlatformNotSupportedException("Scanned PDF recognition requires Windows 6.1 or later. Upload a text PDF or an image instead.");
        }

        using var engine = CreateEngine();
        using var document = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(1600, 2200));
        var text = new StringBuilder();
        var pageCount = Math.Min(document.GetPageCount(), 3);

        for (var index = 0; index < pageCount; index++)
        {
            using var pageReader = document.GetPageReader(index);
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();
            var pixels = pageReader.GetImage();
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var bounds = new Rectangle(0, 0, width, height);
            var bitmapData = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(pixels, 0, bitmapData.Scan0, pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            using var png = new MemoryStream();
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            using var image = Pix.LoadFromMemory(png.ToArray());
            using var recognizedPage = engine.Process(image);
            text.AppendLine(recognizedPage.GetText());
        }

        return text.ToString();
    }

    private static TesseractEngine CreateEngine()
    {
        var tessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(tessdataPath, "eng.traineddata")))
        {
            throw new InvalidOperationException("Local invoice recognition data is not installed.");
        }

        return new TesseractEngine(tessdataPath, "eng", EngineMode.Default);
    }

    private static string? ExtractLabeledValue(string text, string labelPattern)
    {
        var match = Regex.Match(
            text,
            $@"(?im)^\s*(?:{labelPattern})(?:\s*[:#-]\s*|\s+)(?<value>[^\r\n]{{1,120}})",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static DateTime? ExtractDate(string text)
    {
        var match = Regex.Match(
            text,
            @"(?im)\b(?:purchase|invoice|order|transaction|sale)\s+date\s*[:#-]?\s*(?<value>[A-Za-z0-9,./ -]{5,24})",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value.Trim();
        return DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            ? date.Date
            : null;
    }

    private static int? ExtractWarrantyDuration(string text)
    {
        var match = Regex.Match(
            text,
            @"(?i)(?:warranty\s*(?:period|duration|term)?\s*[:#-]?\s*(?<number>\d{1,3})\s*(?<unit>years?|months?)|(?<number>\d{1,3})\s*(?<unit>years?|months?)\s+(?:manufacturer\s+)?warranty)",
            RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var duration))
        {
            return null;
        }

        var months = match.Groups["unit"].Value.StartsWith("year", StringComparison.OrdinalIgnoreCase)
            ? duration * 12
            : duration;
        return months is >= 1 and <= 240 ? months : null;
    }
}
