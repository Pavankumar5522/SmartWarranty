using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartWarranty_2.Models;

public enum WarrantyCoverageCategory
{
    Manufacturer,
    Extended
}

public class Warranty
{
    [Key]
    public int Id { get; set; }

    [ForeignKey(nameof(Product))]
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public WarrantyCoverageCategory CoverageCategory { get; set; } = WarrantyCoverageCategory.Manufacturer;

    [Required]
    public DateTime StartDate { get; set; }

    [Range(1, 240)]
    public int DurationMonths { get; set; }

    [Required]
    public DateTime ExpiryDate { get; set; }

    [Required, StringLength(80)]
    public string WarrantyType { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ProductDocument> Documents { get; set; } = new List<ProductDocument>();
}
