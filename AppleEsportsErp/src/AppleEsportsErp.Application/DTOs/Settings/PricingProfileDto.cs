using System.Collections.Generic;

namespace AppleEsportsErp.Application.DTOs.Settings;

public class PricingProfileDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public decimal BaseHourlyRate { get; set; }
    public int BufferMinutes { get; set; }
    public Guid BranchId { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? RefreshRate { get; set; }
    public string? SystemSpecs { get; set; }

    /// <summary>Custom fixed-duration packages for this profile. Empty means none configured -
    /// the customer-facing plan list falls back to the existing auto 1/2/3-hour multiples.</summary>
    public List<PricingPackageDto> Packages { get; set; } = new();
    public List<ShortSessionSlabDto>? ShortSessionSlabs { get; set; }
}

public class ShortSessionSlabDto
{
    public int FromMinutes { get; set; }
    public int ToMinutes { get; set; }
    public decimal Amount { get; set; }
}

public class PricingPackageDto
{
    public Guid Id { get; set; }
    public Guid PricingProfileId { get; set; }
    public string Name { get; set; } = null!;
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public int? PackagePriceFromMin { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class CreatePricingPackageDto
{
    public Guid PricingProfileId { get; set; }
    public string Name { get; set; } = null!;
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public int? PackagePriceFromMin { get; set; }
    public int SortOrder { get; set; }
}

public class UpdatePricingPackageDto
{
    public string Name { get; set; } = null!;
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public int? PackagePriceFromMin { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CreatePricingProfileDto
{
    public string Name { get; set; } = null!;
    public decimal BaseHourlyRate { get; set; }
    public int BufferMinutes { get; set; } = 10;
    public Guid BranchId { get; set; }
    public bool IsActive { get; set; } = true;
    public string? RefreshRate { get; set; }
    public string? SystemSpecs { get; set; }
    public List<ShortSessionSlabDto>? ShortSessionSlabs { get; set; }
}

public class UpdatePricingProfileDto
{
    public string Name { get; set; } = null!;
    public decimal BaseHourlyRate { get; set; }
    public int BufferMinutes { get; set; } = 10;
    public bool IsActive { get; set; }
    public string? RefreshRate { get; set; }
    public string? SystemSpecs { get; set; }
    public List<ShortSessionSlabDto>? ShortSessionSlabs { get; set; }
}
