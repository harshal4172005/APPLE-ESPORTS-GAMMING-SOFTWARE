using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Security.Claims;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Wallets;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Api.Controllers;

/// <summary>Gaming wallet top-up rules — minimum amount and default bonus %.
/// Reading is open to any authenticated staff (they need to know the current rules to process a top-up);
/// editing is restricted to Super Admin (or an Admin explicitly granted the "wallet_settings" permission).</summary>
[ApiController]
[Route("api/wallet-settings")]
[Authorize]
public class WalletSettingsController : ControllerBase
{
    private const string ConfigKey = SystemConfigKeys.WalletTopUpRules;
    private const decimal DefaultMinGamingTopUp = 500m;
    private const decimal DefaultBonusPercent = 0m;

    private readonly AppDbContext _db;

    public WalletSettingsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetRules()
    {
        var config = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == ConfigKey);
        var rules = config != null
            ? JsonSerializer.Deserialize<WalletTopUpRulesDto>(config.ConfigValue)
            : null;

        rules ??= new WalletTopUpRulesDto { MinGamingTopUp = DefaultMinGamingTopUp, DefaultBonusPercent = DefaultBonusPercent };

        return Ok(ApiResponse<WalletTopUpRulesDto>.Ok(rules));
    }

    [HttpPut]
    [Authorize(Policy = "Dashboard:wallet_settings")]
    public async Task<IActionResult> SaveRules([FromBody] WalletTopUpRulesDto dto)
    {
        var adminId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var config = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == ConfigKey);
        if (config == null)
        {
            config = new SystemConfig
            {
                Id = Guid.NewGuid(),
                ConfigKey = ConfigKey,
                ConfigValue = JsonSerializer.Serialize(dto),
                Description = "Gaming wallet top-up minimum amount and default bonus percentage",
                UpdatedBy = adminId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.SystemConfigs.Add(config);
        }
        else
        {
            config.ConfigValue = JsonSerializer.Serialize(dto);
            config.UpdatedBy = adminId;
            config.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync();

        return Ok(ApiResponse<WalletTopUpRulesDto>.Ok(dto));
    }
}
