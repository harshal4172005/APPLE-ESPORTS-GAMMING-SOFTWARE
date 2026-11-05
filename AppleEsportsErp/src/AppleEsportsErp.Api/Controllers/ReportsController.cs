using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Api.Extensions;
using AppleEsportsErp.Api.Filters;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Application.DTOs.Reports;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Infrastructure.Data;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AppleEsportsErp.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
[BranchIsolation]
public class ReportsController : ControllerBase
{
    private readonly IReportsService _reportsService;
    private readonly AppDbContext _db;

    public ReportsController(IReportsService reportsService, AppDbContext db)
    {
        _reportsService = reportsService;
        _db = db;
    }

    private Guid GetBranchId() => Guid.Parse(HttpContext.Items["BranchId"]!.ToString()!);

    [HttpGet("cash-reconciliation")]
    public async Task<IActionResult> GetCashReconciliationReport([FromQuery] DateTime startDate, [FromQuery] DateTime endDate)
    {
        var branchId = GetBranchId();
        var report = await _reportsService.GetCashReconciliationReportAsync(branchId, startDate, endDate);
        return Ok(ApiResponse<List<ReconciliationReportDto>>.Ok(report));
    }

    [HttpPatch("cash-register/{id}/acknowledge")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> AcknowledgeCashRegister(Guid id, [FromBody] AcknowledgeDto dto)
    {
        var reg = await _db.Set<CashRegister>().FirstOrDefaultAsync(r => r.Id == id);
        if (reg == null) return NotFound(ApiResponse<object>.Fail("Cash register not found."));

        reg.SuperAdminAcknowledged = dto.Acknowledged;
        reg.AcknowledgedAt = dto.Acknowledged ? DateTimeOffset.UtcNow : null;
        await _db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(new { reg.SuperAdminAcknowledged, reg.AcknowledgedAt }));
    }
}

public class AcknowledgeDto
{
    public bool Acknowledged { get; set; }
}
