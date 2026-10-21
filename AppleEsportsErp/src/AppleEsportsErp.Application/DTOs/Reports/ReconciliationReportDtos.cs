using System;

namespace AppleEsportsErp.Application.DTOs.Reports;

public class ReconciliationReportDto
{
    public Guid ShiftId { get; set; }
    public Guid CashRegisterId { get; set; }
    public string OperatorName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    
    // Core Verification Math
    public decimal ExpectedDrawerCash { get; set; }
    public decimal PhysicalCashCounted { get; set; }
    public decimal Difference { get; set; }
    public string MismatchReason { get; set; } = string.Empty;
    public bool IsVerified { get; set; }

    // Physical Note Breakdown
    public int Notes2000 { get; set; }
    public int Notes500 { get; set; }
    public int Notes200 { get; set; }
    public int Notes100 { get; set; }
    public int Notes50 { get; set; }
    public int Notes20 { get; set; }
    public int Notes10 { get; set; }
    public int Coins5 { get; set; }
    public int Coins2 { get; set; }
    public int Coins1 { get; set; }

    /// <summary>Stock counted at this register's shift start and end, oldest first.</summary>
    public List<StockCountDto> StockCounts { get; set; } = new();
}

public class StockCountDto
{
    public string Kind { get; set; } = string.Empty;
    public string OperatorName { get; set; } = string.Empty;
    public DateTimeOffset CountedAt { get; set; }
    public List<StockCountLineDto> Lines { get; set; } = new();

    /// <summary>The bill this count raised for missing items, if any.</summary>
    public StockShortageDto? Shortage { get; set; }
}

/// <summary>A missing-stock bill: who it charges, how much, and whether/how it was paid.</summary>
public class StockShortageDto
{
    public Guid BillId { get; set; }
    public string BillNumber { get; set; } = string.Empty;
    public string ChargedTo { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    /// <summary>Settled at the counter - paid, or put on credit. False = still waiting to be paid.</summary>
    public bool Paid { get; set; }
    public string? PaidWith { get; set; }       // Cash, Online, Split, Credit
    public DateTimeOffset? PaidAt { get; set; }

    // How it was settled, as it was settled - never rewritten later.
    public decimal CashAmount { get; set; }
    public decimal OnlineAmount { get; set; }
    public decimal CreditAmount { get; set; }

    /// <summary>Status only: a credit cleared on a later day. The money counts on that day.</summary>
    public bool CreditCleared { get; set; }
    public DateTimeOffset? CreditClearedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Items { get; set; } = string.Empty;
}

public class StockCountLineDto
{
    public Guid InventoryId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int System { get; set; }
    public int Counted { get; set; }
}
