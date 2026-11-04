import { useEffect, useState } from 'react';
import { PackageSearch, Receipt, CheckCircle2, AlertTriangle } from 'lucide-react';
import api from '../../config/api';
import { processPayment } from '../../api/billing.api';

const time = (iso) => new Date(iso).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', hour12: true });

/**
 * Shift stock count.
 *
 * Two things this has to get right, both reported by the owner on 4 Nov 2026:
 *
 * 1. The number on screen can move while the operator is counting - another sale goes through -
 *    and the operator was then told they had one extra ("I counted 28 water, it showed 27").
 *    So the screen remembers what the system said when it OPENED (`systemWas`) and the server
 *    reports what moved in between, instead of silently comparing against a number that changed.
 *
 * 2. A shared fridge is one physical shelf, and every branch counts it at its own shift end
 *    (owner, 4 Nov 2026) - nobody is refused for being second. What went wrong at Citylight on
 *    31 Oct was that each counter compared the shelf against its OWN copy: 144Hz still held 0
 *    while the fridge held 14, so a correct count of 14 was reported as "14 extra". "System says"
 *    now comes from Head Office live, and if another counter counted minutes ago, both their
 *    number and this one are shown.
 */
export function MissingStockCount({ registerId, onDone, kind = 'closing', title = 'Count the stock' }) {
  const [sheet, setSheet] = useState(null);
  const [counted, setCounted] = useState({});
  const [review, setReview] = useState(null);
  const [notes, setNotes] = useState([]);
  const [trail, setTrail] = useState([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const load = async () => {
    setError('');
    try {
      const res = await api.get('/inventory/stock-count/sheet');
      const data = res.data?.data;
      setSheet(data);
      setCounted(Object.fromEntries((data?.items || []).map(i => [i.inventoryId, String(i.system)])));
    } catch (err) {
      setError(err.response?.data?.error || 'Could not load the stock list.');
    }
  };
  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, []);

  const toCount = sheet?.items || [];
  const value = (i) => {
    const raw = counted[i.inventoryId];
    const n = Number(raw);
    return raw === undefined || raw === '' || Number.isNaN(n) ? i.system : n;
  };
  const missingLines = () => toCount
    .map(i => ({ item: i, missing: i.system - value(i) }))
    .filter(l => l.missing > 0);

  const save = async () => {
    setBusy(true);
    setError('');
    try {
      const res = await api.post('/inventory/stock-count', {
        kind,
        cashRegisterId: registerId,
        openedAt: sheet?.openedAt,
        counts: toCount.map(i => ({ inventoryId: i.inventoryId, counted: value(i), systemWas: i.system })),
      });
      const data = res.data?.data;
      const moved = (data?.lines || []).filter(l => l.movedWhileCounting);
      const all = [
        ...(data?.sharedNotes || []),
        ...moved.map(l => `${l.itemName}: ${Math.abs(l.movedWhileCounting)} ${l.movedWhileCounting < 0 ? 'sold' : 'added'} while you were counting.`),
      ];
      // Where anything short actually went - shown before the operator is asked to pay.
      setTrail(data?.trail || []);
      // Nothing to explain: carry straight on, as before.
      if (all.length === 0 && (data?.trail || []).length === 0) { onDone(); return; }
      setNotes(all);
      setReview(null);
      setBusy(false);
    } catch (err) {
      setError(err.response?.data?.error || 'Could not save the stock count.');
      setBusy(false);
    }
  };

  const check = () => {
    const lines = missingLines();
    if (lines.length === 0) save();
    else setReview(lines);
  };

  const total = (review || []).reduce((s2, l) => s2 + l.missing * Number(l.item.price ?? 0), 0);

  if (!sheet) {
    return (
      <div className="flex-1 flex items-center justify-center border border-border bg-bg-2 rounded-xl p-6 text-text-3 text-sm">
        {error || 'Loading the stock list...'}
      </div>
    );
  }

  // Saved, with something the operator needs to know about it.
  if (notes.length > 0) {
    return (
      <div className="flex-1 flex flex-col border border-border bg-bg-2 rounded-xl p-6">
        <div className="flex items-center gap-2 mb-3">
          <CheckCircle2 className="w-5 h-5 text-neon-green" />
          <h2 className="text-lg font-heading font-bold uppercase tracking-widest text-text">Count saved</h2>
        </div>
        {notes.length > 0 && (
          <div className="mb-4 p-4 rounded-lg border border-neon-orange/40 bg-neon-orange/10 space-y-1">
            {notes.map((n, idx) => (
              <div key={idx} className="text-xs text-text-2 flex items-start gap-2">
                <AlertTriangle className="w-3.5 h-3.5 text-neon-orange shrink-0 mt-0.5" /> {n}
              </div>
            ))}
          </div>
        )}

        {trail.length > 0 && (
          <div className="mb-4 p-4 rounded-lg border border-border bg-bg-3">
            <div className="text-text-3 uppercase tracking-wider font-bold text-[10px] mb-2">
              Where the short stock went, since the last count
            </div>
            {trail.map((t, idx) => (
              <div key={idx} className="flex justify-between text-xs font-mono text-text-2">
                <span>{t.itemName}</span>
                <span className="text-text-3">{t.quantity} {t.what} at {time(t.at)}</span>
              </div>
            ))}
            <p className="text-[11px] text-text-3 mt-2 font-sans">
              Anything above is accounted for. Whatever is left over is billed to you at menu price.
            </p>
          </div>
        )}
        <button onClick={onDone} className="btn-primary w-full py-3">Continue</button>
      </div>
    );
  }

  return (
    <div className="flex-1 flex flex-col border border-border bg-bg-2 rounded-xl p-6">
      <div className="flex items-center gap-2 mb-1">
        <PackageSearch className="w-5 h-5 text-accent" />
        <h2 className="text-lg font-heading font-bold uppercase tracking-widest text-text">{title}</h2>
      </div>
      <p className="text-text-3 text-xs mb-4 leading-relaxed">
        Count what is actually on the shelf. Anything missing is billed at menu price and paid before the register is locked.
        {sheet.sharedFridge && ' This fridge is shared, so count it here too - the number below is the live fridge, not this counter\'s own copy.'}
      </p>

      {sheet.sharedFridge && !sheet.fridgeLive && (
        <div className="mb-4 p-3 rounded-lg border border-neon-orange/40 bg-neon-orange/10 text-xs text-text-2 flex items-start gap-2">
          <AlertTriangle className="w-3.5 h-3.5 text-neon-orange shrink-0 mt-0.5" />
          No line to Head Office, so these are this counter's own numbers. Count the shelf anyway - the fridge is brought in step when the line is back.
        </div>
      )}

      {toCount.length === 0 ? (
        <div className="text-text-3 text-xs mb-4">Nothing left to count here.</div>
      ) : (
        <div className="max-h-72 overflow-auto -mx-1 px-1 mb-4">
          <table className="w-full text-xs">
            <thead className="text-text-3 text-[10px] uppercase tracking-wider">
              <tr>
                <th className="text-left font-normal pb-2">Item</th>
                <th className="text-right font-normal pb-2">System says</th>
                <th className="text-right font-normal pb-2 w-24">On the shelf</th>
              </tr>
            </thead>
            <tbody>
              {toCount.map(i => {
                const differs = value(i) !== i.system;
                return (
                  <tr key={i.inventoryId} className="border-t border-border/60">
                    <td className="py-2 text-text-2">
                      {i.itemName}
                      {i.lastCountedBy && (
                        <span className="block text-[10px] text-text-3">
                          {i.lastCountedBy} counted this at {time(i.lastCountedAt)}
                        </span>
                      )}
                    </td>
                    <td className="py-2 text-right font-mono text-text-3">{i.system}</td>
                    <td className="py-2 text-right">
                      <input
                        type="number" min="0"
                        value={counted[i.inventoryId] ?? ''}
                        disabled={!!review || busy}
                        onChange={e => setCounted({ ...counted, [i.inventoryId]: e.target.value })}
                        className={`w-20 bg-bg-3 border rounded px-2 py-1 text-right font-mono text-xs ${differs ? 'border-neon-orange text-neon-orange' : 'border-border text-text'}`}
                      />
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {review && (
        <div className="mb-4 p-4 rounded-lg border border-neon-red/50 bg-neon-red/10">
          <div className="text-neon-red text-sm font-bold mb-2">Missing stock</div>
          {review.map(l => (
            <div key={l.item.inventoryId} className="flex justify-between text-xs font-mono text-text-2">
              <span>{l.item.itemName}: system {l.item.system}, counted {value(l.item)}</span>
              <span className="text-neon-red font-bold">{l.missing} missing x Rs {Number(l.item.price ?? 0)}</span>
            </div>
          ))}
          <div className="flex justify-between border-t border-neon-red/30 mt-2 pt-2 text-sm font-bold text-neon-red">
            <span>To pay</span><span className="font-mono">Rs {total.toFixed(2)}</span>
          </div>
        </div>
      )}

      {error && <p className="text-neon-red text-xs mb-3">{error}</p>}

      {review ? (
        <div className="grid grid-cols-2 gap-3">
          <button onClick={() => { setReview(null); load(); }} disabled={busy} className="btn-secondary py-3">Recount</button>
          <button onClick={save} disabled={busy} className="btn-primary py-3">{busy ? 'Saving...' : 'Confirm'}</button>
        </div>
      ) : (
        <button onClick={check} disabled={busy} className="btn-primary w-full py-3">{busy ? 'Saving...' : 'Check counts'}</button>
      )}
    </div>
  );
}

/**
 * Shift end, step 2: the operator's unpaid missing-stock bills, each paid like any bill at the
 * Billing Counter - cash, online, split or credit (same requests as BillDetailsPanel). The
 * register cannot be locked until every one is settled (server: CashDeskService).
 */
export function MissingStockPay({ bills, onPaid }) {
  return (
    <div className="flex-1 flex flex-col border border-neon-red/40 bg-bg-2 rounded-xl p-6">
      <div className="flex items-center gap-2 mb-1">
        <Receipt className="w-5 h-5 text-neon-red" />
        <h2 className="text-lg font-heading font-bold uppercase tracking-widest text-text">Step 2 · Pay for missing stock</h2>
      </div>
      <p className="text-text-3 text-xs mb-4">Pay by cash, online, split or credit. Then lock the register and count the notes.</p>
      <div className="space-y-4">
        {bills.map(b => <PayOne key={b.id} bill={b} onPaid={onPaid} />)}
      </div>
    </div>
  );
}

function PayOne({ bill, onPaid }) {
  const total = Number(bill.totalAmount) || 0;
  const [method, setMethod] = useState('cash');
  const [cashReceived, setCashReceived] = useState(String(total));
  const [splitCash, setSplitCash] = useState('');
  const [splitOnline, setSplitOnline] = useState('');
  const [paidNow, setPaidNow] = useState('0');
  const [name, setName] = useState((bill.customerName || '').replace(/^Missing stock - /, ''));
  const [phone, setPhone] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const pay = async () => {
    setError('');
    let payload;
    if (method === 'cash') {
      const received = Number(cashReceived) || 0;
      if (received < total) return setError('Cash received cannot be less than the amount.');
      payload = { paymentType: 'Cash', cashAmount: total, onlineAmount: 0, walletAmount: 0, cashReceived: received };
    } else if (method === 'online') {
      payload = { paymentType: 'Online', cashAmount: 0, onlineAmount: total, walletAmount: 0, cashReceived: 0 };
    } else if (method === 'split') {
      const c = Number(splitCash) || 0, o = Number(splitOnline) || 0;
      if (Math.abs(c + o - total) > 0.01) return setError(`Cash + online must be ₹${total}.`);
      payload = { paymentType: 'Split', cashAmount: c, onlineAmount: o, walletAmount: 0, cashReceived: c };
    } else {
      const now = Number(paidNow) || 0;
      if (now < 0 || now > total) return setError('Paid now must be between ₹0 and the amount.');
      if (!name.trim() || !phone.trim()) return setError('Name and phone number are needed for credit.');
      payload = { paymentType: 'Cash', cashAmount: now, onlineAmount: 0, walletAmount: 0, cashReceived: now,
        creditAmount: total - now, customerName: name.trim(), customerPhone: phone.trim() };
    }
    setBusy(true);
    try {
      await processPayment(bill.id, payload);
      onPaid();
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Payment failed.');
      setBusy(false);
    }
  };

  const input = 'w-full bg-bg-3 border border-border text-text font-mono rounded-lg py-2 px-3 text-sm focus:border-accent outline-none';

  return (
    <div className="bg-bg-3 border border-border rounded-xl p-4">
      <div className="flex justify-between items-start mb-3">
        <div>
          <div className="text-text text-sm font-bold">{bill.customerName}</div>
          <div className="text-text-3 text-[11px] font-mono">{bill.billNumber}</div>
          <div className="text-text-2 text-xs mt-1">
            {(bill.items || []).map(i => `${i.quantity} × ${i.itemName.replace(' - missing in stock count', '')}`).join(', ')}
          </div>
        </div>
        <div className="text-neon-red font-mono font-bold text-xl">₹{total.toFixed(2)}</div>
      </div>

      <div className="grid grid-cols-4 gap-1.5 mb-3">
        {[['cash', 'Cash'], ['online', 'Online'], ['split', 'Split'], ['credit', 'Credit']].map(([id, label]) => (
          <button key={id} onClick={() => setMethod(id)}
            className={`py-2 rounded border text-[11px] font-bold uppercase tracking-wider ${method === id ? 'border-accent text-accent bg-accent/10' : 'border-border text-text-2'}`}>
            {label}
          </button>
        ))}
      </div>

      {method === 'cash' && (
        <label className="block text-[10px] text-text-3 uppercase font-bold">Cash received
          <input type="number" min="0" value={cashReceived} onChange={e => setCashReceived(e.target.value)} className={`${input} mt-1`} />
          {Number(cashReceived) > total && <span className="text-neon-orange normal-case text-xs">Return ₹{(Number(cashReceived) - total).toFixed(2)}</span>}
        </label>
      )}
      {method === 'online' && <p className="text-text-2 text-xs">₹{total.toFixed(2)} by UPI / online.</p>}
      {method === 'split' && (
        <div className="grid grid-cols-2 gap-2">
          <label className="text-[10px] text-text-3 uppercase font-bold">Cash<input type="number" min="0" value={splitCash} onChange={e => setSplitCash(e.target.value)} className={`${input} mt-1`} /></label>
          <label className="text-[10px] text-text-3 uppercase font-bold">Online<input type="number" min="0" value={splitOnline} onChange={e => setSplitOnline(e.target.value)} className={`${input} mt-1`} /></label>
        </div>
      )}
      {method === 'credit' && (
        <div className="space-y-2">
          <label className="block text-[10px] text-text-3 uppercase font-bold">Paid now (cash)
            <input type="number" min="0" value={paidNow} onChange={e => setPaidNow(e.target.value)} className={`${input} mt-1`} /></label>
          <label className="block text-[10px] text-text-3 uppercase font-bold">Name
            <input value={name} onChange={e => setName(e.target.value)} className={`${input} mt-1 font-sans`} /></label>
          <label className="block text-[10px] text-text-3 uppercase font-bold">Phone number
            <input value={phone} onChange={e => setPhone(e.target.value)} className={`${input} mt-1`} /></label>
          <p className="text-neon-orange text-xs">On credit: ₹{Math.max(0, total - (Number(paidNow) || 0)).toFixed(2)} - clear it on the Credits page.</p>
        </div>
      )}

      {error && <p className="text-neon-red text-xs mt-2">{error}</p>}
      <button onClick={pay} disabled={busy} className="btn-primary w-full py-3 mt-3">{busy ? 'Saving...' : 'Pay'}</button>
    </div>
  );
}
