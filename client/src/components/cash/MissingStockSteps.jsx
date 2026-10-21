import { useState } from 'react';
import { PackageSearch, Receipt } from 'lucide-react';
import api from '../../config/api';
import { processPayment } from '../../api/billing.api';

const itemName = (i) => i.itemName || i.ItemName || i.name;
const systemCount = (i) => Number(i.currentStock ?? i.CurrentStock ?? 0);
const countedValue = (i, counted) => {
  const raw = counted[i.id];
  const n = Number(raw);
  return raw === undefined || raw === '' || Number.isNaN(n) ? systemCount(i) : n;
};

/**
 * Shift end, step 1 when the branch charges for missing stock (Branch.ChargeMissingStock):
 * count the stock while the register is still open. Anything missing is shown with its menu
 * price first - Recount or Confirm - and only a confirmed count raises the bill.
 */
export function MissingStockCount({ inventory, registerId, onDone }) {
  const [counted, setCounted] = useState(() =>
    Object.fromEntries((inventory || []).map(i => [i.id, String(systemCount(i))])));
  const [review, setReview] = useState(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const missingLines = () => (inventory || [])
    .map(i => ({ item: i, missing: systemCount(i) - countedValue(i, counted) }))
    .filter(l => l.missing > 0);

  const save = async () => {
    setBusy(true);
    setError('');
    try {
      await api.post('/inventory/stock-count', {
        kind: 'closing',
        cashRegisterId: registerId,
        counts: (inventory || []).map(i => ({ inventoryId: i.id, counted: countedValue(i, counted) })),
      });
      onDone();
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

  const total = (review || []).reduce((s, l) => s + l.missing * Number(l.item.price ?? l.item.Price ?? 0), 0);

  return (
    <div className="flex-1 flex flex-col border border-border bg-bg-2 rounded-xl p-6">
      <div className="flex items-center gap-2 mb-1">
        <PackageSearch className="w-5 h-5 text-accent" />
        <h2 className="text-lg font-heading font-bold uppercase tracking-widest text-text">Step 1 · Count the stock</h2>
      </div>
      <p className="text-text-3 text-xs mb-4 leading-relaxed">
        Count what is actually on the shelf. Anything missing is billed to you at menu price and paid before the register is locked.
      </p>

      {(inventory || []).length === 0 ? (
        <div className="text-text-3 text-xs mb-4">Nothing is stocked at this branch, so there is nothing to count.</div>
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
              {inventory.map(i => {
                const differs = countedValue(i, counted) !== systemCount(i);
                return (
                  <tr key={i.id} className="border-t border-border/60">
                    <td className="py-2 text-text-2">{itemName(i)}</td>
                    <td className="py-2 text-right font-mono text-text-3">{systemCount(i)}</td>
                    <td className="py-2 text-right">
                      <input
                        type="number" min="0"
                        value={counted[i.id] ?? ''}
                        disabled={!!review || busy}
                        onChange={e => setCounted({ ...counted, [i.id]: e.target.value })}
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
            <div key={l.item.id} className="flex justify-between text-xs font-mono text-text-2">
              <span>{itemName(l.item)}: system {systemCount(l.item)}, counted {countedValue(l.item, counted)}</span>
              <span className="text-neon-red font-bold">{l.missing} missing × ₹{Number(l.item.price ?? l.item.Price ?? 0)}</span>
            </div>
          ))}
          <div className="flex justify-between border-t border-neon-red/30 mt-2 pt-2 text-sm font-bold text-neon-red">
            <span>To pay</span><span className="font-mono">₹{total.toFixed(2)}</span>
          </div>
        </div>
      )}

      {error && <p className="text-neon-red text-xs mb-3">{error}</p>}

      {review ? (
        <div className="grid grid-cols-2 gap-3">
          <button onClick={() => setReview(null)} disabled={busy} className="btn-secondary py-3">Recount</button>
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
