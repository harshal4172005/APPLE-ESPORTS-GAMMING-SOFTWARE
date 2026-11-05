# New Updates — Count List

One file per update: `01-short-name.md`, `02-short-name.md`, ...
Work starts from count 1.

## Process (owner's rule, 4 Nov 2026)
1. Owner tells the problem.
2. Claude finds the cause and the solution, and explains it.
3. We discuss it.
4. Owner gives the green signal ("develop").
5. Claude develops and tests it locally.
6. **Nothing goes to the server one by one.** All counts ship together in ONE release at the end.

| # | Update | Where (Branch / HQ / Gaming PC) | Status |
|---|--------|---------------------------------|--------|
| 1 | [Super Admin "takes" the shift → forced takeover](01-super-admin-shift-takeover.md) | Branch + HQ | Built + tested locally — waits for combined release |
| 2 | [Difference total + per-operator difference](02-cash-register-difference-totals.md) | Screen (branch + HQ) | Built (client build passes) — screen not yet viewed in a browser |
| 3 | [Member can't log out after counter login](03-member-logout-after-counter-login.md) | Branch + gaming PC screen | Built + tested locally — waits for combined release |
| 4 | [Super Admin discount as ₹ amount](04-super-admin-flat-discount.md) | Screen (branch + HQ) | Built + tested locally — waits for combined release |
| 5 | [PC edit/delete from HQ doesn't show](05-pc-edit-delete-from-hq.md) | HQ + Branch | Built + tested locally — waits for combined release |
| 6 | [Extension request: show PC + customer](06-extension-request-pc-and-customer.md) | Branch | Built + tested locally — waits for combined release |
| 7 | [Member balance edit not showing](07-member-balance-edit-not-showing.md) | HQ + Branch + gaming PC screen | Built + tested locally — waits for combined release |
| 8 | [PS5 transaction on Cash Desk + EOD + log](08-ps5-transaction-entry.md) | Branch + HQ (money, EOD) | Built + tested locally (60/60 money checks) — waits for combined release |
| 9 | [Upgrade/downgrade a running package](09-change-package-mid-session.md) | Branch + gaming PC screen | Built + tested locally — waits for combined release |
| 10 | [EOD operator boxes: whole-day Cash/Online/Food](10-eod-operator-boxes-accurate.md) | EOD (branch + HQ) | Built + tested locally — waits for combined release |
| 11 | [Shift-end order + shared stock + Menu Editor totals](11-shift-end-flow-shared-stock-menu-totals.md) | Branch + HQ (stock) | Built + tested locally (25/25 + 13/13) — waits for combined release |
| 12 | [EOD range Total vs cash collected](12-eod-range-total-vs-collected.md) | EOD (HQ + branch) | Built + tested locally (10/10) — waits for next release |
| 13 | [PS5 log: Person and Hours columns + payment edit](13-ps5-person-and-hours-columns.md) | Branch + HQ | Built + tested locally (19/19 + 8/8) — waits for next release |
| 14 | [Member balance Add / Subtract in Edit Values](14-member-balance-add-subtract.md) | Branch + HQ + gaming PC | Built (frontend only, no new endpoint) — waits for next release |
| 15 | [Bonus tiers per amount + fix 0% bug](15-bonus-tiers-and-fix-zero.md) | Branch + HQ | Built — waits for next release |
| 16 | [Discount modal: show total + UI](16-discount-modal-show-total.md) | Branch + HQ | Built (frontend only) — waits for next release |
| 17 | [Change Package button UI](17-change-package-button-ui.md) | Branch + HQ | Built (frontend only) — waits for next release |
| 18 | [Shutdown All skips maintenance PCs](18-shutdown-all-skip-maintenance.md) | Branch | Built (server only) — waits for next release |
| 19 | [Power cut: pause timer and money](19-power-cut-pause-timer-and-money.md) | Branch + Gaming PC + HQ | Built — waits for next release |
| 20 | [Restart should not auto-bill](20-restart-should-not-auto-bill.md) | Branch + Gaming PC + HQ | Built (same fix as 19) — waits for next release |
| 21 | [EOD: Gaming Revenue box](21-eod-gaming-revenue-box.md) | EOD (branch + HQ) | Built — waits for next release |
| 22 | [Short session slab pricing](22-short-session-slab-pricing.md) | Branch + HQ + Gaming PC | Built — waits for next release |
| 23 | [Cash register super admin acknowledge](23-cash-register-super-admin-acknowledge.md) | Branch + HQ | Built — waits for next release |

Status values: Noted → Explained → Develop approved → Built → Tested → Shipped (version)

## Each update file holds
- What the owner asked (their words)
- What is wrong now / why
- What will be done
- Where it runs (branch exe, HQ, gaming PC) and version
- Status + date
