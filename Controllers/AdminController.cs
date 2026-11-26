using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using SmartWarranty_2.Data;
using SmartWarranty_2.Models;
using SmartWarranty_2.Services;
using SmartWarranty_2.ViewModels;

namespace SmartWarranty_2.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private const int MaxCatalogCsvBytes = 1024 * 1024;
    private const int MaxCatalogCsvRows = 2000;
    private static readonly string[] BrandCsvHeaders =
    {
        "Name", "OfficialWebsiteUrl", "WarrantyClaimUrl", "SupportPhoneNumber", "SupportEmailAddress", "Description"
    };
    private static readonly string[] CategoryCsvHeaders = { "Name", "Description" };

    private readonly SmartWarrantyDbContext _context;
    private readonly DashboardService _dashboardService;

    public AdminController(SmartWarrantyDbContext context, DashboardService dashboardService)
    {
        _context = context;
        _dashboardService = dashboardService;
    }

    public async Task<IActionResult> Dashboard()
    {
        var metrics = await _dashboardService.GetAdminDashboardMetricsAsync();
        var customers = await _context.Customers.OrderByDescending(c => c.CreatedAt).Take(5).ToListAsync();
        var recentServiceRequests = await _context.ServiceRequests.Include(s => s.Customer).Include(s => s.Product).OrderByDescending(s => s.RequestDate).Take(5).ToListAsync();

        ViewBag.Metrics = metrics;
        ViewBag.Customers = customers;
        ViewBag.RecentRequests = recentServiceRequests;
        return View();
    }

    public async Task<IActionResult> Customers()
    {
        var customers = await _context.Customers.OrderBy(c => c.FirstName).ToListAsync();
        return View(customers);
    }

    public async Task<IActionResult> Brands()
    {
        var brands = await _context.Brands.OrderBy(b => b.Name).ToListAsync();
        return View(brands);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBrand(Brand brand)
    {
        NormalizeOptionalBrandFields(brand);
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Enter valid brand and support details.";
            return RedirectToAction(nameof(Brands));
        }

        brand.Name = brand.Name.Trim();
        var existingBrandNames = await _context.Brands
            .AsNoTracking()
            .Select(existing => existing.Name)
            .ToListAsync();
        var exists = HasDuplicateCatalogName(existingBrandNames, brand.Name);
        if (exists)
        {
            TempData["ErrorMessage"] = "Brand already exists.";
            return RedirectToAction(nameof(Brands));
        }

        _context.Brands.Add(brand);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Brand added successfully.";
        return RedirectToAction(nameof(Brands));
    }

    [HttpGet]
    public IActionResult DownloadBrandCsvTemplate()
    {
        var content = CreateCsvTemplate(BrandCsvHeaders);
        return File(content, "text/csv; charset=utf-8", "brand-import-template.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewBrandImport(IFormFile? csvFile)
    {
        var model = new BrandCsvImportPreviewViewModel();
        if (csvFile == null || csvFile.Length == 0)
        {
            model.Errors.Add("Choose a CSV file to preview.");
            return View("BrandImportPreview", model);
        }

        if (!Path.GetExtension(csvFile.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            model.Errors.Add("Choose a .csv file. Excel workbooks can be exported as CSV.");
            return View("BrandImportPreview", model);
        }

        if (csvFile.Length > MaxCatalogCsvBytes)
        {
            model.Errors.Add("The CSV file must be 1 MB or smaller.");
            return View("BrandImportPreview", model);
        }

        await using var stream = new MemoryStream();
        await csvFile.CopyToAsync(stream);
        var csvBytes = stream.ToArray();
        model.CsvPayload = Convert.ToBase64String(csvBytes);
        await PopulateBrandImportPreviewAsync(model, csvBytes);
        return View("BrandImportPreview", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportBrands(BrandCsvImportPreviewViewModel model)
    {
        var preview = new BrandCsvImportPreviewViewModel
        {
            CsvPayload = model?.CsvPayload ?? string.Empty
        };
        if (!TryDecodeCsvPayload(preview.CsvPayload, preview.Errors, out var csvBytes))
        {
            return View("BrandImportPreview", preview);
        }

        await PopulateBrandImportPreviewAsync(preview, csvBytes);
        var importableRows = preview.Rows
            .Where(row => row.Errors.Count == 0)
            .ToList();
        if (preview.Errors.Count > 0 || importableRows.Count == 0)
        {
            return View("BrandImportPreview", preview);
        }

        _context.Brands.AddRange(importableRows.Select(row => new Brand
        {
            Name = row.Name.Trim(),
            OfficialWebsiteUrl = row.OfficialWebsiteUrl.Trim(),
            WarrantyClaimUrl = row.WarrantyClaimUrl.Trim(),
            SupportPhoneNumber = row.SupportPhoneNumber.Trim(),
            SupportEmailAddress = row.SupportEmailAddress.Trim(),
            Description = row.Description.Trim()
        }));
        await _context.SaveChangesAsync();
        var brandLabel = importableRows.Count == 1 ? "brand" : "brands";
        var resultMessage = $"{importableRows.Count} {brandLabel} imported successfully.";
        if (preview.SkippedRowCount > 0)
        {
            var skippedLabel = preview.SkippedRowCount == 1 ? "row was" : "rows were";
            resultMessage += $" {preview.SkippedRowCount} invalid {skippedLabel} skipped; review the preview for details.";
        }

        TempData["SuccessMessage"] = resultMessage;
        return RedirectToAction(nameof(Brands));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBrand([Bind("Id,Name,OfficialWebsiteUrl,WarrantyClaimUrl,SupportPhoneNumber,SupportEmailAddress,Description")] Brand brand)
    {
        NormalizeOptionalBrandFields(brand);
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Enter valid brand and support details.";
            return RedirectToAction(nameof(Brands));
        }

        var existingBrand = await _context.Brands.FindAsync(brand.Id);
        if (existingBrand == null)
        {
            return NotFound();
        }

        brand.Name = brand.Name.Trim();
        var existingBrandNames = await _context.Brands
            .AsNoTracking()
            .Where(existing => existing.Id != brand.Id)
            .Select(existing => existing.Name)
            .ToListAsync();
        var duplicateName = HasDuplicateCatalogName(existingBrandNames, brand.Name);
        if (duplicateName)
        {
            TempData["ErrorMessage"] = "Another brand already uses that name.";
            return RedirectToAction(nameof(Brands));
        }

        existingBrand.Name = brand.Name;
        existingBrand.OfficialWebsiteUrl = brand.OfficialWebsiteUrl;
        existingBrand.WarrantyClaimUrl = brand.WarrantyClaimUrl;
        existingBrand.SupportPhoneNumber = brand.SupportPhoneNumber;
        existingBrand.SupportEmailAddress = brand.SupportEmailAddress;
        existingBrand.Description = brand.Description;
        existingBrand.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Brand details updated successfully.";
        return RedirectToAction(nameof(Brands));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBrand(int id)
    {
        var brand = await _context.Brands.FindAsync(id);
        if (brand == null)
        {
            return NotFound();
        }

        var productCount = await _context.Products.CountAsync(product => product.BrandId == id);
        if (productCount > 0)
        {
            TempData["ErrorMessage"] = $"Cannot delete {brand.Name}; {productCount} registered product(s) use this brand.";
            return RedirectToAction(nameof(Brands));
        }

        _context.Brands.Remove(brand);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Brand deleted successfully.";
        return RedirectToAction(nameof(Brands));
    }

    public async Task<IActionResult> Categories()
    {
        var categories = await _context.ProductCategories.OrderBy(c => c.Name).ToListAsync();
        return View(categories);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(ProductCategory category)
    {
        NormalizeOptionalCategoryDescription(category);
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Enter a valid category name and description.";
            return RedirectToAction(nameof(Categories));
        }

        category.Name = category.Name.Trim();
        var existingCategoryNames = await _context.ProductCategories
            .AsNoTracking()
            .Select(existing => existing.Name)
            .ToListAsync();
        var exists = HasDuplicateCatalogName(existingCategoryNames, category.Name);
        if (exists)
        {
            TempData["ErrorMessage"] = "Category already exists.";
            return RedirectToAction(nameof(Categories));
        }

        _context.ProductCategories.Add(category);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Category added successfully.";
        return RedirectToAction(nameof(Categories));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCategory([Bind("Id,Name,Description")] ProductCategory category)
    {
        NormalizeOptionalCategoryDescription(category);
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Enter a valid category name and description.";
            return RedirectToAction(nameof(Categories));
        }

        var existingCategory = await _context.ProductCategories.FindAsync(category.Id);
        if (existingCategory == null)
        {
            return NotFound();
        }

        category.Name = category.Name.Trim();
        var existingNames = await _context.ProductCategories
            .AsNoTracking()
            .Where(existing => existing.Id != category.Id)
            .Select(existing => existing.Name)
            .ToListAsync();
        if (HasDuplicateCatalogName(existingNames, category.Name))
        {
            TempData["ErrorMessage"] = "Another category already uses that name.";
            return RedirectToAction(nameof(Categories));
        }

        existingCategory.Name = category.Name;
        existingCategory.Description = category.Description.Trim();
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Category updated successfully.";
        return RedirectToAction(nameof(Categories));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        var category = await _context.ProductCategories.FindAsync(id);
        if (category == null)
        {
            return NotFound();
        }

        var productCount = await _context.Products.CountAsync(product => product.CategoryId == id);
        if (productCount > 0)
        {
            TempData["ErrorMessage"] = $"Cannot delete {category.Name}; {productCount} registered product(s) use this category.";
            return RedirectToAction(nameof(Categories));
        }

        _context.ProductCategories.Remove(category);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Category deleted successfully.";
        return RedirectToAction(nameof(Categories));
    }

    [HttpGet]
    public IActionResult DownloadCategoryCsvTemplate()
    {
        var content = CreateCsvTemplate(CategoryCsvHeaders);
        return File(content, "text/csv; charset=utf-8", "category-import-template.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewCategoryImport(IFormFile? csvFile)
    {
        var model = new CategoryCsvImportPreviewViewModel();
        if (csvFile == null || csvFile.Length == 0)
        {
            model.Errors.Add("Choose a CSV file to preview.");
            return View("CategoryImportPreview", model);
        }

        if (!Path.GetExtension(csvFile.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            model.Errors.Add("Choose a .csv file. Excel workbooks can be exported as CSV.");
            return View("CategoryImportPreview", model);
        }

        if (csvFile.Length > MaxCatalogCsvBytes)
        {
            model.Errors.Add("The CSV file must be 1 MB or smaller.");
            return View("CategoryImportPreview", model);
        }

        await using var stream = new MemoryStream();
        await csvFile.CopyToAsync(stream);
        var csvBytes = stream.ToArray();
        model.CsvPayload = Convert.ToBase64String(csvBytes);
        await PopulateCategoryImportPreviewAsync(model, csvBytes);
        return View("CategoryImportPreview", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCategories(CategoryCsvImportPreviewViewModel model)
    {
        var preview = new CategoryCsvImportPreviewViewModel
        {
            CsvPayload = model?.CsvPayload ?? string.Empty
        };
        if (!TryDecodeCsvPayload(preview.CsvPayload, preview.Errors, out var csvBytes))
        {
            return View("CategoryImportPreview", preview);
        }

        await PopulateCategoryImportPreviewAsync(preview, csvBytes);
        if (!preview.CanImport)
        {
            return View("CategoryImportPreview", preview);
        }

        _context.ProductCategories.AddRange(preview.Rows.Select(row => new ProductCategory
        {
            Name = row.Name.Trim(),
            Description = row.Description.Trim()
        }));
        await _context.SaveChangesAsync();
        var categoryLabel = preview.Rows.Count == 1 ? "category" : "categories";
        TempData["SuccessMessage"] = $"{preview.Rows.Count} {categoryLabel} imported successfully.";
        return RedirectToAction(nameof(Categories));
    }

    public async Task<IActionResult> Support()
    {
        var brands = await _context.Brands.OrderBy(b => b.Name).ToListAsync();
        return View(brands);
    }

    public async Task<IActionResult> Products()
    {
        var products = await _context.Products
            .Include(p => p.Brand)
            .Include(p => p.Category)
            .Include(p => p.Customer)
            .OrderBy(p => p.Name)
            .ToListAsync();
        return View(products);
    }

    public async Task<IActionResult> Warranties()
    {
        var warranties = await _context.Warranties
            .Include(w => w.Product)
            .ThenInclude(p => p.Customer)
            .OrderByDescending(w => w.ExpiryDate)
            .ToListAsync();
        return View(warranties);
    }

    public async Task<IActionResult> ServiceRequests()
    {
        var requests = await _context.ServiceRequests
            .Include(s => s.Customer)
            .Include(s => s.Product)
            .ThenInclude(p => p.Brand)
            .OrderByDescending(s => s.RequestDate)
            .ToListAsync();
        return View(requests);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateServiceStatus(int id, string status, string trackingInfo)
    {
        var request = await _context.ServiceRequests.FindAsync(id);
        if (request == null)
        {
            return NotFound();
        }

        request.Status = status;
        request.TrackingInfo = trackingInfo;
        request.UpdatedAt = DateTime.UtcNow;

        _context.ServiceHistories.Add(new ServiceHistory
        {
            ServiceRequestId = request.Id,
            Status = status,
            Notes = trackingInfo,
            ChangedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Service request updated.";
        return RedirectToAction(nameof(ServiceRequests));
    }

    public async Task<IActionResult> Reports()
    {
        var customers = await _context.Customers.CountAsync();
        var products = await _context.Products.CountAsync();
        var today = DateTime.UtcNow.Date;
        var active = await _context.Warranties.CountAsync(w => w.StartDate < today.AddDays(1) && w.ExpiryDate >= today);
        var expired = await _context.Warranties.CountAsync(w => w.ExpiryDate < today);
        var expiringSoon = await _context.Warranties.CountAsync(w => w.ExpiryDate >= today && w.ExpiryDate < today.AddDays(31));
        var serviceRequests = await _context.ServiceRequests.CountAsync();
        var resolved = await _context.ServiceRequests.CountAsync(s => s.Status == "Resolved" || s.Status == "Closed");

        ViewBag.Report = new
        {
            Customers = customers,
            Products = products,
            Active = active,
            Expired = expired,
            ExpiringSoon = expiringSoon,
            ServiceRequests = serviceRequests,
            Resolved = resolved
        };

        return View();
    }

    private async Task PopulateBrandImportPreviewAsync(BrandCsvImportPreviewViewModel model, byte[] csvBytes)
    {
        var parsed = ParseCatalogCsv(csvBytes, BrandCsvHeaders);
        model.Errors.AddRange(parsed.Errors);
        foreach (var record in parsed.Rows)
        {
            model.Rows.Add(new BrandCsvImportRowViewModel
            {
                SourceRowNumber = record.RowNumber,
                Name = record.Values.GetValueOrDefault("Name", string.Empty),
                OfficialWebsiteUrl = record.Values.GetValueOrDefault("OfficialWebsiteUrl", string.Empty),
                WarrantyClaimUrl = record.Values.GetValueOrDefault("WarrantyClaimUrl", string.Empty),
                SupportPhoneNumber = record.Values.GetValueOrDefault("SupportPhoneNumber", string.Empty),
                SupportEmailAddress = record.Values.GetValueOrDefault("SupportEmailAddress", string.Empty),
                Description = record.Values.GetValueOrDefault("Description", string.Empty)
            });
        }

        if (model.Rows.Count == 0 && model.Errors.Count == 0)
        {
            model.Errors.Add("The CSV contains no data rows.");
        }

        await ValidateBrandImportRowsAsync(model.Rows);
    }

    private async Task PopulateCategoryImportPreviewAsync(CategoryCsvImportPreviewViewModel model, byte[] csvBytes)
    {
        var parsed = ParseCatalogCsv(csvBytes, CategoryCsvHeaders);
        model.Errors.AddRange(parsed.Errors);
        foreach (var record in parsed.Rows)
        {
            model.Rows.Add(new CategoryCsvImportRowViewModel
            {
                SourceRowNumber = record.RowNumber,
                Name = record.Values.GetValueOrDefault("Name", string.Empty),
                Description = record.Values.GetValueOrDefault("Description", string.Empty)
            });
        }

        if (model.Rows.Count == 0 && model.Errors.Count == 0)
        {
            model.Errors.Add("The CSV contains no data rows.");
        }

        await ValidateCategoryImportRowsAsync(model.Rows);
    }

    private async Task ValidateBrandImportRowsAsync(List<BrandCsvImportRowViewModel> rows)
    {
        var existingNames = await _context.Brands.AsNoTracking().Select(brand => brand.Name).ToListAsync();
        var seenNames = CreateNameSet(existingNames);
        foreach (var row in rows)
        {
            row.Errors.Clear();
            row.Name = (row.Name ?? string.Empty).Trim();
            row.OfficialWebsiteUrl = (row.OfficialWebsiteUrl ?? string.Empty).Trim();
            row.WarrantyClaimUrl = (row.WarrantyClaimUrl ?? string.Empty).Trim();
            row.SupportPhoneNumber = (row.SupportPhoneNumber ?? string.Empty).Trim();
            row.SupportEmailAddress = (row.SupportEmailAddress ?? string.Empty).Trim();
            row.Description = (row.Description ?? string.Empty).Trim();
            AddValidationErrors(row, row.Errors);
            if (!string.IsNullOrWhiteSpace(row.SupportEmailAddress)
                && !new EmailAddressAttribute().IsValid(row.SupportEmailAddress))
            {
                row.Errors.Add("SupportEmailAddress: Enter a valid email address, or leave this optional field blank.");
            }
            AddDuplicateNameError(row.Name, seenNames, row.Errors, "brand");
        }
    }

    private async Task ValidateCategoryImportRowsAsync(List<CategoryCsvImportRowViewModel> rows)
    {
        var existingNames = await _context.ProductCategories.AsNoTracking().Select(category => category.Name).ToListAsync();
        var seenNames = CreateNameSet(existingNames);
        foreach (var row in rows)
        {
            row.Errors.Clear();
            row.Name = (row.Name ?? string.Empty).Trim();
            row.Description = (row.Description ?? string.Empty).Trim();
            AddValidationErrors(row, row.Errors);
            AddDuplicateNameError(row.Name, seenNames, row.Errors, "category");
        }
    }

    private static void AddValidationErrors(object row, List<string> errors)
    {
        var validationResults = new List<ValidationResult>();
        Validator.TryValidateObject(row, new ValidationContext(row), validationResults, validateAllProperties: true);
        errors.AddRange(validationResults.Select(result =>
        {
            var member = result.MemberNames.FirstOrDefault();
            var message = result.ErrorMessage ?? "The value is invalid.";
            return string.IsNullOrWhiteSpace(member) ? message : $"{member}: {message}";
        }));
    }

    private static HashSet<string> CreateNameSet(IEnumerable<string> names)
    {
        return new HashSet<string>(
            names.Select(NormalizeCatalogName),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddDuplicateNameError(
        string name,
        HashSet<string> seenNames,
        List<string> errors,
        string catalogName)
    {
        if (!string.IsNullOrWhiteSpace(name)
            && !seenNames.Add(NormalizeCatalogName(name)))
        {
            errors.Add($"This {catalogName} name already exists in the catalog or another CSV row.");
        }
    }

    private static string NormalizeCatalogName(string name)
    {
        return string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private void NormalizeOptionalBrandFields(Brand brand)
    {
        NormalizeOptionalField(nameof(Brand.OfficialWebsiteUrl), brand.OfficialWebsiteUrl, value => brand.OfficialWebsiteUrl = value);
        NormalizeOptionalField(nameof(Brand.WarrantyClaimUrl), brand.WarrantyClaimUrl, value => brand.WarrantyClaimUrl = value);
        NormalizeOptionalField(nameof(Brand.SupportPhoneNumber), brand.SupportPhoneNumber, value => brand.SupportPhoneNumber = value);
        NormalizeOptionalField(nameof(Brand.SupportEmailAddress), brand.SupportEmailAddress, value => brand.SupportEmailAddress = value);
        NormalizeOptionalField(nameof(Brand.Description), brand.Description, value => brand.Description = value);
    }

    private void NormalizeOptionalCategoryDescription(ProductCategory category)
    {
        NormalizeOptionalField(
            nameof(ProductCategory.Description),
            category.Description,
            value => category.Description = value);
    }

    private void NormalizeOptionalField(string fieldName, string? value, Action<string> assign)
    {
        if (value is null)
        {
            ModelState.Remove(fieldName);
            assign(string.Empty);
        }
    }

    private static string CanonicalizeCsvHeader(string header)
    {
        var normalized = new string(header.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized switch
        {
            "NAME" or "BRANDNAME" or "CATEGORYNAME" => "Name",
            "OFFICIALWEBSITEURL" or "OFFICIALWEBSITE" or "WEBSITE" => "OfficialWebsiteUrl",
            "WARRANTYCLAIMURL" or "WARRANTYAPPLICATIONURL" or "WARRANTYURL" => "WarrantyClaimUrl",
            "SUPPORTPHONENUMBER" or "SUPPORTPHONE" or "PHONE" => "SupportPhoneNumber",
            "SUPPORTEMAILADDRESS" or "SUPPORTEMAIL" or "SUPPORTEMAILID" or "EMAILADDRESS" or "EMAILID" or "EMAIL" => "SupportEmailAddress",
            "DESCRIPTION" => "Description",
            _ => header
        };
    }

    private static bool HasDuplicateCatalogName(IEnumerable<string> names, string candidate)
    {
        var normalizedCandidate = NormalizeCatalogName(candidate);
        return names.Any(name =>
            string.Equals(
                NormalizeCatalogName(name),
                normalizedCandidate,
                StringComparison.OrdinalIgnoreCase));
    }

    private static CsvReadResult ParseCatalogCsv(byte[] csvBytes, IReadOnlyCollection<string> allowedHeaders)
    {
        var rows = new List<CsvDataRow>();
        var errors = new List<string>();
        try
        {
            using var stream = new MemoryStream(csvBytes);
            using var parser = new TextFieldParser(stream, Encoding.UTF8, detectEncoding: true)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false
            };
            parser.SetDelimiters(",");

            var rawHeaders = parser.ReadFields();
            if (rawHeaders == null || rawHeaders.Length == 0)
            {
                errors.Add("The CSV is empty or has no header row.");
                return new CsvReadResult(rows, errors);
            }

            var headers = rawHeaders
                .Select((header, index) =>
                {
                    var originalName = (header ?? string.Empty).Trim().TrimStart('\uFEFF');
                    return (Name: CanonicalizeCsvHeader(originalName), OriginalName: originalName, Index: index);
                })
                .ToArray();
            var allowedHeaderSet = new HashSet<string>(allowedHeaders, StringComparer.OrdinalIgnoreCase);
            var headerIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header.Name))
                {
                    errors.Add("Every CSV column must have a header.");
                }
                else if (!allowedHeaderSet.Contains(header.Name))
                {
                    errors.Add($"Unsupported CSV column: {header.OriginalName}.");
                }
                else if (!headerIndexes.TryAdd(header.Name, header.Index))
                {
                    errors.Add($"Duplicate CSV column: {header.OriginalName}.");
                }
            }

            if (!headerIndexes.ContainsKey("Name"))
            {
                errors.Add("The CSV must include a Name column.");
            }

            if (errors.Count > 0)
            {
                return new CsvReadResult(rows, errors);
            }

            var rowNumber = 1;
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                rowNumber++;
                if (fields == null || fields.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                if (rows.Count >= MaxCatalogCsvRows)
                {
                    errors.Add($"The CSV cannot contain more than {MaxCatalogCsvRows} data rows.");
                    break;
                }

                if (fields.Length != headers.Length)
                {
                    errors.Add($"CSV row {rowNumber} has {fields.Length} values but {headers.Length} columns were expected.");
                    continue;
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in headers)
                {
                    values[header.Name] = fields[header.Index] ?? string.Empty;
                }

                rows.Add(new CsvDataRow(rowNumber, values));
            }
        }
        catch (MalformedLineException)
        {
            errors.Add("The CSV contains invalid quoting or a malformed row.");
        }
        catch (IOException)
        {
            errors.Add("The CSV could not be read. Check the file and try again.");
        }

        return new CsvReadResult(rows, errors);
    }

    private static bool TryDecodeCsvPayload(string payload, List<string> errors, out byte[] csvBytes)
    {
        csvBytes = Array.Empty<byte>();
        var maxEncodedLength = ((MaxCatalogCsvBytes + 2) / 3) * 4;
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > maxEncodedLength)
        {
            errors.Add("The CSV preview has expired or is invalid. Upload the file again.");
            return false;
        }

        try
        {
            csvBytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            errors.Add("The CSV preview is invalid. Upload the file again.");
            return false;
        }

        if (csvBytes.Length > MaxCatalogCsvBytes)
        {
            errors.Add("The CSV file must be 1 MB or smaller.");
            return false;
        }

        return true;
    }

    private static byte[] CreateCsvTemplate(IEnumerable<string> headers)
    {
        var content = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(string.Join(",", headers) + Environment.NewLine))
            .ToArray();
        return content;
    }

    private sealed record CsvDataRow(int RowNumber, Dictionary<string, string> Values);
    private sealed record CsvReadResult(List<CsvDataRow> Rows, List<string> Errors);
}
