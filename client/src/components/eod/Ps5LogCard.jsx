import { useEffect, useState } from 'react';
import { Gamepad2, Pencil, X, Check } from 'lucide-react';
import { format } from 'date-fns';
import api from '../../config/api';
import { editPaymentMethod } from '../../api/billing.api';
import { tradingDayRangeIst } from '../../utils/timeUtils';
import { useAuth } from '../../contexts/AuthContext';
import { useToast } from '../ui/Toast';

const rs = (n) => `₹${Number(n || 0).toFixed(2)}`;

const playTime = (mins) => {
  if (!mins) return '—';
  const h = Math.floor(mins / 60), m = mins % 60;
  return h > 0 && m > 0 ? `${h} hr ${m} min` : h > 0 ? `${h} hr` : `${m} min`;
};

export default function Ps5LogCard({ branchId, pageFrom, pageTo }) {
  const [from, setFrom] = useState(pageFrom);
  const [to, setTo] = useState(pageTo);
  const [rows, setRows] = useState([]);
  const [loading, setLoading] = useState(false);
  const [editId, setEditId] = useState(null);
  const [editMethod, setEditMethod] = useState('cash');
  const [editReason, setEditReason] = useState('');
  const [editBusy, setEditBusy] = useState(false);
  const { canCorrectPaymentMethod } = useAuth();
  const toast = useToast();

  useEffect(() => { setFrom(pageFrom); setTo(pageTo); }, [pageFrom, pageTo]);

  const fetchRows = () => {
    if (!from || !to || to < from) return;
    setLoading(true);
    api.get('/eod/range-report', {
      params: { startDate: tradingDayRangeIst(from).startIso, endDate: tradingDayRangeIst(to).endIso, branchId },
    })
      .then(res => setRows(res.data?.data?.ps5Entries || []))
      .catch(() => setRows([]))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    let cancelled = false;
    if (!from || !to || to < from) return;
    setLoading(true);
    api.get('/eod/range-report', {
      params: { startDate: tradingDayRangeIst(from).startIso, endDate: tradingDayRangeIst(to).endIso, branchId },
    })
      .then(res => { if (!cancelled) setRows(res.data?.data?.ps5Entries || []); })
      .catch(() => { if (!cancelled) setRows([]); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [from, to, branchId]);

  const canEdit = (r) => {
    if (!canCorrectPaymentMethod()) return false;
    const m = (r.paymentType || '').toLowerCase();
    return m === 'cash' || m === 'online' || m === 'split';
  };

  const openEdit = (r) => {
    setEditId(r.billId);
    setEditMethod((r.paymentType || '').toLowerCase() === 'online' ? 'cash' : 'online');
    setEditReason('');
  };

  const saveEdit = async (r) => {
    if (!editReason.trim()) { toast.error('A reason is required'); return; }
    const total = Number(r.total) || 0;
    setEditBusy(true);
    try {
      await editPaymentMethod(r.billId, {
        newPaymentType: editMethod === 'cash' ? 'Cash' : 'Online',
        cashAmount: editMethod === 'cash' ? total : 0,
        onlineAmount: editMethod === 'online' ? total : 0,
        reason: editReason.trim(),
      });
      toast.success('Payment method corrected');
      setEditId(null);
      fetchRows();
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to correct payment method');
    } finally {
      setEditBusy(false);
    }
  };

  const sum = (k) => rows.reduce((t, r) => t + Number(r[k] || 0), 0);
  const label = from === to ? from : `${from} to ${to}`;

  return (
    <div className="card bg-bg-2 border border-border p-6 rounded-xl shadow-lg">
      <div className="flex flex-wrap justify-between items-center gap-3 mb-6">
        <h2 className="font-heading font-extrabold text-sm uppercase tracking-wider text-text flex items-center gap-2">
          <Gamepad2 className="w-4.5 h-4.5 text-accent" />
          PS5 Transaction Log ({label})
        </h2>
        <div className="flex items-center gap-2 text-xs">
          <span className="text-text-3 uppercase font-bold">From</span>
          <input type="date" value={from} max={to} onChange={e => setFrom(e.target.value)}
            className="bg-bg border border-border rounded px-2 py-1 text-text" />
          <span className="text-text-3 uppercase font-bold">To</span>
          <input type="date" value={to} min={from} onChange={e => setTo(e.target.value)}
            className="bg-bg border border-border rounded px-2 py-1 text-text" />
        </div>
      </div>

      <div className="overflow-x-auto">
        {loading ? (
          <div className="text-center text-text-3 text-xs py-8">Loading…</div>
        ) : rows.length === 0 ? (
          <div className="text-center text-text-3 text-xs italic py-8 border border-dashed border-border rounded-lg">
            No PS5 entries in this range.
          </div>
        ) : (
          <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
            <thead>
              <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                <th className="py-3 px-3">Date / Time</th>
                <th className="py-3 px-3">Person</th>
                <th className="py-3 px-3">Hours</th>
                <th className="py-3 px-3">Note</th>
                <th className="py-3 px-3 text-center">Payment</th>
                <th className="py-3 px-3 text-right">Cash</th>
                <th className="py-3 px-3 text-right">Online</th>
                <th className="py-3 px-3 text-right">Credit</th>
                <th className="py-3 px-3 text-right">Total</th>
                <th className="py-3 px-3">Operator</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/40 font-mono">
              {rows.map(r => {
                const isEditing = editId === r.billId;
                return (
                  <tr key={r.billNumber}>
                    <td className="py-2 px-3 text-text-2">{r.date ? format(new Date(r.date), 'MMM d, hh:mm a') : '-'}</td>
                    <td className="py-2 px-3 text-text font-sans font-bold">{r.customer}</td>
                    <td className="py-2 px-3 text-text-2">{playTime(r.playMinutes)}</td>
                    <td className="py-2 px-3 text-text-3 font-sans">{r.note || '-'}</td>
                    <td className="py-2 px-3 text-center text-text-2 font-sans">
                      {isEditing ? (
                        <div className="flex flex-col items-center gap-1">
                          <div className="flex gap-1">
                            {['cash', 'online'].map(m => (
                              <button key={m} type="button" onClick={() => setEditMethod(m)}
                                className={`px-2 py-0.5 rounded text-[10px] font-bold uppercase ${editMethod === m ? 'bg-accent text-bg' : 'bg-bg-3 text-text-3'}`}>
                                {m}
                              </button>
                            ))}
                          </div>
                          <input type="text" value={editReason} onChange={e => setEditReason(e.target.value)}
                            placeholder="Reason" className="w-28 bg-bg-3 border border-border rounded px-1.5 py-0.5 text-[10px] text-text" />
                          <div className="flex gap-1">
                            <button type="button" onClick={() => saveEdit(r)} disabled={editBusy}
                              className="p-0.5 text-neon-green hover:text-neon-green/80"><Check className="w-3.5 h-3.5" /></button>
                            <button type="button" onClick={() => setEditId(null)}
                              className="p-0.5 text-neon-red hover:text-neon-red/80"><X className="w-3.5 h-3.5" /></button>
                          </div>
                        </div>
                      ) : (
                        <span className="inline-flex items-center gap-1">
                          {r.paymentType}
                          {canEdit(r) && (
                            <button type="button" onClick={() => openEdit(r)} title="Change payment method"
                              className="p-0.5 text-text-3 hover:text-accent"><Pencil className="w-3 h-3" /></button>
                          )}
                        </span>
                      )}
                    </td>
                    <td className="py-2 px-3 text-right">{rs(r.cash)}</td>
                    <td className="py-2 px-3 text-right">{rs(r.online)}</td>
                    <td className="py-2 px-3 text-right">
                      {r.credit > 0 ? (
                        <span className={r.creditStatus === 'cleared' ? 'text-neon-green' : 'text-neon-orange'}>
                          {rs(r.credit)} {r.creditStatus === 'cleared' ? '(cleared)' : '(due)'}
                        </span>
                      ) : rs(0)}
                    </td>
                    <td className="py-2 px-3 text-right text-text font-bold">{rs(r.total)}</td>
                    <td className="py-2 px-3 text-neon-blue font-sans">{r.operator}</td>
                  </tr>
                );
              })}
              <tr className="border-t-2 border-border font-bold">
                <td className="py-2 px-3 font-sans">TOTAL ({rows.length})</td>
                <td className="py-2 px-3" />
                <td className="py-2 px-3">{playTime(rows.reduce((t, r) => t + Number(r.playMinutes || 0), 0))}</td>
                <td className="py-2 px-3" colSpan={2} />
                <td className="py-2 px-3 text-right">{rs(sum('cash'))}</td>
                <td className="py-2 px-3 text-right">{rs(sum('online'))}</td>
                <td className="py-2 px-3 text-right">{rs(sum('credit'))}</td>
                <td className="py-2 px-3 text-right">{rs(sum('total'))}</td>
                <td />
              </tr>
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
