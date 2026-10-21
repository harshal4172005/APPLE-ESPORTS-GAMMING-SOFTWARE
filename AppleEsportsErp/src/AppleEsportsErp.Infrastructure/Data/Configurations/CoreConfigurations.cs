using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Infrastructure.Data.Configurations;

/// <summary>SOP §16: Branch Configuration — schema.sql L14-23</summary>
public class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("branches");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("uuid_generate_v4()");
        builder.Property(e => e.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Address).HasColumnType("text");
        builder.Property(e => e.OpeningTime).HasDefaultValue(new TimeOnly(10, 0));
        builder.Property(e => e.ClosingTime).HasDefaultValue(new TimeOnly(2, 0));
        builder.Property(e => e.Status).HasMaxLength(20).HasDefaultValue(BranchStatus.Active)
            .HasConversion(v => v.ToString().ToLowerInvariant(),
                           v => Enum.Parse<BranchStatus>(v, true));
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");

        builder.HasOne(e => e.FoodGroup).WithMany(g => g.Branches)
            .HasForeignKey(e => e.FoodGroupId).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>Links branches that share one food/snacks menu and stock count.</summary>
public class FoodGroupConfiguration : IEntityTypeConfiguration<FoodGroup>
{
    public void Configure(EntityTypeBuilder<FoodGroup> builder)
    {
        builder.ToTable("food_groups");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("uuid_generate_v4()");
        builder.Property(e => e.Name).HasMaxLength(100).IsRequired();
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
    }
}

/// <summary>Head Office's one count per shared-fridge item - see SharedStockCount.</summary>
public class SharedStockCountConfiguration : IEntityTypeConfiguration<SharedStockCount>
{
    public void Configure(EntityTypeBuilder<SharedStockCount> builder)
    {
        builder.ToTable("shared_stock_counts");
        builder.HasKey(e => new { e.FoodGroupId, e.InventoryItemId });
        builder.HasIndex(e => new { e.FoodGroupId, e.Version });
    }
}

public class SharedStockRequestConfiguration : IEntityTypeConfiguration<SharedStockRequest>
{
    public void Configure(EntityTypeBuilder<SharedStockRequest> builder)
    {
        builder.ToTable("shared_stock_requests");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Op).HasMaxLength(10).IsRequired();
        builder.Property(e => e.RefusedReason).HasMaxLength(300);
        builder.HasIndex(e => e.CreatedAt);
    }
}

public class StockCountConfiguration : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> builder)
    {
        builder.ToTable("stock_counts");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.OperatorName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Kind).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Lines).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(e => e.CashRegisterId);
        builder.HasIndex(e => new { e.BranchId, e.CountedAt });
    }
}

public class SharedStockPendingConfiguration : IEntityTypeConfiguration<SharedStockPending>
{
    public void Configure(EntityTypeBuilder<SharedStockPending> builder)
    {
        builder.ToTable("shared_stock_pending");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Op).HasMaxLength(10).IsRequired();
    }
}

/// <summary>SOP §5.1: Super Admin accounts — schema.sql L29-40</summary>
public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("uuid_generate_v4()");
        builder.Property(e => e.Email).HasMaxLength(255).IsRequired();
        builder.HasIndex(e => e.Email).IsUnique();
        builder.Property(e => e.PasswordHash).HasMaxLength(255).IsRequired();
        builder.Property(e => e.FullName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Role).HasMaxLength(20).HasDefaultValue("super_admin");
        builder.Property(e => e.Status).HasMaxLength(20).HasDefaultValue(UserStatus.Active)
            .HasConversion(v => v.ToString().ToLowerInvariant(),
                           v => Enum.Parse<UserStatus>(v, true));
        builder.Property(e => e.DeviceInfo).HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");
    }
}

/// <summary>SOP §5.2: Operators — schema.sql L46-74</summary>
public class OperatorConfiguration : IEntityTypeConfiguration<Operator>
{
    public void Configure(EntityTypeBuilder<Operator> builder)
    {
        builder.ToTable("operators");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("uuid_generate_v4()");
        builder.Property(e => e.FullName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Username).HasMaxLength(50).IsRequired();
        builder.HasIndex(e => e.Username).IsUnique();
        builder.Property(e => e.PasswordHash).HasMaxLength(255).IsRequired();
        builder.Property(e => e.MobileNumber).HasMaxLength(20);
        builder.Property(e => e.Status).HasMaxLength(20).HasDefaultValue(OperatorStatus.Active)
            .HasConversion(v => v.ToString().ToLowerInvariant().Replace("loggedout", "logged_out"),
                           v => Enum.Parse<OperatorStatus>(v.Replace("logged_out", "LoggedOut"), true));
        builder.Property(e => e.DashboardPermissions).HasColumnType("jsonb");
        builder.Property(e => e.DeviceInfo).HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");

        // Indexes
        builder.HasIndex(e => e.BranchId).HasDatabaseName("idx_operators_branch");
        builder.HasIndex(e => e.Status).HasDatabaseName("idx_operators_status");

        // Relationships
        builder.HasOne(e => e.Branch).WithMany(b => b.Operators)
            .HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Creator).WithMany()
            .HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>SOP §7.1: PC stations — schema.sql L83-97</summary>
public class PcConfiguration : IEntityTypeConfiguration<Pc>
{
    public void Configure(EntityTypeBuilder<Pc> builder)
    {
        builder.ToTable("pcs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("uuid_generate_v4()");
        builder.Property(e => e.PcNumber).HasMaxLength(20).IsRequired();
        builder.Property(e => e.State).HasMaxLength(20).HasDefaultValue(PcState.Idle)
            .HasConversion(v => v.ToString().ToLowerInvariant().Replace("awaitingbilling", "awaiting_billing"),
                           v => Enum.Parse<PcState>(v.Replace("awaiting_billing", "AwaitingBilling"), true));
        builder.Property(e => e.IpAddress).HasMaxLength(45);
        builder.Property(e => e.Specs).HasColumnType("jsonb");

        // Setup / provisioning
        builder.Ignore(e => e.IsProvisioned);   // derived from MachineId, not a column
        builder.Property(e => e.MachineId).HasMaxLength(128);
        // A JWT, not a short key — around 1,200 characters, and it grows with the claims it
        // carries. Capping it truncated the token and the insert failed outright.
        builder.Property(e => e.MachineToken).HasColumnType("text");

        // One machine, one PC. Without this a single machine could claim several PC numbers
        // and receive unlock commands meant for other seats. Filtered so the many unclaimed
        // PCs (MachineId null) do not collide with each other.
        builder.HasIndex(e => e.MachineId)
            .IsUnique()
            .HasFilter("\"MachineId\" IS NOT NULL")
            .HasDatabaseName("idx_pcs_machine_unique");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");

        // Composite unique
        builder.HasIndex(e => new { e.PcNumber, e.BranchId }).IsUnique();
        builder.HasIndex(e => e.BranchId).HasDatabaseName("idx_pcs_branch");
        builder.HasIndex(e => e.State).HasDatabaseName("idx_pcs_state");

        // Relationships
        builder.HasOne(e => e.Branch).WithMany(b => b.Pcs)
            .HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.CurrentSession).WithMany()
            .HasForeignKey(e => e.CurrentSessionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.CurrentReservation).WithMany()
            .HasForeignKey(e => e.CurrentReservationId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.LastOperator).WithMany()
            .HasForeignKey(e => e.LastOperatorId).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>SOP §HR-01: Employee HR records — migrations/001_add_employees.sql</summary>
public class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("employees");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.EmployeeNumber).HasMaxLength(20).IsRequired();
        builder.HasIndex(e => e.EmployeeNumber).IsUnique();
        builder.Property(e => e.FullName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("Active");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");

        builder.HasIndex(e => e.BranchId).HasDatabaseName("idx_employees_branch_id");

        builder.HasOne(e => e.Branch).WithMany()
            .HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.SubmittedByOperator).WithMany()
            .HasForeignKey(e => e.SubmittedBy).OnDelete(DeleteBehavior.SetNull);

        // Made explicit (was previously left to convention) so an unrelated migration diffing
        // the model doesn't pick up a changed default and silently alter this relationship's
        // existing, already-deployed behavior.
        builder.HasOne(e => e.Operator).WithMany()
            .HasForeignKey(e => e.OperatorId).OnDelete(DeleteBehavior.SetNull);
    }
}
