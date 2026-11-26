using System.ComponentModel.DataAnnotations;

namespace SmartWarranty_2.ViewModels;

public class BrandCsvImportRowViewModel
{
    public int SourceRowNumber { get; set; }

    [Required, StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [Url]
    public string OfficialWebsiteUrl { get; set; } = string.Empty;

    [Url]
    public string WarrantyClaimUrl { get; set; } = string.Empty;

    [Phone]
    public string SupportPhoneNumber { get; set; } = string.Empty;

    public string SupportEmailAddress { get; set; } = string.Empty;

    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    public List<string> Errors { get; set; } = new();
}

public class BrandCsvImportPreviewViewModel
{
    public string CsvPayload { get; set; } = string.Empty;
    public List<BrandCsvImportRowViewModel> Rows { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    public bool CanImport =>
        Rows.Count > 0
        && Errors.Count == 0
        && Rows.Any(row => row.Errors.Count == 0);

    public int ImportableRowCount => Rows.Count(row => row.Errors.Count == 0);

    public int SkippedRowCount => Rows.Count - ImportableRowCount;
}

public class CategoryCsvImportRowViewModel
{
    public int SourceRowNumber { get; set; }

    [Required, StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    public List<string> Errors { get; set; } = new();
}

public class CategoryCsvImportPreviewViewModel
{
    public string CsvPayload { get; set; } = string.Empty;
    public List<CategoryCsvImportRowViewModel> Rows { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    public bool CanImport =>
        Rows.Count > 0
        && Errors.Count == 0
        && Rows.All(row => row.Errors.Count == 0);
}
