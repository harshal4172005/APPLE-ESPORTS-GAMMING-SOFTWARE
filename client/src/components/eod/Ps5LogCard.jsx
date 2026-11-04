import { useEffect, useState } from 'react';
import { Gamepad2 } from 'lucide-react';
import { format } from 'date-fns';
import api from '../../config/api';
import { tradingDayRangeIst } from '../../utils/timeUtils';

const rs = (n) => `₹${Number(n || 0).toFixed(2)}`;

/**
 * PS5 entries made at the Cash Desk, with their own From/To (starting at the page's range).
 * Display only: these bills are already in every total on the page, like any other bill.
 */
export default function Ps5LogCard({ branchId, pageFrom, pageTo }) {
  const [from, setFrom] = useState(pageFrom);
  const [to, setTo] = useState(pageTo);
  const [rows, setRows] = useState([]);
  const [loading, setLoading] = useState(false);

  // Follow the page's range when it changes; the card's own pickers can then narrow it.
  useEffect(() => { setFrom(pageFrom); setTo(pageTo); }, [pageFrom, pageTo]);

  useEffect(() => {
    if (!from || !to || to < from) return;
    let cancelled = false;
    setLoading(true);
    api.get('/eod/range-report', {
      params: { startDate: tradingDayRangeIst(from).startIso, endDate: tradingDayRangeIst(to).endIso, branchId },
    })
      .then(res => { if (!cancelled) setRows(res.data?.data?.ps5Entries || []); })
      .catch(() => { if (!cancelled) setRows([]); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [from, to, branchId]);

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
                <th className="py-3 px-3">Customer</th>
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
              {rows.map(r => (
                <tr key={r.billNumber}>
                  <td className="py-2 px-3 text-text-2">{r.date ? format(new Date(r.date), 'MMM d, hh:mm a') : '-'}</td>
                  <td className="py-2 px-3 text-text font-sans font-bold">{r.customer}</td>
                  <td className="py-2 px-3 text-text-3 font-sans">{r.note || '-'}</td>
                  <td className="py-2 px-3 text-center text-text-2 font-sans">{r.paymentType}</td>
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
              ))}
              <tr className="border-t-2 border-border font-bold">
                <td className="py-2 px-3 font-sans" colSpan={4}>TOTAL ({rows.length})</td>
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
