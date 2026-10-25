using AppleEsportsErp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppleEsportsErp.Infrastructure.Migrations
{
    /// <summary>
    /// One-off, Citylight-144Hz only (same as 3.1.73) - a no-op anywhere else, the Id doesn't
    /// exist there. Nazmin's 25 Oct morning drawer: fifteen of 24 Oct's bills were corrected
    /// Cash -> Online at 09:58 and each correction took its amount (Rs 1,090 in all) out of her
    /// drawer, which never held that cash. Expected 510 -> 1,600, difference 1,140 -> 50 (what
    /// she counted, 1,650, minus what really went in). The fifteen correction rows stay as a
    /// record but move nothing, the same as the fixed code now does for an earlier shift's bill.
    /// The branch resends both to Head Office on its own (SyncManifestReconcilerService).
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261025140000_FixCitylightOct25PaymentCorrections")]
    public partial class FixCitylightOct25PaymentCorrections : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                -- ActualCashCollected is added by DbUpdater, after migrations, so a fresh install
                -- doesn't have it yet - and has nothing to fix either.
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM cash_register WHERE ""Id"" = '26e2958d-407c-49a3-b583-80e0d860baef' AND ""ExpectedDrawerCash"" = 510) THEN
                    UPDATE cash_transactions SET ""CashAmount"" = 0, ""ActualCashCollected"" = 0
                    WHERE ""CashRegisterId"" = '26e2958d-407c-49a3-b583-80e0d860baef'
                      AND ""TransactionType"" = 'payment_method_correction';
                  END IF;
                END $$;

                UPDATE cash_register
                SET ""ExpectedDrawerCash"" = 1600, ""TotalCashSales"" = 600, ""CashDifference"" = 50
                WHERE ""Id"" = '26e2958d-407c-49a3-b583-80e0d860baef'
                  AND ""ExpectedDrawerCash"" = 510
                  AND ""PhysicalCashCounted"" = 1650;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
