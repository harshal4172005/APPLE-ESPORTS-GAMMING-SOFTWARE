import { useState } from 'react';
import { AlertTriangle } from 'lucide-react';

const n = (v) => Number(v || 0);
const rs = (v) => `₹${n(v).toFixed(2)}`;

/**
 * "What the operator sees": every rupee that came in today, from everywhere (server's
 * operatorBoxes). Tap a box to see what it is made of. Same Total math as EodPaymentSummaryBar.
 */
export default function OperatorBoxes({ report, showLabel }) {
  const [open, setOpen] = useState(null);
  const pm = report.paymentMethods;
  const ob = report.operatorBoxes;

  // Older server without operatorBoxes: the figures exactly as before.
  const cash = ob ? n(ob.cash) : n(pm.totalCash) + n(pm.totalWalletTopUpsCash);
  const online = ob ? n(ob.online) : n(pm.totalOnline) + n(pm.totalWalletTopUpsOnline);
  const food = ob ? n(ob.food) : n(report.revenue.totalFoodRevenue);
  const total = cash + online
    - n(report.cash.totalPettyExpenses) + n(report.cash.totalCashInwards)
    - n(report.cash.totalOwnerWithdrawals);

  const parts = ob && {
    total: [
      ['Cash', cash], ['Online', online],
      ['Cash in (Add Entry)', n(report.cash.totalCashInwards)],
      ['Expenses', -n(report.cash.totalPettyExpenses)],
      ['Owner takeout', -n(report.cash.totalOwnerWithdrawals)],
    ],
    cash: [
      ['Bills (incl. PS5)', ob.cashFromBills], ['Credits paid off', ob.cashFromCreditsCleared],
      ['Member top-ups', ob.cashFromTopUps], ['Reservation deposits', ob.cashFromReservationDeposits],
    ],
    online: [
      ['Bills (incl. PS5)', ob.onlineFromBills], ['Credits paid off', ob.onlineFromCreditsCleared],
      ['Member top-ups', ob.onlineFromTopUps], ['Reservation deposits', ob.onlineFromReservationDeposits],
    ],
    food: [['Paid in cash', ob.foodCash], ['Paid online', ob.foodOnline]],
    ps5: [['Paid in cash', ob.ps5Cash], ['Paid online', ob.ps5Online]],
    gaming: [['Paid in cash', ob.gamingCash], ['Paid online', ob.gamingOnline]],
  };

  const Box = ({ id, label, value, accent, sub }) => (
    <button
      type="button"
      onClick={() => parts && setOpen(open === id ? null : id)}
      className={`text-left bg-bg-2 p-5 rounded-xl border shadow-lg ${open === id ? 'border-accent' : 'border-border'} ${parts ? 'cursor-pointer hover:border-accent/60' : 'cursor-default'}`}
    >
      <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">{label}</div>
      <div className={`text-2xl font-mono font-bold ${accent ? 'text-accent' : 'text-text'}`}>{rs(value)}</div>
      {sub && <div className="text-[11px] text-text-3 mt-1 font-mono">{sub}</div>}
      {open === id && parts && (
        <div className="mt-3 pt-3 border-t border-border space-y-1 text-xs">
          {parts[id].filter(([, v]) => n(v) !== 0 || id === 'food').map(([k, v]) => (
            <div key={k} className="flex justify-between gap-3">
              <span className="text-text-3">{k}</span>
              <span className="font-mono text-text-2">{n(v) < 0 ? `−${rs(-n(v))}` : rs(v)}</span>
            </div>
          ))}
        </div>
      )}
    </button>
  );

  return (
    <div>
      {showLabel && (
        <div className="text-text-3 text-[11px] uppercase font-bold tracking-widest mb-2">What the operator sees</div>
      )}
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4 items-start">
        <Box id="total" label="Total" value={total} accent />
        <Box id="cash" label="Cash" value={cash} />
        <Box id="online" label="Online" value={online} />
        <Box id="food" label="Food" value={food}
          sub={ob ? `cash ${rs(ob.foodCash)} + online ${rs(ob.foodOnline)}` : undefined} />
        <Box id="ps5" label="PS5" value={n(ob?.ps5)}
          sub={ob ? `cash ${rs(ob.ps5Cash)} + online ${rs(ob.ps5Online)}` : undefined} />
        <Box id="gaming" label="Gaming" value={n(ob?.gaming)}
          sub={ob ? `cash ${rs(ob.gamingCash)} + online ${rs(ob.gamingOnline)}` : undefined} />
      </div>
      {ob && ob.paymentMethodEdits > 0 && (
        <div className="mt-2 flex items-center gap-2 text-xs text-neon-orange">
          <AlertTriangle className="w-3.5 h-3.5 shrink-0" />
          {ob.paymentMethodEdits} payment-method change{ob.paymentMethodEdits > 1 ? 's' : ''} today
          {ob.paymentMethodEditsNotInDrawer > 0 && ` (${ob.paymentMethodEditsNotInDrawer} after the drawer was closed — the counted drawer will not match Cash by that much)`}
        </div>
      )}
      {parts && <div className="mt-1 text-[10px] text-text-3">Tap a box to see what it is made of. Food, PS5 and Gaming are already inside Cash and Online.</div>}
    </div>
  );
}
