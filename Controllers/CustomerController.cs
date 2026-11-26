using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartWarranty_2.Data;
using SmartWarranty_2.Models;
using SmartWarranty_2.Services;
using SmartWarranty_2.ViewModels;

namespace SmartWarranty_2.Controllers;

[Authorize(Roles = "Customer")]
[ServiceFilter(typeof(WarrantyExpiryNotificationFilter))]
public class CustomerController : Controller
{
    private readonly SmartWarrantyDbContext _context;
    private readonly DashboardService _dashboardService;
    private readonly IInvoiceRecognitionService _invoiceRecognitionService;
    private readonly DocumentStorageService _documentStorage;
    private readonly ILogger<CustomerController> _logger;

    public CustomerController(
        SmartWarrantyDbContext context,
        DashboardService dashboardService,
        IInvoiceRecognitionService invoiceRecognitionService,
        DocumentStorageService documentStorage,
        ILogger<CustomerController> logger)
    {
        _context = context;
        _dashboardService = dashboardService;
        _invoiceRecognitionService = invoiceRecognitionService;
        _documentStorage = documentStorage;
        _logger = logger;
    }

    private int CurrentCustomerId => int.Parse(User.FindFirstValue("UserId") ?? "0");

    public async Task<IActionResult> Dashboard()
    {
        var customer = await _context.Customers.FindAsync(CurrentCustomerId);
        if (customer == null) return RedirectToAction("Logout", "Account");

        var metrics = await _dashboardService.GetCustomerDashboardMetricsAsync(CurrentCustomerId);
        var recentProducts = await _context.Products
            .Include(p => p.Warranties)
            .Where(p => p.CustomerId == CurrentCustomerId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(5)
            .ToListAsync();
        var warrantyItems = await _context.Warranties.Include(w => w.Product).Where(w => w.Product.CustomerId == CurrentCustomerId).OrderBy(w => w.ExpiryDate).Take(5).ToListAsync();
        var recentRequests = await _context.ServiceRequests.Where(s => s.CustomerId == CurrentCustomerId).OrderByDescending(s => s.RequestDate).Take(5).ToListAsync();

        ViewBag.Metrics = metrics;
        ViewBag.RecentProducts = recentProducts;
        ViewBag.UpcomingWarranties = warrantyItems;
        ViewBag.RecentRequests = recentRequests;
        return View(customer);
    }

    public async Task<IActionResult> Profile()
    {
        var customer = await _context.Customers.FindAsync(CurrentCustomerId);
        return View(customer);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProfile(Customer customer)
    {
        var existing = await _context.Customers.FindAsync(CurrentCustomerId);
        if (existing == null) return NotFound();

        existing.FirstName = customer.FirstName;
        existing.LastName = customer.LastName;
        existing.Email = customer.Email;
        existing.PhoneNumber = customer.PhoneNumber;
        existing.Address = customer.Address;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Profile updated successfully.";
        return RedirectToAction(nameof(Profile));
    }

    public async Task<IActionResult> Products()
    {
        var products = await _context.Products
            .Include(p => p.Brand)
            .Include(p => p.Category)
            .Include(p => p.Warranties)
            .Where(p => p.CustomerId == CurrentCustomerId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
        return View(products);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteProduct(int id)
    {
        var product = await _context.Products
            .Include(p => p.Documents)
            .FirstOrDefaultAsync(p => p.Id == id && p.CustomerId == CurrentCustomerId);
        if (product == null)
        {
            return NotFound();
        }

        if (await _context.ServiceRequests.AnyAsync(s => s.ProductId == id))
        {
            TempData["ErrorMessage"] = "This product has service request history and cannot be deleted.";
            return RedirectToAction(nameof(Products));
        }

        var storedFileNames = product.Documents.Select(d => d.StoredFileName).ToList();
        var warrantyIds = await _context.Warranties
            .Where(w => w.ProductId == product.Id)
            .Select(w => w.Id)
            .ToArrayAsync();
        if (warrantyIds.Length > 0)
        {
            var warrantyNotifications = await _context.Notifications
                .Where(n => n.WarrantyId.HasValue && warrantyIds.Contains(n.WarrantyId.Value))
                .ToListAsync();
            _context.Notifications.RemoveRange(warrantyNotifications);
        }

        _context.Products.Remove(product);
        await _context.SaveChangesAsync();

        foreach (var storedFileName in storedFileNames)
        {
            var path = _documentStorage.GetFilePath(storedFileName);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }

        TempData["SuccessMessage"] = "Product and its warranties and documents were deleted.";
        return RedirectToAction(nameof(Products));
    }

    public async Task<IActionResult> RegisterProduct()
    {
        ViewBag.Brands = await _context.Brands.OrderBy(b => b.Name).ToListAsync();
        ViewBag.Categories = await _context.ProductCategories.OrderBy(c => c.Name).ToListAsync();
        return View(new ProductRegistrationViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReadRegistrationInvoice(IFormFile invoice)
    {
        ViewBag.Brands = await _context.Brands.OrderBy(b => b.Name).ToListAsync();
        ViewBag.Categories = await _context.ProductCategories.OrderBy(c => c.Name).ToListAsync();

        var model = new ProductRegistrationViewModel();
        if (invoice == null || invoice.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Choose an invoice file to read.");
            return View("RegisterProduct", model);
        }

        var extension = Path.GetExtension(invoice.FileName).ToLowerInvariant();
        var allowedExtensions = new[] { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };
        if (!allowedExtensions.Contains(extension))
        {
            ModelState.AddModelError(string.Empty, "Choose a PDF or supported image file.");
            return View("RegisterProduct", model);
        }

        if (invoice.Length > 5 * 1024 * 1024)
        {
            ModelState.AddModelError(string.Empty, "Invoice file size must be less than 5 MB.");
            return View("RegisterProduct", model);
        }

        var fileName = Path.GetFileName(invoice.FileName);
        if (fileName.Length > 200)
        {
            ModelState.AddModelError(string.Empty, "Invoice filename must be 200 characters or fewer.");
            return View("RegisterProduct", model);
        }

        InvoiceRecognitionResult recognition;
        try
        {
            await using var stream = invoice.OpenReadStream();
            recognition = await _invoiceRecognitionService.RecognizeAsync(stream, extension);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            _logger.LogWarning(exception, "Invoice recognition failed for customer {CustomerId}.", CurrentCustomerId);
            ModelState.AddModelError(string.Empty, "We couldn't read this invoice. Try another PDF/image or enter the details manually.");
            return View("RegisterProduct", model);
        }

        var draftId = Guid.NewGuid();
        var draftPath = _documentStorage.GetRegistrationDraftFilePath(CurrentCustomerId, draftId, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        var draftSaved = false;
        try
        {
            await using var destination = new FileStream(draftPath, FileMode.CreateNew);
            await invoice.CopyToAsync(destination);
            draftSaved = true;
        }
        finally
        {
            if (!draftSaved && System.IO.File.Exists(draftPath))
            {
                System.IO.File.Delete(draftPath);
            }
        }

        var matchedBrand = string.IsNullOrWhiteSpace(recognition.BrandName)
            ? null
            : ((IEnumerable<Brand>)ViewBag.Brands).FirstOrDefault(brand =>
                string.Equals(brand.Name, recognition.BrandName.Trim(), StringComparison.OrdinalIgnoreCase));
        var matchedCategory = string.IsNullOrWhiteSpace(recognition.CategoryName)
            ? null
            : ((IEnumerable<ProductCategory>)ViewBag.Categories).FirstOrDefault(category =>
                string.Equals(category.Name, recognition.CategoryName.Trim(), StringComparison.OrdinalIgnoreCase));
        model.Name = recognition.ProductName ?? string.Empty;
        model.BrandId = matchedBrand?.Id;
        model.BrandName = matchedBrand == null ? recognition.BrandName : null;
        model.CategoryId = matchedCategory?.Id ?? 0;
        model.ModelNumber = recognition.ModelNumber ?? string.Empty;
        model.SerialNumber = recognition.SerialNumber ?? string.Empty;
        model.PurchaseDate = recognition.PurchaseDate ?? DateTime.UtcNow.Date;
        model.SellerName = recognition.SellerName ?? string.Empty;
        model.ManufacturerWarrantyStatus = WarrantyInformationStatus.NeedsConfirmation;
        model.WarrantyStartDate = recognition.PurchaseDate;
        model.WarrantyDurationMonths = recognition.WarrantyDurationMonths;
        model.InvoiceDraftId = draftId;
        model.InvoiceDraftExtension = extension;
        model.InvoiceDraftFileName = fileName;
        model.InvoiceTextFound = recognition.HasRecognizedText;
        model.InvoiceFieldsFound = recognition.HasRecognizedFields;

        return View("RegisterProduct", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterProduct(ProductRegistrationViewModel model)
    {
        ViewBag.Brands = await _context.Brands.OrderBy(b => b.Name).ToListAsync();
        ViewBag.Categories = await _context.ProductCategories.OrderBy(c => c.Name).ToListAsync();
        string? invoiceDraftPath = null;
        var invoiceFileName = string.Empty;
        var invoiceExtension = string.Empty;

        if (model.InvoiceDraftId.HasValue)
        {
            invoiceExtension = model.InvoiceDraftExtension?.ToLowerInvariant() ?? string.Empty;
            var allowedExtensions = new[] { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };
            invoiceFileName = Path.GetFileName(model.InvoiceDraftFileName ?? string.Empty);
            if (model.InvoiceDraftId.Value == Guid.Empty
                || !allowedExtensions.Contains(invoiceExtension)
                || string.IsNullOrWhiteSpace(invoiceFileName)
                || invoiceFileName.Length > 200)
            {
                ModelState.AddModelError(string.Empty, "The invoice draft is invalid. Upload the invoice again.");
            }
            else
            {
                invoiceDraftPath = _documentStorage.GetRegistrationDraftFilePath(
                    CurrentCustomerId,
                    model.InvoiceDraftId.Value,
                    invoiceExtension);
                if (!System.IO.File.Exists(invoiceDraftPath))
                {
                    ModelState.AddModelError(string.Empty, "The invoice draft has expired. Upload the invoice again.");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(model.InvoiceDraftExtension)
            || !string.IsNullOrWhiteSpace(model.InvoiceDraftFileName))
        {
            ModelState.AddModelError(string.Empty, "The invoice draft is invalid. Upload the invoice again.");
        }

        if (model.ManufacturerWarrantyStatus.HasValue
            && !Enum.IsDefined(typeof(WarrantyInformationStatus), model.ManufacturerWarrantyStatus.Value))
        {
            ModelState.AddModelError(nameof(model.ManufacturerWarrantyStatus), "Choose a valid manufacturer warranty situation.");
        }

        if (model.ManufacturerWarrantyStatus == WarrantyInformationStatus.Confirmed)
        {
            if (!model.WarrantyStartDate.HasValue)
            {
                ModelState.AddModelError(nameof(model.WarrantyStartDate), "Enter the warranty start date.");
            }
            else if (model.WarrantyStartDate.Value.Date > DateTime.UtcNow.Date)
            {
                ModelState.AddModelError(nameof(model.WarrantyStartDate), "Warranty start date cannot be in the future.");
            }

            if (!model.WarrantyDurationMonths.HasValue)
            {
                ModelState.AddModelError(nameof(model.WarrantyDurationMonths), "Enter the warranty duration.");
            }

            if (string.IsNullOrWhiteSpace(model.WarrantyType))
            {
                ModelState.AddModelError(nameof(model.WarrantyType), "Enter the warranty type.");
            }
        }
        else
        {
            ModelState.Remove(nameof(model.WarrantyStartDate));
            ModelState.Remove(nameof(model.WarrantyDurationMonths));
            ModelState.Remove(nameof(model.WarrantyType));
        }

        var existingBrand = model.BrandId.HasValue
            ? await _context.Brands.FindAsync(model.BrandId.Value)
            : null;
        if (model.BrandId.HasValue)
        {
            if (existingBrand == null)
            {
                ModelState.AddModelError(nameof(model.BrandId), "Select a valid brand.");
            }
        }
        else if (string.IsNullOrWhiteSpace(model.BrandName))
        {
            ModelState.AddModelError(nameof(model.BrandName), "Select a brand or enter its name.");
        }

        if (model.PurchaseDate.Date > DateTime.UtcNow.Date)
        {
            ModelState.AddModelError(nameof(model.PurchaseDate), "Purchase date cannot be in the future.");
        }

        var categoryExists = await _context.ProductCategories.AnyAsync(c => c.Id == model.CategoryId);
        if (!categoryExists)
        {
            ModelState.AddModelError(nameof(model.CategoryId), "Select a valid category.");
        }

        var serialNumber = model.SerialNumber?.Trim() ?? string.Empty;
        if (await _context.Products.AnyAsync(p => p.SerialNumber == serialNumber))
        {
            ModelState.AddModelError(nameof(model.SerialNumber), "A product with this serial number is already registered.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        Brand brand;
        if (existingBrand != null)
        {
            brand = existingBrand;
        }
        else
        {
            var brandName = model.BrandName!.Trim();
            brand = await _context.Brands.FirstOrDefaultAsync(b => b.Name == brandName)
                ?? new Brand { Name = brandName };
            if (brand.Id == 0)
            {
                _context.Brands.Add(brand);
            }
        }

        var product = new Product
        {
            ProductCode = "PRD-" + Guid.NewGuid().ToString()[..8].ToUpper(),
            Name = model.Name.Trim(),
            Brand = brand,
            BrandId = brand.Id,
            CategoryId = model.CategoryId,
            ModelNumber = model.ModelNumber.Trim(),
            SerialNumber = serialNumber,
            PurchaseDate = model.PurchaseDate,
            SellerName = model.SellerName.Trim(),
            ManufacturerWarrantyStatus = model.ManufacturerWarrantyStatus!.Value,
            CustomerId = CurrentCustomerId,
            CreatedAt = DateTime.UtcNow
        };

        if (model.ManufacturerWarrantyStatus == WarrantyInformationStatus.Confirmed)
        {
            var warrantyStartDate = model.WarrantyStartDate!.Value.Date;
            product.Warranties.Add(new Warranty
            {
                CoverageCategory = WarrantyCoverageCategory.Manufacturer,
                StartDate = warrantyStartDate,
                DurationMonths = model.WarrantyDurationMonths!.Value,
                ExpiryDate = warrantyStartDate.AddMonths(model.WarrantyDurationMonths.Value),
                WarrantyType = model.WarrantyType!.Trim(),
                CreatedAt = DateTime.UtcNow
            });
        }

        Warranty? manufacturerWarranty = null;
        if (model.ManufacturerWarrantyStatus == WarrantyInformationStatus.Confirmed)
        {
            manufacturerWarranty = product.Warranties.Single();
        }

        string? storedInvoicePath = null;
        var registrationSaved = false;
        try
        {
            if (invoiceDraftPath != null && model.InvoiceDraftId.HasValue)
            {
                Directory.CreateDirectory(_documentStorage.StoragePath);
                var storedFileName = Guid.NewGuid() + invoiceExtension;
                storedInvoicePath = _documentStorage.GetFilePath(storedFileName);
                System.IO.File.Copy(invoiceDraftPath, storedInvoicePath);
                var invoiceSize = new FileInfo(storedInvoicePath).Length;

                _context.ProductDocuments.Add(new ProductDocument
                {
                    Product = product,
                    Warranty = manufacturerWarranty,
                    DocumentType = "Purchase Invoice",
                    FileName = invoiceFileName,
                    StoredFileName = storedFileName,
                    ContentType = GetDocumentContentType(storedFileName),
                    FileSize = invoiceSize,
                    UploadedAt = DateTime.UtcNow
                });
            }

            _context.Products.Add(product);
            await _context.SaveChangesAsync();
            registrationSaved = true;
        }
        finally
        {
            if (!registrationSaved && storedInvoicePath != null && System.IO.File.Exists(storedInvoicePath))
            {
                System.IO.File.Delete(storedInvoicePath);
            }
        }

        if (registrationSaved && model.InvoiceDraftId.HasValue)
        {
            try
            {
                _documentStorage.DeleteRegistrationDraft(CurrentCustomerId, model.InvoiceDraftId.Value);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Could not remove a completed invoice draft for customer {CustomerId}.", CurrentCustomerId);
            }
        }

        TempData["SuccessMessage"] = model.ManufacturerWarrantyStatus == WarrantyInformationStatus.Confirmed
            ? invoiceDraftPath == null
                ? "Product and manufacturer warranty registered successfully."
                : "Product and manufacturer warranty registered. The invoice is saved in your documents."
            : model.ManufacturerWarrantyStatus == WarrantyInformationStatus.NoWarranty
                ? invoiceDraftPath == null
                    ? "Product registered. No manufacturer warranty was recorded."
                    : "Product registered with no manufacturer warranty. The invoice is saved in your documents."
                : invoiceDraftPath == null
                    ? "Product registered. You can add the manufacturer warranty details later."
                    : "Product registered. You can add the manufacturer warranty details later; the invoice is saved in your documents.";
        return RedirectToAction(nameof(Products));
    }

    public async Task<IActionResult> Warranties()
    {
        var warranties = await _context.Warranties
            .Include(w => w.Product)
            .ThenInclude(p => p.Brand)
            .Include(w => w.Documents)
            .Where(w => w.Product.CustomerId == CurrentCustomerId)
            .OrderByDescending(w => w.ExpiryDate)
            .ToListAsync();

        foreach (var item in warranties)
        {
            item.Product = item.Product ?? new Product();
        }

        return View(warranties);
    }

    public async Task<IActionResult> AddWarranty(int? productId = null, WarrantyCoverageCategory? coverageCategory = null)
    {
        var products = await _context.Products
            .Include(p => p.Warranties)
            .Where(p => p.CustomerId == CurrentCustomerId)
            .OrderBy(p => p.Name)
            .ToListAsync();
        ViewBag.Products = products;
        var selectedProductId = productId.HasValue && products.Any(p => p.Id == productId.Value)
            ? productId.Value
            : products.FirstOrDefault()?.Id ?? 0;
        var selectedCategory = coverageCategory.HasValue
            && Enum.IsDefined(typeof(WarrantyCoverageCategory), coverageCategory.Value)
                ? coverageCategory
                : WarrantyCoverageCategory.Manufacturer;
        return View(new WarrantyRegistrationViewModel
        {
            ProductId = selectedProductId,
            CoverageCategory = selectedCategory
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddWarranty(WarrantyRegistrationViewModel model)
    {
        var products = await _context.Products
            .Include(p => p.Warranties)
            .Where(p => p.CustomerId == CurrentCustomerId)
            .OrderBy(p => p.Name)
            .ToListAsync();
        ViewBag.Products = products;

        var product = products.FirstOrDefault(p => p.Id == model.ProductId);
        if (product == null)
        {
            ModelState.AddModelError(nameof(model.ProductId), "Select one of your registered products.");
        }

        if (model.CoverageCategory.HasValue
            && !Enum.IsDefined(typeof(WarrantyCoverageCategory), model.CoverageCategory.Value))
        {
            ModelState.AddModelError(nameof(model.CoverageCategory), "Choose a valid coverage category.");
        }

        if (model.CoverageCategory == WarrantyCoverageCategory.Manufacturer
            && product?.Warranties.Any(w => w.CoverageCategory == WarrantyCoverageCategory.Manufacturer) == true)
        {
            ModelState.AddModelError(nameof(model.CoverageCategory), "This product already has a manufacturer warranty. Add extended coverage instead.");
        }

        if (model.StartDate.Date > DateTime.UtcNow.Date)
        {
            ModelState.AddModelError(nameof(model.StartDate), "Warranty start date cannot be in the future.");
        }

        IFormFile? invoice = null;
        string? invoiceExtension = null;
        string? invoiceFileName = null;
        if (model.CoverageCategory == WarrantyCoverageCategory.Extended)
        {
            invoice = model.ExtendedWarrantyInvoice;
            if (invoice == null || invoice.Length == 0)
            {
                ModelState.AddModelError(nameof(model.ExtendedWarrantyInvoice), "Upload the invoice for this extended warranty.");
            }
            else
            {
                invoiceExtension = Path.GetExtension(invoice.FileName).ToLowerInvariant();
                var allowedExtensions = new[] { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };
                if (!allowedExtensions.Contains(invoiceExtension))
                {
                    ModelState.AddModelError(nameof(model.ExtendedWarrantyInvoice), "Invoice must be a PDF or supported image file.");
                }

                if (invoice.Length > 5 * 1024 * 1024)
                {
                    ModelState.AddModelError(nameof(model.ExtendedWarrantyInvoice), "Invoice file size must be less than 5 MB.");
                }

                invoiceFileName = Path.GetFileName(invoice.FileName);
                if (invoiceFileName.Length > 200)
                {
                    ModelState.AddModelError(nameof(model.ExtendedWarrantyInvoice), "Invoice filename must be 200 characters or fewer.");
                }
            }
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var expiryDate = model.StartDate.Date.AddMonths(model.DurationMonths);
        var warranty = new Warranty
        {
            Product = product!,
            CoverageCategory = model.CoverageCategory!.Value,
            StartDate = model.StartDate.Date,
            DurationMonths = model.DurationMonths,
            ExpiryDate = expiryDate,
            WarrantyType = model.WarrantyType.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        string? invoicePath = null;
        var saveSucceeded = false;
        try
        {
            if (invoice != null && invoiceExtension != null && invoiceFileName != null)
            {
                Directory.CreateDirectory(_documentStorage.StoragePath);
                var storedFileName = Guid.NewGuid() + invoiceExtension;
                invoicePath = _documentStorage.GetFilePath(storedFileName);
                await using (var stream = new FileStream(invoicePath, FileMode.CreateNew))
                {
                    await invoice.CopyToAsync(stream);
                }

                _context.ProductDocuments.Add(new ProductDocument
                {
                    Product = product!,
                    Warranty = warranty,
                    DocumentType = "Extended Warranty Invoice",
                    FileName = invoiceFileName,
                    StoredFileName = storedFileName,
                    ContentType = GetDocumentContentType(storedFileName),
                    FileSize = invoice.Length,
                    UploadedAt = DateTime.UtcNow
                });
            }

            if (warranty.CoverageCategory == WarrantyCoverageCategory.Manufacturer)
            {
                product!.ManufacturerWarrantyStatus = WarrantyInformationStatus.Confirmed;
            }

            _context.Warranties.Add(warranty);
            await _context.SaveChangesAsync();
            saveSucceeded = true;
        }
        finally
        {
            if (!saveSucceeded && invoicePath != null && System.IO.File.Exists(invoicePath))
            {
                System.IO.File.Delete(invoicePath);
            }
        }

        TempData["SuccessMessage"] = warranty.CoverageCategory == WarrantyCoverageCategory.Extended
            ? "Extended warranty and its invoice were saved."
            : "Manufacturer warranty added successfully.";
        return RedirectToAction(nameof(Warranties));
    }

    public async Task<IActionResult> Documents()
    {
        var docs = await _context.ProductDocuments
            .Include(d => d.Product)
            .Where(d => d.Product.CustomerId == CurrentCustomerId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();
        return View(docs);
    }

    public async Task<IActionResult> UploadDocument(int productId)
    {
        var product = await _context.Products.FirstOrDefaultAsync(p => p.Id == productId && p.CustomerId == CurrentCustomerId);
        if (product == null) return Forbid();
        ViewBag.Product = product;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadDocument(int productId, IFormFile file, string documentType)
    {
        var product = await _context.Products.FirstOrDefaultAsync(p => p.Id == productId && p.CustomerId == CurrentCustomerId);
        if (product == null) return Forbid();

        if (file == null || file.Length == 0)
        {
            TempData["ErrorMessage"] = "Please select a file.";
            return RedirectToAction(nameof(Documents));
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowedExtensions = new[] { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };
        if (!allowedExtensions.Contains(extension))
        {
            TempData["ErrorMessage"] = "Unsupported file type.";
            return RedirectToAction(nameof(Documents));
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            TempData["ErrorMessage"] = "File size must be less than 5 MB.";
            return RedirectToAction(nameof(Documents));
        }

        InvoiceRecognitionResult? recognition = null;
        if (documentType == "Purchase Invoice")
        {
            await using var invoiceStream = file.OpenReadStream();
            recognition = await _invoiceRecognitionService.RecognizeAsync(invoiceStream, extension);
        }

        Directory.CreateDirectory(_documentStorage.StoragePath);

        var storedName = Guid.NewGuid() + extension;
        var fullPath = _documentStorage.GetFilePath(storedName);
        await using (var stream = new FileStream(fullPath, FileMode.CreateNew))
        {
            await file.CopyToAsync(stream);
        }

        _context.ProductDocuments.Add(new ProductDocument
        {
            ProductId = productId,
            DocumentType = documentType,
            FileName = file.FileName,
            StoredFileName = storedName,
            ContentType = file.ContentType,
            FileSize = file.Length,
            UploadedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        if (recognition != null)
        {
            TempData["InvoiceReview"] = JsonSerializer.Serialize(new InvoiceReviewViewModel
            {
                ProductId = product.Id,
                ProductName = recognition.ProductName ?? product.Name,
                ModelNumber = recognition.ModelNumber ?? product.ModelNumber,
                SerialNumber = recognition.SerialNumber ?? product.SerialNumber,
                PurchaseDate = recognition.PurchaseDate ?? product.PurchaseDate,
                SellerName = recognition.SellerName ?? product.SellerName,
                WarrantyDurationMonths = recognition.WarrantyDurationMonths,
                WarrantyStartDate = recognition.PurchaseDate ?? product.PurchaseDate,
                ExtractedTextFound = recognition.HasRecognizedText
            });
            return RedirectToAction(nameof(ReviewInvoice));
        }

        TempData["SuccessMessage"] = "Document uploaded successfully.";
        return RedirectToAction(nameof(Documents));
    }

    public IActionResult ReviewInvoice()
    {
        var serializedReview = TempData["InvoiceReview"] as string;
        if (string.IsNullOrEmpty(serializedReview))
        {
            return RedirectToAction(nameof(Products));
        }

        var model = JsonSerializer.Deserialize<InvoiceReviewViewModel>(serializedReview);
        if (model == null)
        {
            return RedirectToAction(nameof(Products));
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewInvoice(InvoiceReviewViewModel model)
    {
        var product = await _context.Products
            .FirstOrDefaultAsync(p => p.Id == model.ProductId && p.CustomerId == CurrentCustomerId);
        if (product == null)
        {
            return NotFound();
        }

        if (model.PurchaseDate.Date > DateTime.UtcNow.Date)
        {
            ModelState.AddModelError(nameof(model.PurchaseDate), "Purchase date cannot be in the future.");
        }

        if (await _context.Products.AnyAsync(p =>
                p.Id != model.ProductId &&
                p.SerialNumber == model.SerialNumber.Trim()))
        {
            ModelState.AddModelError(nameof(model.SerialNumber), "A product with this serial number is already registered.");
        }

        var manufacturerWarrantyExists = await _context.Warranties.AnyAsync(w =>
            w.ProductId == product.Id
            && w.CoverageCategory == WarrantyCoverageCategory.Manufacturer);
        if (model.WarrantyDurationMonths.HasValue && manufacturerWarrantyExists)
        {
            ModelState.AddModelError(
                nameof(model.WarrantyDurationMonths),
                "A manufacturer warranty is already recorded. Use Add extended warranty for additional coverage.");
        }

        if (model.WarrantyDurationMonths.HasValue)
        {
            var warrantyStartDate = model.WarrantyStartDate?.Date ?? model.PurchaseDate.Date;
            if (warrantyStartDate > DateTime.UtcNow.Date)
            {
                ModelState.AddModelError(nameof(model.WarrantyStartDate), "Warranty start date cannot be in the future.");
            }

            if (string.IsNullOrWhiteSpace(model.WarrantyType))
            {
                ModelState.AddModelError(nameof(model.WarrantyType), "Enter the warranty type.");
            }
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        product.Name = model.ProductName.Trim();
        product.ModelNumber = model.ModelNumber.Trim();
        product.SerialNumber = model.SerialNumber.Trim();
        product.PurchaseDate = model.PurchaseDate.Date;
        product.SellerName = model.SellerName.Trim();

        if (model.WarrantyDurationMonths.HasValue)
        {
            var startDate = model.WarrantyStartDate?.Date ?? model.PurchaseDate.Date;
            product.ManufacturerWarrantyStatus = WarrantyInformationStatus.Confirmed;
            _context.Warranties.Add(new Warranty
            {
                ProductId = product.Id,
                CoverageCategory = WarrantyCoverageCategory.Manufacturer,
                StartDate = startDate,
                DurationMonths = model.WarrantyDurationMonths.Value,
                ExpiryDate = startDate.AddMonths(model.WarrantyDurationMonths.Value),
                WarrantyType = model.WarrantyType,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = model.WarrantyDurationMonths.HasValue
            ? "Invoice details saved and warranty added."
            : "Invoice details saved.";
        return RedirectToAction(nameof(Products));
    }

    public async Task<IActionResult> DownloadDocument(int id)
    {
        var document = await _context.ProductDocuments.Include(d => d.Product).FirstOrDefaultAsync(d => d.Id == id);
        if (document == null || document.Product.CustomerId != CurrentCustomerId)
        {
            return Forbid();
        }

        var path = _documentStorage.GetFilePath(document.StoredFileName);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, GetDocumentContentType(document.StoredFileName), document.FileName);
    }

    public async Task<IActionResult> ViewDocument(int id)
    {
        var document = await _context.ProductDocuments
            .Include(d => d.Product)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (document == null || document.Product.CustomerId != CurrentCustomerId)
        {
            return Forbid();
        }

        var contentType = GetPreviewContentType(document.StoredFileName);
        if (contentType == null)
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var path = _documentStorage.GetFilePath(document.StoredFileName);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers["Content-Disposition"] = "inline";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "private, no-store";
        return PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    public async Task<IActionResult> DeleteDocument(int id)
    {
        var document = await _context.ProductDocuments.Include(d => d.Product).FirstOrDefaultAsync(d => d.Id == id);
        if (document == null || document.Product.CustomerId != CurrentCustomerId)
        {
            return Forbid();
        }

        var path = _documentStorage.GetFilePath(document.StoredFileName);
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        _context.ProductDocuments.Remove(document);
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Document deleted.";
        return RedirectToAction(nameof(Documents));
    }

    private static string GetDocumentContentType(string storedFileName) =>
        GetPreviewContentType(storedFileName) ?? "application/octet-stream";

    private static string? GetPreviewContentType(string storedFileName) =>
        Path.GetExtension(storedFileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null
        };

    public async Task<IActionResult> Eligibility()
    {
        var products = await _context.Products
            .Include(p => p.Brand)
            .Include(p => p.Warranties)
            .Where(p => p.CustomerId == CurrentCustomerId)
            .ToListAsync();
        return View(products);
    }

    public async Task<IActionResult> ManufacturerSupport(int? brandId)
    {
        var brands = await _context.Brands
            .Where(b => b.WarrantyClaimUrl != string.Empty
                || b.OfficialWebsiteUrl != string.Empty
                || b.SupportPhoneNumber != string.Empty
                || b.SupportEmailAddress != string.Empty)
            .OrderBy(b => b.Name)
            .ToListAsync();
        ViewBag.Brands = brands;

        var selectedBrand = brandId.HasValue
            ? brands.FirstOrDefault(b => b.Id == brandId.Value)
            : null;
        return View(selectedBrand);
    }

    public async Task<IActionResult> ServiceRequests()
    {
        var requests = await _context.ServiceRequests
            .Include(s => s.Product).ThenInclude(p => p.Brand)
            .Include(s => s.Histories)
            .Where(s => s.CustomerId == CurrentCustomerId)
            .OrderByDescending(s => s.RequestDate)
            .ToListAsync();
        return View(requests);
    }

    public async Task<IActionResult> CreateServiceRequest(int? productId = null)
    {
        var products = await _context.Products.Include(p => p.Brand).Where(p => p.CustomerId == CurrentCustomerId).ToListAsync();
        ViewBag.Products = products;
        var selectedProductId = productId.HasValue && products.Any(p => p.Id == productId.Value)
            ? productId.Value
            : products.FirstOrDefault()?.Id ?? 0;
        return View(new ServiceRequestViewModel { ProductId = selectedProductId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateServiceRequest(ServiceRequestViewModel model)
    {
        var products = await _context.Products.Include(p => p.Brand).Where(p => p.CustomerId == CurrentCustomerId).ToListAsync();
        ViewBag.Products = products;

        if (!products.Any(p => p.Id == model.ProductId))
        {
            ModelState.AddModelError(nameof(model.ProductId), "Select one of your registered products.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var request = new ServiceRequest
        {
            ServiceRequestCode = "SR-" + Guid.NewGuid().ToString()[..8].ToUpper(),
            CustomerId = CurrentCustomerId,
            ProductId = model.ProductId,
            IssueDescription = model.IssueDescription,
            AdditionalInformation = model.AdditionalInformation,
            ClaimReferenceNumber = model.ClaimReferenceNumber,
            RequestDate = DateTime.UtcNow,
            Status = "Submitted",
            TrackingInfo = "Submitted and awaiting manufacturer follow-up.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.ServiceRequests.Add(request);
        await _context.SaveChangesAsync();

        _context.ServiceHistories.Add(new ServiceHistory
        {
            ServiceRequestId = request.Id,
            Status = "Submitted",
            Notes = "Service request submitted.",
            ChangedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "Service request submitted.";
        return RedirectToAction(nameof(ServiceRequests));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateClaimReference(int id, string claimReferenceNumber)
    {
        var request = await _context.ServiceRequests.FirstOrDefaultAsync(s => s.Id == id && s.CustomerId == CurrentCustomerId);
        if (request == null)
        {
            return Forbid();
        }

        request.ClaimReferenceNumber = claimReferenceNumber;
        request.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        TempData["SuccessMessage"] = "Claim reference updated.";
        return RedirectToAction(nameof(ServiceRequests));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWarrantyClaimStatus(int id, string? warrantyClaimed, string? returnUrl = null)
    {
        bool? claimed;
        if (warrantyClaimed == "true")
        {
            claimed = true;
        }
        else if (warrantyClaimed == "false")
        {
            claimed = false;
        }
        else if (string.IsNullOrEmpty(warrantyClaimed))
        {
            claimed = null;
        }
        else
        {
            return BadRequest();
        }

        var request = await _context.ServiceRequests
            .FirstOrDefaultAsync(s => s.Id == id && s.CustomerId == CurrentCustomerId);
        if (request == null)
        {
            return NotFound();
        }

        if (request.WarrantyClaimed != claimed)
        {
            request.WarrantyClaimed = claimed;
            request.UpdatedAt = DateTime.UtcNow;
            _context.ServiceHistories.Add(new ServiceHistory
            {
                ServiceRequestId = request.Id,
                Status = request.Status,
                Notes = $"Warranty claim status changed to {(claimed == true ? "Claimed" : claimed == false ? "Not claimed" : "Not specified")}.",
                ChangedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = "Warranty claim status updated.";
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(ServiceRequests));
    }

    public async Task<IActionResult> ServiceHistory()
    {
        var histories = await _context.ServiceRequests
            .Include(s => s.Product).ThenInclude(p => p.Brand)
            .Include(s => s.Histories)
            .Where(s => s.CustomerId == CurrentCustomerId)
            .OrderByDescending(s => s.RequestDate)
            .ToListAsync();

        return View(histories);
    }

    public async Task<IActionResult> Notifications()
    {
        var notifications = await _context.Notifications
            .Where(n => n.CustomerId == CurrentCustomerId)
            .OrderBy(n => n.IsRead)
            .ThenByDescending(n => n.CreatedAt)
            .ToListAsync();
        return View(notifications);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DismissNotification(int id, string? returnUrl = null)
    {
        var notification = await _context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.CustomerId == CurrentCustomerId);
        if (notification == null)
        {
            return NotFound();
        }

        notification.IsRead = true;
        await _context.SaveChangesAsync();

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Notifications));
    }
}
