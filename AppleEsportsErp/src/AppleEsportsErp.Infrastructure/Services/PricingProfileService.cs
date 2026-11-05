using AppleEsportsErp.Application.DTOs.Settings;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Infrastructure.Data;
using AppleEsportsErp.Application.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace AppleEsportsErp.Infrastructure.Services;

public class PricingProfileService : IPricingProfileService
{
    private readonly AppDbContext _db;
    private readonly IHubNotificationService _hubNotifier;

    public PricingProfileService(AppDbContext db, IHubNotificationService hubNotifier)
    {
        _db = db;
        _hubNotifier = hubNotifier;
    }

    public async Task<IEnumerable<PricingProfileDto>> GetAllByBranchAsync(Guid branchId)
    {
        var profiles = await _db.PricingProfiles
            .Include(p => p.Packages)
            .Where(p => p.BranchId == branchId && p.IsActive)
            .OrderBy(p => p.BaseHourlyRate)
            .ToListAsync();

        return profiles.Select(MapToDto);
    }

    private static PricingProfileDto MapToDto(PricingProfile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        BaseHourlyRate = p.BaseHourlyRate,
        BufferMinutes = p.BufferMinutes,
        BranchId = p.BranchId,
        IsActive = p.IsActive,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        RefreshRate = p.RefreshRate,
        SystemSpecs = p.SystemSpecs,
        Packages = p.Packages
            .Where(pkg => pkg.IsActive)
            .OrderBy(pkg => pkg.SortOrder)
            .ThenBy(pkg => pkg.DurationMinutes)
            .Select(MapPackageToDto)
            .ToList(),
        ShortSessionSlabs = p.ShortSessionSlabs?.Select(s => new ShortSessionSlabDto
        {
            FromMinutes = s.FromMinutes, ToMinutes = s.ToMinutes, Amount = s.Amount
        }).OrderBy(s => s.FromMinutes).ToList()
    };

    private static PricingPackageDto MapPackageToDto(PricingPackage pkg) => new()
    {
        Id = pkg.Id,
        PricingProfileId = pkg.PricingProfileId,
        Name = pkg.Name,
        DurationMinutes = pkg.DurationMinutes,
        Price = pkg.Price,
        SortOrder = pkg.SortOrder,
        IsActive = pkg.IsActive,
        PackagePriceFromMin = pkg.PackagePriceFromMin
    };

    /// <summary>"Package price from" has to sit inside the package: 4 hr from 3 hr 30 is fine,
    /// from 0 or from 5 hr is a typo that would quietly reprice every session.</summary>
    private static List<ShortSessionSlab>? MapSlabsFromDto(List<ShortSessionSlabDto>? slabs)
    {
        if (slabs == null || slabs.Count == 0) return null;
        return slabs.OrderBy(s => s.FromMinutes)
            .Select(s => new ShortSessionSlab { FromMinutes = s.FromMinutes, ToMinutes = s.ToMinutes, Amount = s.Amount })
            .ToList();
    }

    private static int? CheckFromMin(int? fromMin, int durationMinutes)
    {
        if (fromMin is { } m && (m <= 0 || m > durationMinutes))
            throw new AppException($"\"Package price from\" must be between 1 and {durationMinutes} minutes.");
        return fromMin;
    }

    public async Task<PricingProfileDto> CreateAsync(CreatePricingProfileDto dto)
    {
        var profile = new PricingProfile
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            BaseHourlyRate = dto.BaseHourlyRate,
            BufferMinutes = dto.BufferMinutes,
            BranchId = dto.BranchId,
            IsActive = dto.IsActive,
            RefreshRate = dto.RefreshRate,
            SystemSpecs = dto.SystemSpecs,
            ShortSessionSlabs = MapSlabsFromDto(dto.ShortSessionSlabs),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.PricingProfiles.Add(profile);
        await _db.SaveChangesAsync();
        await _hubNotifier.BroadcastPricingProfileUpdateAsync(profile.BranchId);

        return MapToDto(profile);
    }

    public async Task<PricingProfileDto> UpdateAsync(Guid id, UpdatePricingProfileDto dto)
    {
        var profile = await _db.PricingProfiles.FindAsync(id)
            ?? throw new NotFoundException("Pricing profile not found.");

        profile.Name = dto.Name;
        profile.BaseHourlyRate = dto.BaseHourlyRate;
        profile.BufferMinutes = dto.BufferMinutes;
        profile.IsActive = dto.IsActive;
        profile.RefreshRate = dto.RefreshRate;
        profile.SystemSpecs = dto.SystemSpecs;
        profile.ShortSessionSlabs = MapSlabsFromDto(dto.ShortSessionSlabs);
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        await _hubNotifier.BroadcastPricingProfileUpdateAsync(profile.BranchId);

        return MapToDto(profile);
    }

    public async Task DeleteAsync(Guid id)
    {
        var profile = await _db.PricingProfiles.FindAsync(id)
            ?? throw new NotFoundException("Pricing profile not found.");

        // Soft delete so historical session data isn't affected
        profile.IsActive = false;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        await _hubNotifier.BroadcastPricingProfileUpdateAsync(profile.BranchId);
    }

    public async Task<PricingPackageDto> CreatePackageAsync(CreatePricingPackageDto dto)
    {
        var profile = await _db.PricingProfiles.FindAsync(dto.PricingProfileId)
            ?? throw new NotFoundException("Pricing profile not found.");

        var package = new PricingPackage
        {
            Id = Guid.NewGuid(),
            PricingProfileId = dto.PricingProfileId,
            Name = dto.Name,
            DurationMinutes = dto.DurationMinutes,
            Price = dto.Price,
            SortOrder = dto.SortOrder,
            PackagePriceFromMin = CheckFromMin(dto.PackagePriceFromMin, dto.DurationMinutes),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.PricingPackages.Add(package);
        await _db.SaveChangesAsync();
        await _hubNotifier.BroadcastPricingProfileUpdateAsync(profile.BranchId);

        return MapPackageToDto(package);
    }

    public async Task<PricingPackageDto> UpdatePackageAsync(Guid id, UpdatePricingPackageDto dto)
    {
        var package = await _db.PricingPackages.FindAsync(id)
            ?? throw new NotFoundException("Pricing package not found.");
        var profile = await _db.PricingProfiles.FindAsync(package.PricingProfileId)
            ?? throw new NotFoundException("Pricing profile not found.");

        package.Name = dto.Name;
        package.DurationMinutes = dto.DurationMinutes;
        package.Price = dto.Price;
        package.SortOrder = dto.SortOrder;
        package.PackagePriceFromMin = CheckFromMin(dto.PackagePriceFromMin, dto.DurationMinutes);
        package.IsActive = dto.IsActive;
        package.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        await _hubNotifier.BroadcastPricingProfileUpdateAsync(profile.BranchId);

        return MapPackageToDto(package);
    }

    public async Task DeletePackageAsync(Guid id)
    {
        var package = await _db.PricingPackages.FindAsync(id)
            ?? throw new NotFoundException("Pricing package not found.");
        var profile = await _db.PricingProfiles.FindAsync(package.PricingProfileId)
            ?? throw new NotFoundException("Pricing profile not found.");

        // Soft delete, same as profiles - keeps historical bills referencing it intact
        package.IsActive = false;
        package.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        await _hubNotifier.BroadcastPricingProfileUpdateAsync(profile.BranchId);
    }
}
