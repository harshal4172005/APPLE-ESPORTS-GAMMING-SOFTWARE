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

Status values: Noted → Explained → Develop approved → Built → Tested → Shipped (version)

## Each update file holds
- What the owner asked (their words)
- What is wrong now / why
- What will be done
- Where it runs (branch exe, HQ, gaming PC) and version
- Status + date
