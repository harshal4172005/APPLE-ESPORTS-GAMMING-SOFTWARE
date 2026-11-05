namespace AppleEsportsErp.Application.Services;

/// <summary>
/// Single source of truth for "what does this session cost right now".
/// Used identically by: final billing on session stop, the live PC-status feed
/// (operator PC cards, member overlay), and the billing counter — so every
/// screen always shows the same number for the same session.
/// </summary>
public static class SessionPricingCalculator
{
    // No fabricated fallback rate — a PC is required to have a Pricing Profile (enforced at
    // session-start and PC-creation time). If one is ever missing anyway, showing/charging ₹0
    // is the honest behavior — inventing a number is worse than admitting it's unconfigured.
    public const decimal DefaultRatePerHour = 0m;
    public const int DefaultBufferMinutes = 10;

    /// <summary>
    /// Gaming charge only (excludes food/add-ons). Free during the buffer window,
    /// then billed for exact elapsed time at the given hourly rate — regardless of
    /// whether the session was booked as a fixed package or open/Pay-As-You-Go.
    /// </summary>
    public static decimal CalculateGamingAmount(decimal ratePerHour, int bufferMinutes, decimal elapsedMinutes,
        List<Domain.Entities.ShortSessionSlab>? slabs = null)
    {
        if (elapsedMinutes <= bufferMinutes)
            return 0m;

        if (slabs is { Count: > 0 })
        {
            var match = slabs.Where(s => elapsedMinutes > s.FromMinutes && elapsedMinutes <= s.ToMinutes)
                .OrderByDescending(s => s.FromMinutes).FirstOrDefault();
            if (match != null) return match.Amount;
        }

        decimal hours = elapsedMinutes / 60m;
        return Math.Round(hours * ratePerHour, 2);
    }

    /// <summary>
    /// What a session actually costs right now, package or not - the one calculation every
    /// screen showing a live amount must call, instead of each re-deriving its own hourly
    /// number from <see cref="CalculateGamingAmount"/> directly.
    ///
    /// Before this existed as a shared call, five separate places (the billing counter's live
    /// bill, applying a discount, the PC card, Head Office's remote view, and the
    /// customer-facing public display) each recomputed the amount purely as
    /// hours &#215; BaseHourlyRate - ignoring a session's own committed package price entirely,
    /// even though the session was genuinely running under "4 hrs - Rs 180" or similar. Only
    /// the final Stop settlement ever got this right, so every screen a customer or operator
    /// actually watches *while a session is running* showed a number with nothing to do with
    /// what they would really be charged - and a mid-session discount was computed off that
    /// wrong number too. This is that same, now-shared, logic.
    ///
    /// <paramref name="packagePrice"/>/<paramref name="plannedDurationMin"/> are a session's own
    /// <c>PackagePrice</c>/<c>PlannedDurationMin</c> - null for a genuine Pay-As-You-Go session,
    /// which always remains pure hourly here, exactly as before.
    ///
    /// A package only ever charges its flat price for FINISHING it - reaching the planned time,
    /// or stopping within the buffer's own grace window of the end (the same few minutes that
    /// already forgive a late stop don't start penalising an early one). Stop meaningfully
    /// earlier than that and the package was never actually used, so it bills the honest elapsed
    /// rate instead, same as genuine Pay-As-You-Go - never the flat price for time nobody played.
    /// Explicit owner instruction: a 1 hr (₹50) session stopped at 45 minutes must bill what 45
    /// minutes actually costs, not the full ₹50 a customer who never got the full hour never used.
    /// </summary>
    /// <paramref name="packageFromMin"/> is the package's own "package price from" setting
    /// (owner, 25 Oct 2026: 4 hr / Rs 200 from 3 hr 30 min). Null keeps the old edge,
    /// (planned - buffer). Played past the package, the extra minutes are hourly on top.
    public static decimal CalculateLiveGamingAmount(
        decimal? packagePrice, int? plannedDurationMin,
        decimal ratePerHour, int bufferMinutes, decimal elapsedMinutes, int? packageFromMin = null,
        List<Domain.Entities.ShortSessionSlab>? slabs = null)
    {
        if (elapsedMinutes <= bufferMinutes)
            return 0m;

        if (packagePrice.HasValue && plannedDurationMin.HasValue)
        {
            decimal overrunMinutes = elapsedMinutes - plannedDurationMin.Value;

            if (elapsedMinutes < (packageFromMin ?? plannedDurationMin.Value - bufferMinutes))
                return CalculateGamingAmount(ratePerHour, bufferMinutes, elapsedMinutes, slabs);

            return overrunMinutes <= bufferMinutes
                ? packagePrice.Value
                : packagePrice.Value + CalculateGamingAmount(ratePerHour, 0, overrunMinutes);
        }

        return CalculateGamingAmount(ratePerHour, bufferMinutes, elapsedMinutes, slabs);
    }

    /// <summary>
    /// Rounds a final bill total to the nearest ₹10 — down for a remainder of 0-4,
    /// up for 5-9 (standard round-half-up) — so the amount actually charged never
    /// lands on an awkward figure. Applied only at the point a bill's TotalAmount
    /// is finalized; never on line items, Subtotal, DiscountAmount, or wallet top-ups.
    ///
    /// A remainder of exactly 5 rounds UP, not down. It used to round down, which
    /// silently took ₹5 off every fairly-priced total that landed exactly on a ₹5
    /// remainder - a genuine ₹75 custom package (1 hr = ₹50, played 1.5 hr) was
    /// charged ₹70 with no discount, adjustment, or explanation anywhere on the bill.
    /// </summary>
    public static decimal RoundBillTotal(decimal amount)
    {
        if (amount <= 0m) return 0m;

        decimal remainder = amount % 10m;
        return remainder < 5m
            ? amount - remainder
            : amount + (10m - remainder);
    }

    /// <summary>
    /// How long a member can play before their wallet is used up — worked out in advance.
    ///
    /// This is the inverse of the billing above, and it exists so a session can be stopped at
    /// the right moment instead of caught after the fact. Checking "has he gone over?" once a
    /// minute only ever notices once he has: ₹10 buys ten minutes, the check fires at minute
    /// eleven, and the member is left owing ₹1 that was never there. That shortfall is created
    /// purely by waiting to catch him rather than stopping him on time.
    ///
    /// The answer must be derived from <see cref="RoundBillTotal"/> rather than from the hourly
    /// rate alone, because rounding is applied BEFORE the wallet is deducted and it rounds *up*
    /// above a ₹5 remainder. A member with ₹16 stopped at exactly ₹16 of play is billed ₹20 and
    /// walks away owing ₹4 — a perfectly timed stop, and still a debt. So the search below walks
    /// down through real amounts asking the actual rounding function what each would be charged.
    /// It cannot drift out of step with billing, because it is billing that answers.
    ///
    /// Returns the elapsed minutes at which to stop. Never less than the free buffer: nobody can
    /// be charged inside it, so there is nothing to run out of there.
    /// </summary>
    /// <param name="safetyRupees">
    /// Headroom for the fact that nothing fires at the exact millisecond. Whatever checks this
    /// will be a little late, and the stop must still be affordable when it is.
    ///
    /// This is not a nicety. Without it the obvious answer — the most play the balance covers —
    /// lands exactly on a rounding boundary, because ₹14 of play at a ₹10 rounding rounds down to
    /// ₹10 and ₹15 rounds up to ₹20. Sitting on that edge, a few seconds of lateness moves the
    /// bill by a whole ₹10 and hands the member a debt. Tested across four branch rates and 200
    /// balances: parked on the boundary, two thirds of cases left the member owing money.
    /// </param>
    public static decimal AffordableMinutes(
        decimal ratePerHour, int bufferMinutes, decimal balance, decimal safetyRupees = 1m)
    {
        // A PC with no rate configured bills ₹0, so the wallet can never run out on it. Stopping
        // the session would be inventing a limit that does not exist.
        if (ratePerHour <= 0m) return decimal.MaxValue;
        if (balance <= 0m) return bufferMinutes;

        // Start above the balance, not at it. Rounding can come *down* by as much as ₹4, so play
        // worth ₹14 is charged ₹10 — a member with ₹10 can afford fourteen minutes at ₹60/hour,
        // and starting the search at the balance would cut them off short. The +5 headroom here
        // is deliberately more than that ₹4 max, so the search always starts above the true edge.
        decimal raw = balance + 5m;

        // The test is what the bill would be if the stop arrives late — so the answer holds when
        // it does. RoundBillTotal is asked rather than reimplemented: this cannot drift out of
        // step with billing, because billing is what answers.
        while (raw > 0m && RoundBillTotal(raw + safetyRupees) > balance)
            raw -= 0.5m;

        var safetyMinutes = safetyRupees * 60m / ratePerHour;
        var affordableMinutes = raw <= 0m ? 0m : raw * 60m / ratePerHour;

        // The free buffer has a cliff at its far edge, and it is savage: the instant the buffer
        // expires the WHOLE elapsed time becomes billable at once, not just the part beyond it.
        // At ₹60/hour with a 10 minute buffer, ten minutes costs nothing and ten minutes and one
        // second costs ₹10. So a member who cannot afford that first chargeable moment has to be
        // stopped *short* of the edge, with room for the stop to arrive late — parking them on it
        // was 228 of the cases where a member still ended up in debt.
        if (affordableMinutes <= bufferMinutes)
            return Math.Max(0m, bufferMinutes - safetyMinutes);

        return affordableMinutes;
    }

    /// <summary>
    /// Rounds a bill's total to the nearest ₹10 AND folds the adjustment into the
    /// Gaming line so every number on the bill (line items, subtotal, total) agrees
    /// with the same figure — never a line item that doesn't add up to the total.
    /// The Gaming charge (time-based, not a fixed menu price) absorbs the rounding;
    /// if the session is still free/under-buffer (Gaming = 0), the Food line absorbs
    /// it instead so a free session never appears to have a Gaming charge.
    /// </summary>
    /// <summary>
    /// What an extension of <paramref name="minutes"/> costs on this PC: the price of an active
    /// package of exactly that length if there is one, otherwise the hourly rate pro-rata. Used by
    /// both the operator's approval card and the charge itself, so the two can never disagree.
    /// </summary>
    public static (decimal Amount, AppleEsportsErp.Domain.Entities.PricingPackage? Package) ExtensionPrice(
        AppleEsportsErp.Domain.Entities.PricingProfile? profile, decimal minutes)
    {
        var package = profile?.Packages?
            .Where(pkg => pkg.IsActive && pkg.DurationMinutes == (int)minutes)
            .OrderBy(pkg => pkg.SortOrder)
            .FirstOrDefault();
        return (package?.Price ?? (minutes / 60m) * (profile?.BaseHourlyRate ?? 0m), package);
    }

    public static (decimal displayGamingAmount, decimal displayFoodAmount, decimal roundedTotal) ComputeRoundedBreakdown(
        decimal gamingAmount, decimal foodAmount, decimal discountAmount)
    {
        decimal rawTotal = Math.Max(0m, gamingAmount + foodAmount - discountAmount);
        decimal roundedTotal = RoundBillTotal(rawTotal);
        decimal diff = roundedTotal - rawTotal;

        decimal displayGaming = gamingAmount;
        decimal displayFood = foodAmount;

        if (gamingAmount > 0m)
            displayGaming = Math.Max(0m, gamingAmount + diff);
        else if (foodAmount > 0m)
            displayFood = Math.Max(0m, foodAmount + diff);

        return (displayGaming, displayFood, roundedTotal);
    }
}
