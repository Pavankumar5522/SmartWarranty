using System.Security.Cryptography;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;

namespace SmartWarranty_2.Services;

public sealed class DocumentStorageService
{
    private static readonly string[] SupportedExtensions = { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };
    private readonly string _privateStoragePath;
    private readonly string _legacyStoragePath;

    public DocumentStorageService(IWebHostEnvironment environment)
    {
        _privateStoragePath = Path.Combine(environment.ContentRootPath, "App_Data", "uploads");
        _legacyStoragePath = Path.Combine(
            environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"),
            "uploads");
    }

    public string StoragePath => _privateStoragePath;

    public string GetFilePath(string storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName)
            || storedFileName is "." or ".."
            || !string.Equals(storedFileName, Path.GetFileName(storedFileName), StringComparison.Ordinal)
            || storedFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("The stored document filename is invalid.");
        }

        return Path.Combine(_privateStoragePath, storedFileName);
    }

    public string GetRegistrationDraftFilePath(int customerId, Guid draftId, string extension)
    {
        if (customerId <= 0 || draftId == Guid.Empty)
        {
            throw new ArgumentException("A valid customer and draft identifier are required.");
        }

        var normalizedExtension = extension.ToLowerInvariant();
        if (!SupportedExtensions.Contains(normalizedExtension, StringComparer.Ordinal))
        {
            throw new ArgumentException("The invoice file type is not supported.", nameof(extension));
        }

        var customerDraftPath = Path.Combine(
            _privateStoragePath,
            "pending",
            customerId.ToString(CultureInfo.InvariantCulture));
        return Path.Combine(customerDraftPath, draftId.ToString("N") + normalizedExtension);
    }

    public void DeleteRegistrationDraft(int customerId, Guid draftId)
    {
        if (customerId <= 0 || draftId == Guid.Empty)
        {
            return;
        }

        foreach (var extension in SupportedExtensions)
        {
            var path = GetRegistrationDraftFilePath(customerId, draftId, extension);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public void CleanupExpiredRegistrationDrafts(TimeSpan lifetime)
    {
        var pendingPath = Path.Combine(_privateStoragePath, "pending");
        if (!Directory.Exists(pendingPath))
        {
            return;
        }

        var expiration = DateTime.UtcNow - lifetime;
        foreach (var draftPath in Directory.EnumerateFiles(pendingPath, "*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(draftPath) < expiration)
            {
                File.Delete(draftPath);
            }
        }
    }

    public void MigrateLegacyFiles()
    {
        if (!Directory.Exists(_legacyStoragePath))
        {
            return;
        }

        Directory.CreateDirectory(_privateStoragePath);
        foreach (var legacyPath in Directory.EnumerateFiles(_legacyStoragePath))
        {
            var privatePath = GetFilePath(Path.GetFileName(legacyPath));
            if (File.Exists(privatePath))
            {
                if (!FilesMatch(legacyPath, privatePath))
                {
                    throw new IOException($"Conflicting document files were found for '{Path.GetFileName(legacyPath)}'.");
                }

                File.Delete(legacyPath);
                continue;
            }

            File.Move(legacyPath, privatePath);
        }
    }

    private static bool FilesMatch(string firstPath, string secondPath)
    {
        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        if (first.Length != second.Length)
        {
            return false;
        }

        return SHA256.HashData(first).SequenceEqual(SHA256.HashData(second));
    }
}
