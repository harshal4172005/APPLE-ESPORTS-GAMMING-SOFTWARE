import { memo, useState, useEffect, useId } from 'react';
import { Infinity as InfinityIcon, Timer, UserRound } from 'lucide-react';
import { motion } from 'framer-motion';
import api from '../../config/api';
import { useToast } from '../ui/Toast';

// ── Pancafe-style status colors: icon + name + status dot only, no inline
// timers/charges/buttons — click a tile to load its detail panel ──
// bg is a permanent wash across the whole tile rather than a hover effect, and that is what makes a
// status readable from across the room instead of only when the mouse happens to be on it.
//
// AwaitingBilling uses pc-awaiting (white) now. It used to borrow neon-orange, which left the
// dedicated token unused and made "waiting to be billed" and "time finished" the same colour - two
// states an operator has to tell apart at a glance, because only one of them owes money.
//
// UnderMaintenance gets its own yellow for the same reason: it used to share red with a PC that had
// simply been shut down.
//
// AwaitingSetup gets its own grey for the same reason again: a PC record that has never been
// claimed by a physical machine used to fall through to STATUS_STYLES[pc.state] finding nothing
// and landing on DEFAULT_STYLE (bright red, "OFFLINE") - wrong in the other direction, since a PC
// that was never set up is not the same fact as one that lost power. Grey rather than another
// bright hue, deliberately: this is the one state on the grid that is not "something happening",
// so it should not compete for attention the way the others are meant to.
const STATUS_STYLES = {
  Idle:            { icon: 'text-pc-idle',        dot: 'bg-pc-idle',        border: 'border-pc-idle/50',        bg: 'bg-pc-idle/10',        label: 'FREE' },
  Active:          { icon: 'text-pc-active',      dot: 'bg-pc-active',      border: 'border-pc-active/60',      bg: 'bg-pc-active/10',      label: 'OCCUPIED' },
  AwaitingBilling: { icon: 'text-pc-awaiting',    dot: 'bg-pc-awaiting',    border: 'border-pc-awaiting/60',    bg: 'bg-pc-awaiting/10',    label: 'BILLING' },
  UnderMaintenance:{ icon: 'text-pc-maintenance', dot: 'bg-pc-maintenance', border: 'border-pc-maintenance/60', bg: 'bg-pc-maintenance/10', label: 'MAINT' },
  Expired:         { icon: 'text-neon-orange',    dot: 'bg-neon-orange',    border: 'border-neon-orange/60',    bg: 'bg-neon-orange/10',    label: 'EXPIRED' },
  AwaitingSetup:   { icon: 'text-pc-awaitingsetup', dot: 'bg-pc-awaitingsetup', border: 'border-pc-awaitingsetup/50', bg: 'bg-pc-awaitingsetup/10', label: 'NOT SET UP' },
};
const DEFAULT_STYLE = { icon: 'text-pc-offline', dot: 'bg-pc-offline', border: 'border-pc-offline/50', bg: 'bg-pc-offline/10', label: 'OFFLINE' };
const PENDING_STYLE = { icon: 'text-accent', dot: 'bg-accent', border: 'border-accent/50', bg: 'bg-accent/10', label: 'PENDING' };

// PC was told to shut down (pc.poweredOff) while a session was still open on it - the "time
// chalu, PC shutdown" case: the clock is still billing but the machine has actually powered
// off. Distinct from plain Shut Down (DEFAULT_STYLE, red) on purpose - an operator needs to
// see at a glance that this one still owes money. Reuses the same neon-orange token as
// Expired above rather than a new colour, since both are "needs attention" states.
const SHUTDOWN_BILLING_STYLE = { icon: 'text-neon-orange', dot: 'bg-neon-orange', border: 'border-neon-orange/60', bg: 'bg-neon-orange/10', label: 'OFF - BILLING' };

// ── Tile size steps — driven by the zoom control on SessionsPage ──
export const TILE_SIZES = ['sm', 'md', 'lg', 'xl'];
const SIZE_STYLES = {
  sm: { glyph: 'w-10 h-10', infinity: 'w-4 h-4', name: 'text-xs',   status: 'text-[9px]',  dot: 'w-2 h-2',     gap: 'gap-1',   py: 'py-3'  },
  md: { glyph: 'w-16 h-16', infinity: 'w-6 h-6', name: 'text-base', status: 'text-[10px]', dot: 'w-2.5 h-2.5', gap: 'gap-2',   py: 'py-5'  },
  lg: { glyph: 'w-24 h-24', infinity: 'w-9 h-9', name: 'text-xl',   status: 'text-xs',     dot: 'w-3 h-3',     gap: 'gap-2.5', py: 'py-7'  },
  xl: { glyph: 'w-32 h-32', infinity: 'w-12 h-12', name: 'text-2xl', status: 'text-sm',    dot: 'w-4 h-4',     gap: 'gap-3',   py: 'py-9'  },
};

// ── Glossy monitor glyph: bezel + screen + diagonal light sheen + stand,
// colored via the wrapping text-color class so it tracks PC status ──
function MonitorGlyph({ className = '', sizeClass = 'w-16 h-16', glow = false, children }) {
  const clipId = useId();
  return (
    <svg
      viewBox="0 0 64 56"
      className={`${sizeClass} ${className} ${glow ? 'drop-shadow-[0_0_8px_currentColor]' : ''}`}
      fill="none"
    >
      <rect x="3" y="3" width="58" height="38" rx="7" fill="currentColor" opacity="0.16" />
      <rect x="7" y="7" width="50" height="30" rx="4" fill="currentColor" opacity="0.85" />
      <clipPath id={clipId}>
        <rect x="7" y="7" width="50" height="30" rx="4" />
      </clipPath>
      <polygon points="7,37 36,7 57,7 28,37" fill="#fff" opacity="0.14" clipPath={`url(#${clipId})`} />
      <rect x="27" y="41" width="10" height="7" fill="currentColor" opacity="0.55" />
      <rect x="17" y="49" width="30" height="4" rx="2" fill="currentColor" opacity="0.55" />
      {children}
    </svg>
  );
}

// ── Console tile: the actual console photo (PS5 today) rather than a drawn glyph, so it reads
// unmistakably as a console at a glance instead of a stylised icon that could be mistaken for
// anything else. Tinted via the same status-color + drop-shadow pattern as MonitorGlyph, which
// works on a PNG the same way it does on an SVG as long as the source image has a transparent
// background - the glow traces the console's own silhouette rather than a plain rectangle. ──
function ConsoleGlyph({ className = '', sizeClass = 'w-16 h-16', glow = false }) {
  return (
    <img
      src="/ps5-console.png"
      alt="Console"
      className={`${sizeClass} ${className} object-contain ${glow ? 'drop-shadow-[0_0_8px_currentColor]' : ''}`}
    />
  );
}

function fmtDuration(ms) {
  const totalSec = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

const PcTile = memo(({ pc, walkinReq, isSelected, onSelect, onQuickStart, onRefresh, size = 'md' }) => {
  const toast = useToast();
  const [isDragOver, setIsDragOver] = useState(false);
  const [isTransferring, setIsTransferring] = useState(false);
  const [now, setNow] = useState(Date.now());
  const sizeStyle = SIZE_STYLES[size] || SIZE_STYLES.md;

  // A console (PS5, Xbox...) is a Pc row like any other underneath - same State, same Session,
  // same billing - tagged only by Zone so it never needed its own entity. This is purely a
  // display distinction: an operator glancing at the grid should see a controller, not a
  // monitor, for a seat that has neither a screen nor a screen-lock agent.
  const isConsole = pc.zone === 'Console';

  // pc.poweredOff means PcStatusHub's shutdown command was sent and the PC has not reconnected
  // since (see backend Pc.PoweredOff). On its own that's ambiguous: a shut-down PC with a
  // session still open on it (Active/AwaitingBilling) is still billing the customer and needs
  // the operator's attention, which a plain "Shut Down" tile would hide.
  const hasOpenSession = pc.state === 'Active' || pc.state === 'AwaitingBilling';

  // A PC still AwaitingSetup has never been claimed by a real machine, so poweredOff being true
  // on one is stale leftover state from before the backend guarded shutdown commands against
  // this exact case (SendShutdownCommand/SendShutdownAllCommand skip AwaitingSetup PCs now) -
  // never a real signal to act on. Excluded here too so old, already-stuck data can't force a
  // never-set-up PC to render as freshly shut down.
  const neverClaimed = pc.state === 'AwaitingSetup';
  const isShutDownWhileBilling = pc.poweredOff && hasOpenSession && !neverClaimed;
  const isShutDownIdle = pc.poweredOff && !hasOpenSession && !neverClaimed;

  // A reservation no longer gets its own tile appearance or gating - a PC is Idle, Occupied or
  // Under Maintenance, full stop. The backend still tracks a Reserved state internally (see
  // ReservationService), but on this screen it reads and behaves exactly like Idle: free to
  // double-click into a normal session the same as any walk-in, reservation or not.
  const displayState = pc.state === 'Reserved' ? 'Idle' : pc.state;

  const style = walkinReq
    ? PENDING_STYLE
    : isShutDownWhileBilling
      ? SHUTDOWN_BILLING_STYLE
      : isShutDownIdle
        ? DEFAULT_STYLE
        : (STATUS_STYLES[displayState] || DEFAULT_STYLE);
  const isIdle = displayState === 'Idle' && !walkinReq;
  const isActive = displayState === 'Active';
  // activeSessionId is only ever set when the session that lives behind it was actually
  // found - which fails silently at Head Office, where a branch's active sessions are never
  // synced (only their bills, once paid). Without this check, "no end time" was read as "this
  // is a genuine open-ended Pay-As-You-Go session" everywhere, including the one place that
  // was really saying "I don't have this session's details at all" - so Head Office showed the
  // infinity glyph on every active PC belonging to every branch, PAYG or not. A PC just
  // transferred between machines hits this hardest: Head Office's own state is briefly (or, for
  // a stuck heartbeat, not-so-briefly) missing the session row entirely.
  const isPayAsYouGo = isActive && !!pc.activeSessionId && !pc.sessionEndTime;
  const hasPlanTime = isActive && !!pc.sessionEndTime;

  // Same distinction as above, but for the plan-type glyph alone: it should still read while
  // a session is AwaitingBilling (time's up or manually stopped, still owed) - the whole
  // reason it's a separate pair of booleans instead of reusing isPayAsYouGo/hasPlanTime is
  // that those two also drive timerLabel, which must keep showing "BILLING" (via style.label)
  // rather than a stale countdown once the session leaves Active.
  const showPlanGlyph = hasOpenSession && !!pc.activeSessionId;
  // A member sitting here shows a person instead, so the operator can tell at a glance who is
  // playing on their own wallet - the owner's request, taking priority over infinity/timer.
  const glyphIsMember = showPlanGlyph && pc.customerType === 'Member';
  const glyphIsPayAsYouGo = showPlanGlyph && !glyphIsMember && !pc.sessionEndTime;
  const glyphIsPlanTime = showPlanGlyph && !glyphIsMember && !!pc.sessionEndTime;

  useEffect(() => {
    if (!isActive) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [isActive]);

  const timerLabel = hasPlanTime
    ? fmtDuration(new Date(pc.sessionEndTime).getTime() - now)
    : isPayAsYouGo && pc.sessionStartTime
      ? fmtDuration(now - new Date(pc.sessionStartTime).getTime())
      : null;

  const handleDoubleClick = () => {
    if (isIdle) onQuickStart?.(pc);
  };

  // Drag idle tiles accept a dropped active session for transfer
  const handleDragOver = (e) => {
    if (!isIdle) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
  };
  const handleDragEnter = (e) => {
    if (!isIdle) return;
    e.preventDefault();
    setIsDragOver(true);
  };
  const handleDragLeave = (e) => {
    e.preventDefault();
    setIsDragOver(false);
  };
  const handleDrop = async (e) => {
    if (!isIdle) return;
    e.preventDefault();
    setIsDragOver(false);
    try {
      const data = JSON.parse(e.dataTransfer.getData('text/plain'));
      if (!data.sessionId || data.sourcePcId === pc.id) return;
      setIsTransferring(true);
      await api.post(`/sessions/${data.sessionId}/transfer`, { targetPcId: pc.id });
      toast.success(`Session transferred to ${pc.name}!`);
      onRefresh?.();
    } catch (err) {
      toast.error(err.response?.data?.error || err.response?.data?.message || 'Failed to transfer session');
    } finally {
      setIsTransferring(false);
    }
  };

  return (
    <motion.button
      type="button"
      initial={{ opacity: 0, scale: 0.95 }}
      animate={{ opacity: 1, scale: 1 }}
      onClick={() => onSelect?.(pc)}
      onDoubleClick={handleDoubleClick}
      draggable={isActive}
      onDragStart={isActive ? (e) => {
        e.dataTransfer.setData('text/plain', JSON.stringify({ sessionId: pc.activeSessionId, sourcePcId: pc.id }));
        e.dataTransfer.effectAllowed = 'move';
      } : undefined}
      onDragOver={handleDragOver}
      onDragEnter={handleDragEnter}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
      title={walkinReq ? `${pc.name}: Walk-in pending` : isPayAsYouGo ? `${pc.name}: ${style.label} (Pay-As-You-Go, ${timerLabel} elapsed)` : hasPlanTime ? `${pc.name}: ${style.label} (${timerLabel} left)` : `${pc.name}: ${style.label}`}
      className={`group relative flex flex-col items-center justify-center ${sizeStyle.gap} rounded-xl ${sizeStyle.py} px-2 select-none transition-all border ${style.border} ${style.bg ?? ''}
        ${isSelected ? 'brightness-150 scale-105' : 'hover:brightness-125 hover:scale-105'}
        ${isDragOver ? 'bg-pc-active/5' : ''}
        ${isActive ? 'cursor-grab active:cursor-grabbing' : 'cursor-pointer'}
      `}
    >
      {isTransferring && (
        <div className="absolute inset-0 z-10 flex items-center justify-center bg-bg/80 backdrop-blur-sm rounded-xl">
          <span className="w-4 h-4 border-2 border-pc-active border-t-transparent rounded-full animate-spin" />
        </div>
      )}

      <div className={`relative flex items-center justify-center ${(walkinReq || pc.state === 'AwaitingBilling') ? 'animate-pulse' : ''}`}>
        {isConsole ? (
          <ConsoleGlyph className={style.icon} sizeClass={sizeStyle.glyph} glow={true} />
        ) : (
        <MonitorGlyph className={style.icon} sizeClass={sizeStyle.glyph} glow={true}>
          {/* What the customer is being charged on, said with a picture rather than a symbol. A
              black infinity mark on the screen reads as "no time limit, billed as they go";
              an orange timer reads as "a fixed block of time, counting down". */}
          {(glyphIsMember || glyphIsPayAsYouGo || glyphIsPlanTime) && (
            <foreignObject x="14" y="12" width="36" height="24">
              <div className="flex items-center justify-center w-full h-full gap-0.5">
                {glyphIsMember ? (
                  <UserRound className={`${sizeStyle.infinity} text-black`} fill="currentColor" strokeWidth={2} />
                ) : glyphIsPayAsYouGo ? (
                  <InfinityIcon className={`${sizeStyle.infinity} text-black`} strokeWidth={2.5} />
                ) : (
                  <Timer className={`${sizeStyle.infinity} text-neon-orange`} strokeWidth={2.5} />
                )}
              </div>
            </foreignObject>
          )}
        </MonitorGlyph>
        )}
      </div>

      <span className={`font-heading font-bold text-text ${sizeStyle.name} tracking-wider truncate max-w-full`}>{pc.name}</span>
      <span className={`flex items-center gap-1.5 ${sizeStyle.status} font-mono font-bold uppercase tracking-widest ${style.icon}`}>
        <span className={`${sizeStyle.dot} rounded-full ${style.dot}`} />
        {timerLabel || style.label}
      </span>
    </motion.button>
  );
});

PcTile.displayName = 'PcTile';
export default PcTile;
