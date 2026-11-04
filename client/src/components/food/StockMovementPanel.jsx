import { useCallback, useEffect, useState } from 'react';
import { PackagePlus, TrendingDown, AlertTriangle, Package, Boxes } from 'lucide-react';
import api from '../../config/api';

const n = (v) => Number(v || 0);
const rs = (v) => `₹${n(v).toFixed(2)}`;
const firstOfMonth = () => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1).toLocaleDateString('en-CA'); };
const today = () => new Date().toLocaleDateString('en-CA');

/**
 * The owner's question, answered on the page: "900 sold this month and the admin put 950 in -
 * where did 50 go?". Every movement is read from the stock history, so the four numbers always
 * reconcile: opening + refilled − sold + corrections = what is on the shelf.
 */
export default function StockMovementPanel({ branchId }) {
  const [from, setFrom] = useState(firstOfMonth);
  const [to, setTo] = useState(today);
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(false);
  const [open, setOpen] = useState(false);

  const load = useCallback(async () => {
    if (!branchId) return;
    setLoading(true);
    try {
      const res = await api.get('/inventory/stock-summary', {
        params: { branchId, from: `${from}T00:00:00+05:30`, to: `${to}T23:59:59+05:30` },
      });
      setData(res.data?.data || null);
    } catch {
      setData(null);
    } finally {
      setLoading(false);
    }
  }, [branchId, from, to]);

  useEffect(() => { load(); }, [load]);

  const t = data?.totals;
  const rows = (data?.items || []).filter(r => r.refilled || r.sold || r.corrections);

  const Box = ({ label, value, icon: Icon, tone, sub }) => (
    <div className="card bg-bg-2 border border-border p-4 rounded-xl flex flex-col justify-between">
      <span className="text-text-3 font-semibold uppercase text-[10px] tracking-wider flex items-center gap-1.5">
        <Icon className="w-3.5 h-3.5" /> {label}
      </span>
      <span className={`text-2xl font-bold font-mono mt-1 ${tone || 'text-text'}`}>{value}</span>
      {sub && <span className="text-[10px] text-text-3 mt-0.5">{sub}</span>}
    </div>
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="font-heading font-extrabold text-sm uppercase tracking-wider text-text">
          Stock in and out
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

      <div className="grid grid-cols-2 lg:grid-cols-5 gap-4">
        <Box label="Was already there" value={`${n(t?.opening)} units`} icon={Package} sub="left over from before" />
        <Box label="Admin added" value={`+${n(t?.refilled)} units`} icon={PackagePlus} tone="text-neon-green" sub="refilled in this range" />
        <Box label="Total available" value={`${n(t?.available)} units`} icon={Boxes} sub="already there + added" />
        <Box label="Sold" value={`−${n(t?.sold)} units`} icon={TrendingDown} tone="text-neon-blue" />
        <Box
          label="Missing"
          value={`${n(t?.missing)} units`}
          icon={AlertTriangle}
          tone={n(t?.missing) > 0 ? 'text-neon-red' : 'text-text-3'}
          sub={n(t?.missing) > 0 ? `worth ${rs(t?.missingValue)}` : 'nothing unaccounted for'}
        />
      </div>

      {t && (
        <p className="text-xs text-text-2">
          <b className="font-mono">{n(t.opening)}</b> were already there and the admin added
          <b className="font-mono text-neon-green"> {n(t.refilled)}</b>, so
          <b className="font-mono"> {n(t.available)}</b> were available.
          <b className="font-mono text-neon-blue"> {n(t.sold)}</b> sold, which leaves
          <b className="font-mono"> {n(t.available) - n(t.sold)}</b>
          {n(t.missing) > 0
            ? <> — but <b className="font-mono text-neon-red">{n(t.missing)}</b> could not be accounted for ({rs(t.missingValue)}).</>
            : <> and that is what the counts found.</>}
        </p>
      )}

      {data && !data.hasHistory && (
        <p className="text-xs text-text-3 italic">
          No stock history for this branch in this range yet. Refills and counts start reaching Head Office from this update onwards.
        </p>
      )}

      {(data?.wentMissing?.length || 0) > 0 && (
        <div className="card bg-bg-2 border border-neon-red/30 rounded-xl p-4">
          <div className="text-neon-red uppercase tracking-wider font-bold text-[10px] mb-2">
            When the stock went missing
          </div>
          <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
            <thead>
              <tr className="text-text-3 uppercase tracking-wider font-bold text-[10px]">
                <th className="py-1 pr-3">Date / time</th>
                <th className="py-1 pr-3">Item</th>
                <th className="py-1 pr-3 text-right">Should have been</th>
                <th className="py-1 pr-3 text-right">Found</th>
                <th className="py-1 pr-3 text-right">Missing</th>
                <th className="py-1 pr-3 text-right">Worth</th>
                <th className="py-1">Counted by</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/40 font-mono">
              {data.wentMissing.map((m, idx) => (
                <tr key={idx}>
                  <td className="py-1.5 pr-3 text-text-2">
                    {new Date(m.at).toLocaleString('en-IN', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', hour12: true })}
                  </td>
                  <td className="py-1.5 pr-3 text-text font-sans font-bold">{m.itemName}</td>
                  <td className="py-1.5 pr-3 text-right text-text-3">{m.was}</td>
                  <td className="py-1.5 pr-3 text-right text-text-3">{m.found}</td>
                  <td className="py-1.5 pr-3 text-right text-neon-red font-bold">{m.quantity}</td>
                  <td className="py-1.5 pr-3 text-right text-neon-red">{rs(m.value)}</td>
                  <td className="py-1.5 text-neon-blue font-sans">{m.countedBy || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <button onClick={() => setOpen(o => !o)} className="text-xs text-accent hover:underline">
        {open ? 'Hide the item breakdown' : 'Show the item breakdown'}
      </button>

      {open && (
        <div className="overflow-x-auto">
          {loading ? (
            <div className="text-center text-text-3 text-xs py-6">Loading…</div>
          ) : rows.length === 0 ? (
            <div className="text-center text-text-3 text-xs italic py-6 border border-dashed border-border rounded-lg">
              Nothing moved in this range.
            </div>
          ) : (
            <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
              <thead>
                <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                  <th className="py-2 px-3">Item</th>
                  <th className="py-2 px-3 text-right">Already there</th>
                  <th className="py-2 px-3 text-right">Admin added</th>
                  <th className="py-2 px-3 text-right">Available</th>
                  <th className="py-2 px-3 text-right">Sold</th>
                  <th className="py-2 px-3 text-right">Should be left</th>
                  <th className="py-2 px-3 text-right">On the shelf</th>
                  <th className="py-2 px-3 text-right">Difference</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border/40 font-mono">
                {rows.map(r => {
                  const diff = n(r.actual) - n(r.shouldBe);
                  return (
                    <tr key={r.inventoryId}>
                      <td className="py-2 px-3 text-text font-sans font-bold">{r.itemName}</td>
                      <td className="py-2 px-3 text-right text-text-2">{r.opening}</td>
                      <td className="py-2 px-3 text-right text-neon-green">+{r.refilled}</td>
                      <td className="py-2 px-3 text-right text-text-2">{n(r.opening) + n(r.refilled)}</td>
                      <td className="py-2 px-3 text-right text-neon-blue">−{r.sold}</td>
                      <td className="py-2 px-3 text-right text-text-2">{r.shouldBe}</td>
                      <td className="py-2 px-3 text-right text-text font-bold">{r.actual}</td>
                      <td className={`py-2 px-3 text-right font-bold ${diff === 0 ? 'text-text-3' : diff < 0 ? 'text-neon-red' : 'text-neon-orange'}`}>
                        {diff === 0 ? '0' : diff > 0 ? `+${diff}` : diff}
                        {diff < 0 && <span className="text-text-3 font-sans font-normal"> ({rs(Math.abs(diff) * n(r.price))})</span>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  );
}
