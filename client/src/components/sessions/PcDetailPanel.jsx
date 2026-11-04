import { useState, useEffect, useCallback } from 'react';
import { Monitor, User, Clock, Wrench, AlertTriangle, Square, RefreshCw, Receipt, Coffee, Gift, Banknote, X, Power, Keyboard, Mouse, Repeat } from 'lucide-react';
import api from '../../config/api';
import { useAuth } from '../../contexts/AuthContext';
import { useNavigate } from 'react-router-dom';
import { useToast } from '../ui/Toast';
import StartSessionForm from './StartSessionForm';
import ExtendSessionModal from './ExtendSessionModal';
import ChangePackageModal from './ChangePackageModal';
import SessionDiscountModal from './SessionDiscountModal';
import { formatMoney } from '../../utils/money';
import { computeRoundedBreakdown } from '../../utils/billRounding';
import { logActivity } from '../../utils/sessionLog';
import { getSessionActivities } from '../../api/sessions.api';

function useElapsedTime(startTimeIso) {
  const [elapsed, setElapsed] = useState({ h: 0, m: 0, s: 0, totalMin: 0 });

  useEffect(() => {
    if (!startTimeIso) return;
    const update = () => {
      const diffMs = Date.now() - new Date(startTimeIso).getTime();
      const totalSec = Math.max(0, Math.floor(diffMs / 1000));
      const h = Math.floor(totalSec / 3600);
      const m = Math.floor((totalSec % 3600) / 60);
      const s = totalSec % 60;
      setElapsed({ h, m, s, totalMin: totalSec / 60 });
    };
    update();
    const id = setInterval(update, 1000);
    return () => clearInterval(id);
  }, [startTimeIso]);

  return elapsed;
}

function fmtElapsed(h, m, s) {
  if (h > 0) return `${h}h ${m}m ${s}s`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

function fmtTime(isoString) {
  if (!isoString) return '';
  const d = new Date(isoString);
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true });
}

const STATUS_STYLES = {
  Idle:            { text: 'text-pc-idle',     border: 'border-pc-idle/40',     label: 'FREE' },
  Active:          { text: 'text-pc-active',   border: 'border-pc-active/50',   label: 'OCCUPIED' },
  // pc-awaiting, matching the tile. Sharing neon-orange with "time finished" made the one state
  // that owes money look like the one that does not.
  AwaitingBilling: { text: 'text-pc-awaiting', border: 'border-pc-awaiting/50', label: 'BILLING' },
  UnderMaintenance:{ text: 'text-pc-offline',  border: 'border-pc-offline/30',  label: 'MAINT' },
  Expired:         { text: 'text-text-3',      border: 'border-border',        label: 'EXPIRED' },
  // A PC record no physical machine has ever claimed - matches PcTile.jsx's own AwaitingSetup
  // entry and the same pc-awaitingsetup token, so this state reads the same way in both places
  // an operator can see it.
  AwaitingSetup:   { text: 'text-pc-awaitingsetup', border: 'border-pc-awaitingsetup/50', label: 'NOT SET UP' },
};
const DEFAULT_STYLE = { text: 'text-text-3', border: 'border-border', label: 'OFFLINE' };
const PENDING_STYLE = { text: 'text-accent', border: 'border-accent/50', label: 'WALK-IN PENDING' };

// PC was shut down (pc.poweredOff) while a session was still open on it - the clock is still
// billing but the machine has actually powered off. Kept distinct from the plain offline style
// above for the same reason as the tile: an operator needs to see at a glance that this one
// still owes money. Reuses neon-orange, the same token PcTile.jsx's Expired state uses.
const SHUTDOWN_BILLING_STYLE = { text: 'text-neon-orange', border: 'border-neon-orange/60', label: 'OFF - BILLING' };

// ── Persistent detail panel — shows placeholder when nothing is selected,
// otherwise renders the form/info/actions appropriate to that PC's state ──
export default function PcDetailPanel({
  pc, walkinReq, onClose, onRefresh,
  onApproveWalkin, onDeclineWalkin,
  onFlagMaintenance, onCreditClick, onShutdown,
}) {
  const { user, canApplyDiscount } = useAuth();
  const navigate = useNavigate();
  const toast = useToast();
  const elapsed = useElapsedTime(pc?.sessionStartTime);
  const [activities, setActivities] = useState([]);
  const [loadingActivities, setLoadingActivities] = useState(false);


  useEffect(() => {
    if (pc?.activeSessionId) {
      setLoadingActivities(true);
      getSessionActivities(pc.activeSessionId)
        .then(data => setActivities(data || []))
        .catch(err => {
          console.error('Failed to fetch session activities:', err);
          setActivities([]);
        })
        .finally(() => setLoadingActivities(false));
    } else {
      setActivities([]);
    }
  }, [pc?.activeSessionId]);

  const [actionLoading, setActionLoading] = useState(null);
  const [showExtendModal, setShowExtendModal] = useState(false);
  const [showPackageModal, setShowPackageModal] = useState(false);
  const [showDiscountModal, setShowDiscountModal] = useState(false);

  const bufferMinutes = pc?.bufferMinutes ?? 10;
  const rawGamingCharge = elapsed.totalMin <= bufferMinutes
    ? 0
    : Number((Math.max(elapsed.totalMin / 60, 1 / 60) * (pc?.ratePerHour || 0)).toFixed(2));
  const { displayGaming: gamingCharge, roundedTotal: liveCharge } = computeRoundedBreakdown(
    rawGamingCharge, pc?.foodAmount || 0
  );

  const remainingMs = pc?.sessionEndTime ? new Date(pc.sessionEndTime).getTime() - Date.now() : 0;
  const remainingTotalSec = Math.max(0, Math.floor(remainingMs / 1000));
  const remaining = {
    h: Math.floor(remainingTotalSec / 3600),
    m: Math.floor((remainingTotalSec % 3600) / 60),
    s: remainingTotalSec % 60,
  };

  const doAction = useCallback(async (action, payload = {}, loadingKey = null) => {
    if (!pc) return;
    const key = loadingKey || action;
    setActionLoading(key);
    try {
      await api.post(`/sessions/${pc.activeSessionId}/${action}`, payload);
      if (payload.deferPayment) {
        toast.success('Session stopped. Bill moved to Review Billing — PC is now free.');
        logActivity(`${pc.name}: Session stopped. [ Usage: ₹${formatMoney(liveCharge)}, Total: ₹${formatMoney(liveCharge)} ] Bill moved to Review Billing.`, 'warn');
      } else if (action === 'stop') {
        toast.success(`Session successfully ${action}ed!`);
        logActivity(`${pc.name}: Session closed. [ Usage: ₹${formatMoney(liveCharge)}, Total: ₹${formatMoney(liveCharge)} ]`, 'error');
      } else {
        toast.success(`Session successfully ${action}ed!`);
        logActivity(`${pc.name}: Session ${action}ed.`, 'success');
      }
      onRefresh?.();
    } catch (err) {
      console.error(`Action ${action} failed:`, err);
      toast.error(`Failed to ${action} session.`);
      logActivity(`${pc.name}: Failed to ${action} session.`, 'error');
    } finally {
      setActionLoading(null);
    }
  }, [pc, onRefresh, toast, liveCharge]);

  if (!pc) {
    return (
      <div className="rounded-lg border border-border bg-bg-2 flex flex-col items-center justify-center gap-2 py-16 px-4 text-center">
        <Monitor className="w-8 h-8 text-text-3" />
        <p className="text-text-3 text-xs font-mono">Select a PC to view details.</p>
        <p className="text-text-3/70 text-[10px] font-mono">Double-click an idle PC to quick-start a session.</p>
      </div>
    );
  }

  // A reservation no longer gets its own section, badge or Start Session/Override flow here -
  // a PC is Idle, Occupied or Under Maintenance, full stop. The backend still tracks a Reserved
  // state internally (see ReservationService), but on this screen it reads and behaves exactly
  // like Idle: the normal Start Session form, the normal Flag for Maintenance/Shut Down actions,
  // nothing reservation-specific. See the matching fix in PcTile.jsx.
  const displayState = pc.state === 'Reserved' ? 'Idle' : pc.state;

  // pc.poweredOff means PcStatusHub's shutdown command was sent and the PC has not reconnected
  // since (see backend Pc.PoweredOff). Combined with state here purely to pick the header
  // badge's colour/label - none of the body sections below key off this, only off displayState,
  // so a shut-down PC with a session still open on it keeps showing that session's normal details.
  const hasOpenSession = displayState === 'Active' || displayState === 'AwaitingBilling';

  // A PC still AwaitingSetup has never been claimed by a real machine, so poweredOff being true
  // on one is stale leftover state from before the backend guarded shutdown commands against
  // this exact case - never a real signal to act on. See the matching exclusion in PcTile.jsx.
  const neverClaimed = displayState === 'AwaitingSetup';
  const isShutDownWhileBilling = pc.poweredOff && hasOpenSession && !neverClaimed;
  const isShutDownIdle = pc.poweredOff && !hasOpenSession && !neverClaimed;

  const style = walkinReq
    ? PENDING_STYLE
    : isShutDownWhileBilling
      ? SHUTDOWN_BILLING_STYLE
      : isShutDownIdle
        ? DEFAULT_STYLE
        : (STATUS_STYLES[displayState] || DEFAULT_STYLE);
  const isActive = displayState === 'Active';
  const isAwaiting = displayState === 'AwaitingBilling';
  const isExpired = displayState === 'Expired';
  const isMaintenance = displayState === 'UnderMaintenance';
  const canStart = !walkinReq && displayState === 'Idle';

  // What this session is billed on - same rule as PcTile.jsx's isPayAsYouGo/hasPlanTime: an
  // open session with no end time is billed as time elapses (Pay-As-You-Go), one with an end
  // time was a fixed pre-purchased duration. Shown for both Active and AwaitingBilling, since
  // a session that finished still had one or the other - only an idle/offline/maintenance PC
  // genuinely has no plan to show an icon for.
  const hasSessionPlan = hasOpenSession && !!pc.activeSessionId;
  const isPayAsYouGoPlan = hasSessionPlan && !pc.sessionEndTime;
  const isFixedPlan = hasSessionPlan && !!pc.sessionEndTime;

  const PlanIcon = () => (
    isPayAsYouGoPlan ? (
      <span className="flex items-center gap-0.5 text-pc-active" title="Pay-As-You-Go">
        <Keyboard className="w-3 h-3" strokeWidth={2.5} />
        <Mouse className="w-3 h-3" strokeWidth={2.5} />
      </span>
    ) : isFixedPlan ? (
      <span className="text-neon-orange" title="Limited Time Plan">
        <Clock className="w-3 h-3" strokeWidth={2.5} />
      </span>
    ) : null
  );

  return (
    <div className={`rounded-lg border ${style.border} bg-bg-2 flex flex-col overflow-hidden`}>
      {/* Header */}
      <div className="px-4 py-3 border-b border-border bg-bg-3 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <Monitor className={`w-4 h-4 flex-shrink-0 ${style.text}`} />
          <span className="font-heading font-bold text-text text-sm tracking-wider truncate">{pc.name}</span>
        </div>
        <div className="flex items-center gap-2 flex-shrink-0">
          <span className={`text-[9px] font-mono font-bold px-1.5 py-0.5 border rounded ${style.border} ${style.text} ${walkinReq || isAwaiting ? 'animate-pulse' : ''}`}>
            {style.label}
          </span>
          {onClose && (
            <button onClick={onClose} className="p-0.5 text-text-3 hover:text-text rounded transition-colors">
              <X className="w-4 h-4" />
            </button>
          )}
        </div>
      </div>

      <div className="p-4 space-y-3 overflow-y-auto">
        {/* App version - the program itself, and the one the Updates page's "N of M up to date"
            count is actually judged against. The one place an operator can check "has this PC
            taken the update yet" instead of trusting the branch-wide count alone. Consoles run
            the same app, so this is shown for them too, unlike the agent version below. */}
        <div className="flex justify-between items-center text-[10px] font-mono text-text-3">
          <span>App version</span>
          <span className={pc.appVersion ? 'text-text-2' : 'italic'}>
            {pc.appVersion || 'not reported yet'}
          </span>
        </div>

        {/* Agent version - the separate screen-lock agent (AppleEsportsAgent.exe), which
            self-updates on its own schedule and can lag behind the app above. Consoles have no
            agent at all (see PcsController.Create's isConsole handling), so this is skipped for
            them rather than showing a permanent, meaningless "not reported yet". */}
        {pc.zone !== 'Console' && (
          <div className="flex justify-between items-center text-[10px] font-mono text-text-3">
            <span>Agent version</span>
            <span className={pc.agentVersion ? 'text-text-2' : 'italic'}>
              {pc.agentVersion || 'not reported yet'}
            </span>
          </div>
        )}

        {/* ── PENDING WALK-IN ── */}
        {walkinReq && (
          <>
            <div className="flex items-center gap-1.5 text-accent text-xs">
              <User className="w-3.5 h-3.5" />
              <span className="font-semibold">{walkinReq.customerName}</span>
            </div>
            <div className="flex justify-between items-center text-[10px] font-mono">
              <span className="text-text-3">Duration: {walkinReq.duration / 60} Hr</span>
              <span className="text-text">Amt: ₹{(walkinReq.duration / 60) * 100}</span>
            </div>
            <div className="grid grid-cols-2 gap-1.5">
              <button
                onClick={() => onDeclineWalkin?.(walkinReq)}
                className="py-1.5 rounded border border-border bg-bg-3 text-text-2 text-[10px] font-bold uppercase tracking-wider hover:bg-bg-3/80 transition-colors"
              >
                Decline
              </button>
              <button
                onClick={() => onApproveWalkin?.(walkinReq)}
                className="py-1.5 rounded border border-accent/40 bg-accent text-white text-[10px] font-bold uppercase tracking-wider hover:bg-accent-dark transition-colors"
              >
                Approve
              </button>
            </div>
          </>
        )}

        {/* ── START SESSION FORM (Idle / Offline) ── */}
        {!walkinReq && canStart && (
          <StartSessionForm pc={pc} onSuccess={onRefresh} />
        )}

        {/* ── OCCUPIED (Active) ── */}
        {!walkinReq && isActive && (
          <>
            {pc.hasOverrunWarning && (
              <div className="flex items-center gap-1.5 bg-neon-orange/15 border border-neon-orange/30 rounded p-1.5 text-[10px] text-neon-orange animate-pulse">
                <AlertTriangle className="w-3.5 h-3.5 flex-shrink-0" />
                <span className="leading-tight">{pc.overrunWarningMessage || 'Session overrun warning'}</span>
              </div>
            )}

            <div className="flex items-center gap-1.5 text-text-2 text-xs">
              <User className="w-3.5 h-3.5 text-text-3" />
              <span>{pc.customerName || pc.customerType || 'Walk-in'}</span>
              <PlanIcon />
            </div>

            <div className={`grid ${pc.sessionEndTime ? 'grid-cols-3' : 'grid-cols-2'} gap-2 bg-bg-3 rounded p-2.5 border border-border`}>
              <div>
                <div className="text-[9px] text-text-3 font-mono uppercase tracking-widest mb-0.5">
                  {pc.sessionEndTime ? 'Ends At' : 'Elapsed'}
                </div>
                <div className="font-mono font-bold text-pc-active text-sm">
                  {pc.sessionEndTime ? fmtTime(pc.sessionEndTime) : fmtElapsed(elapsed.h, elapsed.m, elapsed.s)}
                </div>
              </div>
              {pc.sessionEndTime && (
                <div>
                  <div className="text-[9px] text-text-3 font-mono uppercase tracking-widest mb-0.5">Remaining</div>
                  <div className={`font-mono font-bold text-sm ${remainingMs <= 0 ? 'text-neon-red' : 'text-pc-active'}`}>
                    {remainingMs <= 0 ? 'Overdue' : fmtElapsed(remaining.h, remaining.m, remaining.s)}
                  </div>
                </div>
              )}
              <div>
                <div className="text-[9px] text-text-3 font-mono uppercase tracking-widest mb-0.5">Live Charge</div>
                <div className="font-mono font-bold text-neon-orange text-sm">
                  ₹{formatMoney(liveCharge)}
                </div>
              </div>
            </div>

            <div className={`grid ${pc.sessionEndTime ? 'grid-cols-3' : 'grid-cols-2'} gap-1.5`}>
              <ActionBtn
                color="red"
                icon={<Square className="w-3 h-3" />}
                label="Stop"
                loading={actionLoading === 'stop'}
                onClick={() => doAction('stop', { deferPayment: false })}
              />
              <ActionBtn
                color="yellow"
                icon={<Banknote className="w-3 h-3" />}
                label="Credit"
                loading={actionLoading === 'credit'}
                onClick={async () => {
                  setActionLoading('credit');
                  try {
                    await onCreditClick?.(pc);
                  } finally {
                    setActionLoading(null);
                  }
                }}
              />
              {pc.sessionEndTime && (
                <ActionBtn
                  color="blue"
                  icon={<RefreshCw className="w-3 h-3" />}
                  label="Extend"
                  onClick={() => setShowExtendModal(true)}
                />
              )}
              {pc.sessionEndTime && (
                <ActionBtn
                  color="blue"
                  icon={<Repeat className="w-3 h-3" />}
                  label="Change Package"
                  onClick={() => setShowPackageModal(true)}
                />
              )}
            </div>

            <div className={`grid ${(user?.role === 'super_admin' || (user?.role === 'admin' && user?.dashboardPermissions?.discount === true)) ? 'grid-cols-2' : 'grid-cols-1'} gap-1.5`}>
              <ActionBtn
                color="green"
                icon={<Coffee className="w-3 h-3" />}
                label="Food"
                onClick={() => navigate('/app/food-orders', { state: { autoSelectPcId: pc.id } })}
                small
              />
              {/* Same rule as the billing counter's discount chips and as the server's
                  own check, expressed once in AuthContext rather than copied per screen. */}
              {canApplyDiscount() ? (
                <ActionBtn
                  color="purple"
                  icon={<Gift className="w-3 h-3" />}
                  label="Discount"
                  onClick={() => setShowDiscountModal(true)}
                  small
                />
              ) : null}
            </div>

            {showPackageModal && (
              <ChangePackageModal
                pc={pc}
                onClose={() => setShowPackageModal(false)}
                onDone={() => onRefresh?.()}
              />
            )}
            {showExtendModal && (
              <ExtendSessionModal
                pc={pc}
                onClose={() => setShowExtendModal(false)}
                onActionSuccess={() => {
                  setShowExtendModal(false);
                  onRefresh?.();
                }}
              />
            )}
            <SessionDiscountModal
              isOpen={showDiscountModal}
              onClose={() => setShowDiscountModal(false)}
              pc={pc}
              onRefresh={onRefresh}
            />

            {/* Session Activities Log */}
            <div className="border-t border-border pt-3">
              <div className="text-[9px] font-mono font-bold uppercase tracking-widest text-text-3 mb-2">Session Activity Log</div>
              <div className="bg-bg-3 rounded border border-border/50 max-h-32 overflow-y-auto p-2 text-[10px] font-mono space-y-1">
                {loadingActivities ? (
                  <div className="text-text-3 italic">Loading...</div>
                ) : activities.length === 0 ? (
                  <div className="text-text-3 italic">No activities yet</div>
                ) : (
                  activities.map((act, i) => (
                    <div key={i} className="text-text-2 leading-relaxed">
                      <span className="text-text-3">{new Date(act.timestamp).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</span>
                      <span className="text-text-3 mx-1">→</span>
                      <span>{act.description}</span>
                      {act.amount && <span className="text-neon-orange ml-1">(₹{act.amount})</span>}
                    </div>
                  ))
                )}
              </div>
            </div>
          </>
        )}

        {/* ── AWAITING BILLING ── */}
        {!walkinReq && isAwaiting && (
          <>
            <div className="flex items-center gap-1.5 text-neon-orange text-xs">
              <AlertTriangle className="w-3.5 h-3.5" />
              <span>{pc.customerName || 'Awaiting checkout'}</span>
              <PlanIcon />
            </div>
            <div className="text-[10px] text-text-3 font-mono text-center py-1">Pending at billing counter</div>
            <ActionBtn
              color="orange"
              icon={<Receipt className="w-3.5 h-3.5" />}
              label="Go to Billing"
              onClick={() => navigate('/app/billing', { state: { autoSelectPcId: pc.id } })}
            />
          </>
        )}

        {/* ── EXPIRED ── */}
        {!walkinReq && isExpired && (
          <div className="flex items-center gap-1.5 text-text-3 text-xs">
            <Clock className="w-3.5 h-3.5" />
            <span>Expired Reservation</span>
          </div>
        )}

        {/* ── MAINTENANCE ── */}
        {!walkinReq && isMaintenance && (
          <>
            <div className="flex items-center gap-1.5 text-pc-offline text-xs">
              <Wrench className="w-3.5 h-3.5" />
              <span>Under maintenance</span>
            </div>
            <button
              onClick={() => onFlagMaintenance?.(pc, false)}
              className="w-full py-1.5 rounded border border-pc-active/40 bg-pc-active/10 text-pc-active text-[11px] font-bold uppercase tracking-widest hover:bg-pc-active/20 transition-colors flex items-center justify-center gap-1"
            >
              <RefreshCw className="w-3 h-3" /> RESTORE PC
            </button>
          </>
        )}

        {/* ── Maintenance toggle button for a free/idle PC ── */}
        {!walkinReq && canStart && (
          <button
            onClick={() => onFlagMaintenance?.(pc, true)}
            title="Flag for Maintenance"
            className="w-full py-1.5 rounded border border-pc-offline/40 bg-pc-offline/10 text-pc-offline hover:bg-pc-offline/20 transition-colors flex items-center justify-center gap-1.5 text-[10px] font-bold uppercase tracking-wider"
          >
            <Wrench className="w-3.5 h-3.5" /> Flag for Maintenance
          </button>
        )}

        {/* Offered for a free PC only, deliberately. Cutting the power out from under a paying
            customer is not a shortcut worth having on a button - stop and bill the session
            first, then this. The bulk shutdown skips busy machines for the same reason. */}
        {!walkinReq && canStart && onShutdown && (
          <button
            onClick={() => onShutdown(pc)}
            title="Shut this PC down"
            className="w-full py-1.5 rounded border border-neon-red/40 bg-neon-red/10 text-neon-red hover:bg-neon-red/20 transition-colors flex items-center justify-center gap-1.5 text-[10px] font-bold uppercase tracking-wider"
          >
            <Power className="w-3.5 h-3.5" /> Shut Down PC
          </button>
        )}

        {/* ── OFFLINE fallback (non-Idle, non-mapped states) ── */}
        {!walkinReq && !canStart && !isActive && !isAwaiting && !isExpired && !isMaintenance && (
          <button
            onClick={() => onFlagMaintenance?.(pc, false)}
            className="w-full py-1.5 rounded border border-pc-active/40 bg-pc-active/10 text-pc-active text-[11px] font-bold uppercase tracking-widest hover:bg-pc-active/20 transition-colors flex items-center justify-center gap-1"
          >
            <RefreshCw className="w-3 h-3" /> RESTORE PC
          </button>
        )}
      </div>
    </div>
  );
}

function ActionBtn({ color, icon, label, onClick, loading, small = false }) {
  const colorMap = {
    red:    'border-neon-red/40    bg-neon-red/10    text-neon-red    hover:bg-neon-red/20',
    blue:   'border-neon-blue/40   bg-neon-blue/10   text-neon-blue   hover:bg-neon-blue/20',
    orange: 'border-neon-orange/40 bg-neon-orange/10 text-neon-orange hover:bg-neon-orange/20',
    yellow: 'border-pc-reserved/40 bg-pc-reserved/10 text-pc-reserved hover:bg-pc-reserved/20',
    green:  'border-pc-active/40   bg-pc-active/10   text-pc-active   hover:bg-pc-active/20',
    purple: 'border-neon-purple/40 bg-neon-purple/10 text-neon-purple hover:bg-neon-purple/20',
  };

  return (
    <button
      onClick={onClick}
      disabled={!!loading}
      className={`flex items-center justify-center gap-1 rounded border transition-colors
        ${small ? 'py-1 text-[10px]' : 'py-1.5 text-[11px]'}
        font-bold uppercase tracking-wider
        ${colorMap[color]}
        ${loading ? 'opacity-50 cursor-not-allowed' : ''}
      `}
    >
      {loading ? <span className="w-3 h-3 border border-current border-t-transparent rounded-full animate-spin" /> : icon}
      {label}
    </button>
  );
}
