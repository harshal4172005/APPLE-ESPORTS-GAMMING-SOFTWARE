namespace AppleEsportsErp.Domain.Entities;

/// <summary>SOP PC Management: Dynamic pricing architecture</summary>
public class PricingProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public decimal BaseHourlyRate { get; set; }
    public int BufferMinutes { get; set; } = 10;
    public Guid BranchId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string? RefreshRate { get; set; }
    public string? SystemSpecs { get; set; }
    
    // Navigation
    public Branch Branch { get; set; } = null!;
    public ICollection<Pc> Pcs { get; set; } = new List<Pc>();

    /// <summary>Custom fixed-duration packages (e.g. "30 min - Rs 30") for this profile. Empty
    /// means no custom packages have been set up here - the customer-facing plan list falls
    /// back to the existing auto 1/2/3-hour multiples of BaseHourlyRate, exactly as before.</summary>
    public ICollection<PricingPackage> Packages { get; set; } = new List<PricingPackage>();
}

/// <summary>A specific duration sold at a specific price, rather than derived by multiplying
/// BaseHourlyRate - "4 hours for Rs 180" is a bulk-discount deal, not 4x an hourly rate, and
/// "30 minutes for Rs 30" is a duration BaseHourlyRate alone can't price at all.</summary>
public class PricingPackage
{
    public Guid Id { get; set; }
    public Guid PricingProfileId { get; set; }
    public string Name { get; set; } = null!;
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Played this many minutes or more, the customer pays the package price; less, the hourly
    /// rate. Owner's rule (25 Oct 2026): the 4 hr / Rs 200 package is Rs 200 from 3 hr 30 min
    /// (210). Null keeps the old rule - package price from (duration - buffer). Settings only,
    /// never shown to customers.
    /// </summary>
    public int? PackagePriceFromMin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public PricingProfile PricingProfile { get; set; } = null!;
}
