using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Infrastructure.Configuration;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Api;

public static class DbUpdater
{
    public static void UpdateSchema(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sql = @"
CREATE TABLE IF NOT EXISTS ""PricingProfiles"" (
    ""Id"" uuid NOT NULL DEFAULT uuid_generate_v4(),
    ""Name"" character varying(100) NOT NULL,
    ""BaseHourlyRate"" numeric NOT NULL,
    ""BranchId"" uuid NOT NULL,
    ""IsActive"" boolean NOT NULL DEFAULT true,
    ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
    ""UpdatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
    CONSTRAINT ""PK_PricingProfiles"" PRIMARY KEY (""Id""),
    CONSTRAINT ""FK_PricingProfiles_branches_BranchId"" FOREIGN KEY (""BranchId"") REFERENCES branches (id) ON DELETE CASCADE
);

ALTER TABLE pcs
ADD COLUMN IF NOT EXISTS ""PcName"" character varying(100),
ADD COLUMN IF NOT EXISTS ""Zone"" character varying(50),
ADD COLUMN IF NOT EXISTS ""PricingProfileId"" uuid,
ADD COLUMN IF NOT EXISTS ""HardwareNotes"" text,
ADD COLUMN IF NOT EXISTS ""IsActive"" boolean NOT NULL DEFAULT true,
ADD COLUMN IF NOT EXISTS ""IsDeleted"" boolean NOT NULL DEFAULT false;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_Pcs_PricingProfiles_PricingProfileId'
    ) THEN
        ALTER TABLE pcs
        ADD CONSTRAINT ""FK_Pcs_PricingProfiles_PricingProfileId"" FOREIGN KEY (""PricingProfileId"") REFERENCES ""PricingProfiles"" (""Id"") ON DELETE SET NULL;
    END IF;
END $$;";

        db.Database.ExecuteSqlRaw(sql);

        // Member login credentials (username + password)
        db.Database.ExecuteSqlRaw(@"
ALTER TABLE members
ADD COLUMN IF NOT EXISTS ""Username"" character varying(50),
ADD COLUMN IF NOT EXISTS ""PasswordHash"" character varying(255),
ADD COLUMN IF NOT EXISTS ""GamingBalance"" numeric(10, 2) NOT NULL DEFAULT 0.0,
ADD COLUMN IF NOT EXISTS ""FoodBalance"" numeric(10, 2) NOT NULL DEFAULT 0.0;

CREATE UNIQUE INDEX IF NOT EXISTS ""IX_members_Username""
    ON members (""Username"")
    WHERE ""Username"" IS NOT NULL;

ALTER TABLE wallet_transactions
ADD COLUMN IF NOT EXISTS ""TargetWallet"" character varying(20) NOT NULL DEFAULT 'Gaming';

ALTER TABLE reservations
ADD COLUMN IF NOT EXISTS ""AdvanceDeposit"" numeric(18,2) NOT NULL DEFAULT 0.0;

ALTER TABLE inventory
ADD COLUMN IF NOT EXISTS ""SoldQty"" integer NOT NULL DEFAULT 0;

ALTER TABLE operators
ADD COLUMN IF NOT EXISTS ""IsGlobalAdmin"" boolean NOT NULL DEFAULT false;

-- Set when the operator ticks ""this is the last shift of the day"" on the way out. It closes
-- the trading day, and it tells the outage check that the quiet which follows is the shop
-- being shut rather than a fault. Without it every branch reports a power cut every night.
ALTER TABLE shifts
ADD COLUMN IF NOT EXISTS ""ClosedTradingDay"" boolean NOT NULL DEFAULT false;

ALTER TABLE bills
ADD COLUMN IF NOT EXISTS ""IsDeferred"" boolean NOT NULL DEFAULT false;

ALTER TABLE cash_transactions
ADD COLUMN IF NOT EXISTS ""CashReceived"" numeric(10,2) NOT NULL DEFAULT 0.0,
ADD COLUMN IF NOT EXISTS ""ChangeReturned"" numeric(10,2) NOT NULL DEFAULT 0.0,
ADD COLUMN IF NOT EXISTS ""ActualCashCollected"" numeric(10,2) NOT NULL DEFAULT 0.0;
");

        // Employees table (SOP §HR-01) — never shipped as an EF migration, only as a
        // standalone /migrations/001_add_employees.sql that nothing executes automatically.
        // Create it here so fresh/existing databases both end up with it.
        db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS employees (
    ""Id""                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ""BranchId""                UUID NOT NULL REFERENCES branches(""Id"") ON DELETE RESTRICT,
    ""EmployeeNumber""          TEXT NOT NULL,
    ""FullName""                TEXT NOT NULL,
    ""Gender""                  TEXT,
    ""DateOfBirth""             DATE,
    ""Nationality""             TEXT DEFAULT 'Indian',
    ""MaritalStatus""           TEXT,
    ""PermanentAddress""        TEXT,
    ""CurrentAddress""          TEXT,
    ""Phone""                   TEXT,
    ""Email""                   TEXT,
    ""EmergencyName""           TEXT,
    ""EmergencyRelationship""   TEXT,
    ""EmergencyPhone""          TEXT,
    ""EmergencyEmail""          TEXT,
    ""EmergencyAddress""        TEXT,
    ""PositionTitle""           TEXT,
    ""Department""              TEXT,
    ""Supervisor""              TEXT,
    ""StartDate""                DATE,
    ""BankName""                TEXT,
    ""AccountNumber""           TEXT,
    ""AccountHolderName""       TEXT,
    ""BankBranch""              TEXT,
    ""RefName""                 TEXT,
    ""RefRelationship""         TEXT,
    ""RefPhone""                TEXT,
    ""RefAddress""              TEXT,
    ""PhotoDataUrl""            TEXT,
    ""AadharDataUrl""           TEXT,
    ""Status""                  TEXT NOT NULL DEFAULT 'Active',
    ""SubmittedBy""             UUID REFERENCES operators(""Id"") ON DELETE SET NULL,
    ""CreatedAt""                TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ""UpdatedAt""                TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

DO $$ BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'employees_employeenumber_unique'
  ) THEN
    ALTER TABLE employees ADD CONSTRAINT employees_employeenumber_unique UNIQUE (""EmployeeNumber"");
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_employees_branch_id ON employees(""BranchId"");
CREATE INDEX IF NOT EXISTS idx_employees_employee_number ON employees(""EmployeeNumber"");
CREATE INDEX IF NOT EXISTS idx_employees_status ON employees(""Status"");

ALTER TABLE employees
ADD COLUMN IF NOT EXISTS ""PhotoDataUrl"" text,
ADD COLUMN IF NOT EXISTS ""AadharDataUrl"" text;

-- One fridge, one count: who counted a shared item last, and when.
ALTER TABLE shared_stock_counts
ADD COLUMN IF NOT EXISTS ""CountedAt"" timestamptz,
ADD COLUMN IF NOT EXISTS ""CountedByBranchId"" uuid,
ADD COLUMN IF NOT EXISTS ""CountedByName"" text;

-- When the operator opened the count screen, so what moved while they counted can be shown.
ALTER TABLE stock_counts
ADD COLUMN IF NOT EXISTS ""OpenedAt"" timestamptz;

-- How long a PS5 customer played. Nullable: every other bill, and every PS5 entry made before
-- this, simply has none.
ALTER TABLE bills
ADD COLUMN IF NOT EXISTS ""PlayMinutes"" integer;
");

        // A PC that has never been claimed by a real machine used to be created as Idle -
        // PcsController.Create now creates it AwaitingSetup instead, but that fix only reaches
        // PCs created after it shipped. Every PC added before then is still sitting on the old
        // value, on every branch that had already added one, and looks identical on screen to a
        // real, working, free PC. MachineId IS NULL is the same signal AwaitingSetup itself is
        // defined by - a real claim always sets it - so this can never touch a PC that is
        // genuinely idle and working. Runs on every startup rather than once, so it also cleans
        // up anything added between two updates on a branch that skipped a version, and finds
        // nothing to do once a branch is already caught up.
        db.Database.ExecuteSqlRaw(@"
UPDATE pcs SET ""State"" = 'awaitingsetup', ""PoweredOff"" = false
WHERE ""MachineId"" IS NULL AND ""State"" = 'idle';

-- A separate leftover from the same underlying cause: a PC that was already AwaitingSetup
-- when someone pressed Shut Down / Shut Down All, before SendShutdownCommand and
-- SendShutdownAllCommand were guarded against sending that to an unclaimed PC. Nothing
-- meaningfully shuts down a machine that was never claimed, so PoweredOff should never be
-- true here regardless of how it got set.
UPDATE pcs SET ""PoweredOff"" = false
WHERE ""State"" = 'awaitingsetup' AND ""PoweredOff"" = true;
");

        // Seed the four branches, their PCs, pricing and operators — at HEAD OFFICE, in
        // Development ONLY.
        //
        // A branch must never run this. Its database starts empty, so nothing here would stop
        // it: the guard inside only skips when Adajan or Citylight already exist, and on a
        // freshly installed counter PC they do not. Every branch would therefore invent its own
        // copy of all four branches with its own identifiers — which is precisely the fault that
        // broke sync last time and which Phase 2 exists to prevent. It would also put eight
        // operators with invented email addresses and the password "12345" on the machine, and
        // insert a personal Gmail address as super admin on every till in the business.
        //
        // A branch gets its identity from Head Office instead, through /api/provisioning/adopt,
        // with Head Office's identifiers. Empty until then is the correct state, and an empty
        // branch is a branch that has not been adopted yet — which is visible, unlike a branch
        // quietly running on records it made up.
        //
        // The comment above stopped at "must never run on a branch" and never asked the other
        // half of the same question: what about a Head Office that is itself freshly
        // provisioned? IsHeadOffice() alone said yes to that too, and did so unconditionally
        // in every environment, including Production. A brand-new Head Office server came up
        // and seeded these same four branches under their real names before anyone had a
        // chance to point a real branch at it - the identical fault this guard exists to
        // prevent, arrived at from the other direction. This demo data belongs on a developer's
        // own machine, never on any server actually running the business, Head Office included.
        if (app.Configuration.IsHeadOffice() && app.Environment.IsDevelopment())
        {
            DataSeeder.SeedBranchesAsync(db).GetAwaiter().GetResult();
        }
        else if (!app.Configuration.IsHeadOffice())
        {
            app.Logger.LogInformation(
                "Branch instance: skipping the Head Office seed. This database stays empty until " +
                "the branch is adopted and takes Head Office's identifiers.");
        }
    }
}
