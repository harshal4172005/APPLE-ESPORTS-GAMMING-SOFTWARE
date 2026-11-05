import { useState } from 'react';
import { Gamepad2, X } from 'lucide-react';
import api from '../../config/api';
import { generateIdempotencyKey } from '../../utils/idempotency';
import { useToast } from '../ui/Toast';
import BillDetailsPanel from '../billing/BillDetailsPanel';

/**
 * PS5 entry from the Cash Desk. Step 1 makes a PS5 bill (amount, customer, note); step 2 is the
 * Billing Counter's own payment panel for it - cash, online, split or credit - so the drawer,
 * End of Day and Credits treat it exactly like every other bill.
 */
export default function Ps5EntryModal({ onClose, onDone }) {
  const toast = useToast();
  const [amount, setAmount] = useState('');
  const [customerName, setCustomerName] = useState('');
  const [customerPhone, setCustomerPhone] = useState('');
  // Typed the way an operator says it - "1 hr", "1 hr 30 min", "30 min" - rather than 1.5.
  const [hours, setHours] = useState('');
  const [minutes, setMinutes] = useState('');
  const [note, setNote] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);
  const [bill, setBill] = useState(null);
  // One key per form, so a double click or a retry cannot create two bills.
  const [idemKey] = useState(generateIdempotencyKey);

  const value = Number(amount);
  const playMinutes = (Number(hours) || 0) * 60 + (Number(minutes) || 0);
  const valid = value > 0 && customerName.trim().length > 0 && playMinutes > 0 && playMinutes <= 1440;

  const create = async (e) => {
    e.preventDefault();
    if (!valid || saving) return;
    setSaving(true);
    setError(null);
    try {
      const res = await api.post('/bills/ps5', {
        amount: value,
        playMinutes,
        customerName: customerName.trim(),
        customerPhone: customerPhone.trim() || undefined,
        note: note.trim() || undefined,
      }, { headers: { 'X-Idempotency-Key': idemKey } });
      setBill({ ...res.data?.data, customerPhone: customerPhone.trim() });
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Could not create the PS5 entry.');
    } finally {
      setSaving(false);
    }
  };

  const refetchBill = async () => {
    try {
      const res = await api.get(`/bills/${bill.id}`);
      if (res.data?.data) setBill(b => ({ ...res.data.data, customerPhone: b?.customerPhone }));
    } catch { /* keep what is shown */ }
  };

  const close = () => {
    // Closed before paying: the bill waits on the Billing Counter like any unpaid bill.
    if (bill && bill.status !== 'Completed' && bill.status !== 'completed')
      toast.info(`${bill.billNumber} is waiting on the Billing Counter to be paid.`);
    onClose();
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/75 backdrop-blur-sm">
      <div className="w-full max-w-lg bg-bg-2 border border-border rounded-xl shadow-2xl overflow-hidden flex flex-col max-h-[96vh]">
        <div className="p-4 border-b border-border bg-bg-3 flex items-center justify-between shrink-0">
          <div className="flex items-center gap-2">
            <Gamepad2 className="w-5 h-5 text-accent" />
            <div>
              <h2 className="font-heading font-bold text-text uppercase tracking-wider text-sm leading-none">PS5 Entry</h2>
              {bill && <p className="text-[10px] text-text-3 mt-0.5 font-mono">{bill.billNumber} · {bill.customerName}</p>}
            </div>
          </div>
          <button onClick={close} className="p-1 rounded hover:bg-bg/50 text-text-3 hover:text-text"><X className="w-5 h-5" /></button>
        </div>

        {!bill ? (
          <form onSubmit={create} className="p-5 space-y-4">
            <label className="block">
              <span className="text-xs text-text-3 uppercase tracking-wider font-bold">Amount (₹)</span>
              <input
                type="number" min="1" step="1" inputMode="numeric" autoFocus
                value={amount} onChange={(e) => setAmount(e.target.value)}
                placeholder="e.g. 200"
                className="mt-1 w-full bg-bg border border-border rounded px-3 py-2.5 font-mono text-lg text-text outline-none focus:border-accent"
              />
            </label>
            <label className="block">
              <span className="text-xs text-text-3 uppercase tracking-wider font-bold">Person</span>
              <input
                value={customerName} onChange={(e) => setCustomerName(e.target.value)} maxLength={100}
                placeholder="Customer's name"
                className="mt-1 w-full bg-bg border border-border rounded px-3 py-2 text-text outline-none focus:border-accent"
              />
            </label>

            <div>
              <span className="text-xs text-text-3 uppercase tracking-wider font-bold">How long they played</span>
              <div className="mt-1 grid grid-cols-2 gap-3">
                <div className="flex items-center gap-2 bg-bg border border-border rounded px-3 focus-within:border-accent">
                  <input
                    type="number" min="0" max="24" step="1" inputMode="numeric"
                    value={hours} onChange={(e) => setHours(e.target.value)} placeholder="0"
                    className="w-full bg-transparent py-2 font-mono text-text outline-none"
                  />
                  <span className="text-text-3 text-xs shrink-0">hr</span>
                </div>
                <div className="flex items-center gap-2 bg-bg border border-border rounded px-3 focus-within:border-accent">
                  <input
                    type="number" min="0" max="59" step="1" inputMode="numeric"
                    value={minutes} onChange={(e) => setMinutes(e.target.value)} placeholder="0"
                    className="w-full bg-transparent py-2 font-mono text-text outline-none"
                  />
                  <span className="text-text-3 text-xs shrink-0">min</span>
                </div>
              </div>
              <span className="block mt-1 text-[11px] text-text-3">
                For 1 hour 30 minutes put 1 and 30. For half an hour leave hr empty and put 30.
                This is for your record only - the price is whatever you type above.
              </span>
            </div>
            <label className="block">
              <span className="text-xs text-text-3 uppercase tracking-wider font-bold">Phone (needed for credit)</span>
              <input
                value={customerPhone} onChange={(e) => setCustomerPhone(e.target.value)} maxLength={20} inputMode="tel"
                className="mt-1 w-full bg-bg border border-border rounded px-3 py-2 font-mono text-text outline-none focus:border-accent"
              />
            </label>
            <label className="block">
              <span className="text-xs text-text-3 uppercase tracking-wider font-bold">Note (optional)</span>
              <input
                value={note} onChange={(e) => setNote(e.target.value)} maxLength={120} placeholder="e.g. 2 players, 1 hour"
                className="mt-1 w-full bg-bg border border-border rounded px-3 py-2 text-text outline-none focus:border-accent"
              />
            </label>
            {error && <p className="text-sm text-neon-red">{error}</p>}
            <button
              type="submit" disabled={!valid || saving}
              className="btn-primary w-full py-3 disabled:opacity-50"
            >
              {saving ? 'Creating…' : valid ? `Next: take ₹${value} payment` : 'Fill amount, person and time'}
            </button>
          </form>
        ) : (
          <div className="overflow-y-auto">
            <BillDetailsPanel
              bill={bill}
              onBillUpdate={refetchBill}
              onPaymentSuccess={() => {
                toast.success(`PS5 entry ${bill.billNumber} saved.`);
                onDone?.();
                onClose();
              }}
            />
          </div>
        )}
      </div>
    </div>
  );
}
