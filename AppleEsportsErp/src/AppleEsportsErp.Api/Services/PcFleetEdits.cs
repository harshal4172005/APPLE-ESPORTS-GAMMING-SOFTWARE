using AppleEsportsErp.Application.DTOs.Settings;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Api.Services;

/// <summary>
/// One set of rules for editing and removing a PC, used by every path that does it: the branch's
/// own Settings page, the branch running a Head Office command, and Head Office updating its own
/// copy once the branch confirms. Three copies of this used to drift - only one checked for a
/// running session, none carried the pricing profile, and none released the machine.
/// </summary>
public static class PcFleetEdits
{
    public static void ApplyUpdate(Pc pc, UpdatePcDto dto)
    {
        var isConsole = string.Equals(dto.Zone, "Console", StringComparison.OrdinalIgnoreCase);

        pc.PcNumber = dto.PcNumber;
        pc.PcName = dto.PcName ?? dto.PcNumber;
        pc.IpAddress = isConsole ? null : dto.IpAddress;
        pc.Specs = dto.Specs ?? "{}";
        pc.Zone = dto.Zone ?? "Standard";
        pc.HardwareNotes = dto.HardwareNotes;
        // Only when the form sent one: an older Head Office build sends no profile at all, and
        // that must not wipe the one the PC already has.
        if (dto.PricingProfileId is { } profileId) pc.PricingProfileId = profileId;
        pc.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Why this PC cannot be removed right now, or null if it can.</summary>
    public static string? DeleteBlockedReason(Pc pc) => pc.State switch
    {
        PcState.Active => $"{pc.PcNumber} is in use - a session is running. End it first.",
        PcState.Reserved => $"{pc.PcNumber} is reserved. Cancel the reservation first.",
        PcState.AwaitingBilling => $"{pc.PcNumber} has a bill waiting. Settle it first.",
        _ => null,
    };

    /// <summary>
    /// Removes the PC and releases the machine that was set up as it. The machine id is unique
    /// across deleted rows too, so leaving it on a deleted PC meant that computer could never be
    /// set up as any other PC - "Internal server error" when choosing a new number (Katargam,
    /// PC 01 to PC 13, 3 Nov 2026).
    /// </summary>
    public static void MarkDeleted(Pc pc)
    {
        pc.IsDeleted = true;
        pc.MachineId = null;
        pc.MachineToken = null;
        pc.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
