import React, { useState, useEffect, useRef } from 'react';
import { useParams, Routes, Route, Navigate, useNavigate, useLocation } from 'react-router-dom';
import { OverlaySocketProvider } from '../../contexts/OverlaySocketContext';
import OverlayNavBar from './components/OverlayNavBar';
import SessionInfoScreen from './screens/SessionInfoScreen';
import FoodOrderScreen from './screens/FoodOrderScreen';
import TimeExtensionScreen from './screens/TimeExtensionScreen';
import CallOperatorScreen from './screens/CallOperatorScreen';
import CurrentBillScreen from './screens/CurrentBillScreen';
import OverlayMemberLoginScreen from './screens/OverlayMemberLoginScreen';
import PcLockScreen from './components/PcLockScreen';
import { Minimize2, Maximize2 } from 'lucide-react';
import { useOverlaySocket } from '../../contexts/OverlaySocketContext';
import WalletApprovalModal from './components/WalletApprovalModal';

// Tells the native shell hosting this page (desktop-client's MainForm) how much of the
// screen it should actually cover right now - see MainForm.cs's ApplyOverlayLayout. Only
// meaningful inside that WebView2 host; a plain browser tab (support, testing, mock mode)
// has no such bridge, and this must never throw there.
function postToHost(payload) {
  try { window.chrome?.webview?.postMessage(JSON.stringify(payload)); } catch { /* not hosted */ }
}

// How big each floating mode needs its window to be, in the CSS pixels this page is laid out
// in: the bubble is the w-14 button plus room for its border and glow, the panel is the size
// the quick-panel below is designed around. Kept here rather than in the host because the host
// cannot work them out - see the layoutMode effect for why it needs telling.
const OVERLAY_SIZES = {
  bubble: { width: 60, height: 60 },
  panel: { width: 380, height: 620 },
  // The panel, pinned above the game for the "save your game" warning - see timeWarning.
  alert: { width: 380, height: 620 },
  // Not a real widget size - the host clips "bubble" mode to a circle matching whatever size it
  // is given (see MainForm.cs's SetFloatingShape), so a 1x1 request becomes a single-pixel dot,
  // not a visible corner button. That is the point during maintenance: nothing on screen should
  // read as "our app is still here" while someone is using the PC as an ordinary desktop.
  hidden: { width: 1, height: 1 },
};

// Drag-to-move for the floating widget (bubble or expanded panel). The native window is what
// actually moves - see MainForm.cs's DragFloatingWindow - because WebView2's own content sits
// in a separate child window that a plain WM_NCHITTEST/HTCAPTION trick played by the host Form
// never sees, so the move has to be driven from in here instead.
function useDragToMoveHost() {
  const drag = useRef({ active: false, moved: false, x: 0, y: 0 });

  const onPointerDown = (e) => {
    // A control inside the drag handle does its own job and is never a drag. Without this the
    // panel's minimise button did nothing at all: pressing it started a drag on the header
    // strip around it, which captures the pointer, so the pointerup was delivered to the strip
    // instead of the button - and a browser only raises click when both halves land on the
    // same element. The button was never broken; it simply never received a click. Matched by
    // role rather than by that one button, so anything added to the header later is safe too.
    if (e.target.closest?.('button, a, input, select, textarea, [role="button"]')) return;

    drag.current = { active: true, moved: false, x: e.clientX, y: e.clientY };
    e.currentTarget.setPointerCapture?.(e.pointerId);
  };
  const onPointerMove = (e) => {
    const st = drag.current;
    if (!st.active) return;
    const dx = e.clientX - st.x;
    const dy = e.clientY - st.y;
    if (dx === 0 && dy === 0) return;
    if (Math.abs(dx) > 3 || Math.abs(dy) > 3) st.moved = true;
    st.x = e.clientX;
    st.y = e.clientY;
    postToHost({ type: 'overlay-drag', dx, dy });
  };
  const onPointerUp = () => { drag.current.active = false; };

  return { onPointerDown, onPointerMove, onPointerUp, wasDragged: () => drag.current.moved };
}

// Extracted inner content to consume socket context
function OverlayContent({ isMinimized, setIsMinimized }) {
  const { pcId } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  const { sessionData, sessionLoading, pcState, timeWarning } = useOverlaySocket();
  const bubbleDrag = useDragToMoveHost();
  const panelDrag = useDragToMoveHost();

  // Show full-screen lock screen if there's no active session or time has run out
  const isTimeUp = sessionData && sessionData.remainingTime !== null && sessionData.remainingTime <= 0;
  const isSessionEnded = sessionData && sessionData.sessionStatus !== 'active';
  const showLockScreen = !sessionLoading && (!sessionData || isTimeUp || isSessionEnded);

  // A PC under maintenance never has a session to interrupt (MarkMaintenanceAsync refuses to
  // flag one that does). Handled the same way an active session already is: the app shrinks
  // itself out of the way instead of covering the screen, so whoever is working on this PC has
  // the real Windows desktop to actually use it - not a locked kiosk screen of any kind. Unlike
  // an active session's bubble, nothing is drawn in it at all (see the render below), since
  // there is no session to reopen and nothing here for a customer to click.
  const isUnderMaintenance = pcState === 'UnderMaintenance';

  // Time running out locks this screen right here, before the server has stopped anything -
  // and the games were only ever closed on the server's SessionStopped. So the game kept
  // running under the lock and could come back over it. Tell the host the moment play
  // actually ends here too; closing twice is harmless.
  const wasPlayingRef = useRef(false);
  useEffect(() => {
    const playing = sessionData?.sessionStatus === 'active' && !isTimeUp;
    if (wasPlayingRef.current && isTimeUp) {
      try { window.chrome?.webview?.postMessage('session-ended'); } catch { /* not hosted */ }
    }
    wasPlayingRef.current = playing;
  }, [sessionData?.sessionStatus, isTimeUp]);

  const showTimeAlert = !!timeWarning && !showLockScreen && !isUnderMaintenance;

  // Tells the native shell to start/stop recording this PC's own screen while it's under
  // maintenance - see MainForm.cs's HandleOverlayLayoutMessage. Posted on every transition
  // (not just once) so a page reload mid-maintenance still gets the host into the right state,
  // since the host has no other way to learn this than being told.
  useEffect(() => {
    postToHost({ type: 'maintenance-recording', active: isUnderMaintenance });
  }, [isUnderMaintenance]);

  // Tells the native shell that somebody is playing here right now, so it can hold an update
  // back instead of restarting the machine's app under them - MainForm.IsSessionRunningAsync
  // looks for exactly this attribute before it installs anything.
  //
  // It was looking for it against a page that never set it. querySelector found nothing, which
  // the host reads as "nobody is playing", so the one guard meant to protect a paying customer
  // mid-session answered no every single time and an update on a gaming PC would go in on top
  // of live play. Set on <body> rather than inside any one screen, so it stays true across
  // every state an active session passes through - bubble, panel and anything added later.
  useEffect(() => {
    if (sessionData?.sessionStatus !== 'active') {
      document.body.removeAttribute('data-session-active');
      return undefined;
    }
    document.body.setAttribute('data-session-active', 'true');
    return () => document.body.removeAttribute('data-session-active');
  }, [sessionData?.sessionStatus]);

  // A fresh session opens the panel, so the customer is actually shown what they have just
  // been given - time remaining, rate, what they are being billed - instead of a bubble they
  // have to know to press. They can minimise it to that bubble the moment they want the
  // screen, and it never covers more than its own corner either way.
  //
  // This deliberately replaces the opposite rule. Starting minimised was meant to hand over
  // the whole screen at once, but it made the start of a session look like nothing had
  // happened: the gate disappeared and all that was left was a small circle in the corner.
  // Keyed on sessionId so it fires exactly once per session rather than re-opening the panel
  // every time a silent refetch touches sessionData - which would yank the panel back open
  // under a customer who had just closed it.
  const lastOpenedSessionId = useRef(null);
  useEffect(() => {
    if (sessionData?.sessionStatus === 'active' && sessionData.sessionId !== lastOpenedSessionId.current) {
      lastOpenedSessionId.current = sessionData.sessionId;
      setIsMinimized(false);

      // Forces the panel back to the session screen, regardless of whatever route was left
      // over from before this session existed. Without this, a member who had just been on
      // "/login" (the gate's own member-login screen, or the panel's - either one) kept that
      // path in the browser's own history even after it stopped being shown: while the lock
      // screen covered the whole window nothing here was ever unmounted or reset, since
      // <Routes> only exists inside the panel branch below. The moment the new session flips
      // the layout over to the panel, <Routes> mounts for the first time and matches whatever
      // path was still sitting there - "Member Login" flashing back on screen for a customer
      // who had just finished logging in and was never on that screen with the panel showing
      // at all.
      if (location.pathname !== `/pc-overlay/${pcId}/`) {
        navigate(`/pc-overlay/${pcId}/`, { replace: true });
      }
    }
  }, [sessionData?.sessionId, sessionData?.sessionStatus, setIsMinimized, navigate, pcId, location.pathname]);

  // Tells MainForm how big a window to actually be: full screen for the walk-in/member gate
  // and the locked "session ended" screen (both render themselves fixed inset-0 and need the
  // window to actually be the screen for that to mean anything), otherwise just enough to
  // cover the small floating widget - see the IsUserPc branch of MainForm's constructor.
  //
  // The size goes across in CSS pixels together with this page's own devicePixelRatio, and the
  // host multiplies the two. It cannot work that out for itself: a native window is sized in
  // device pixels, and the ratio between those and the CSS pixels this widget is laid out in is
  // not the window's DPI. Measured on a 1920x1200 screen at 150%, the window reported 144 dpi
  // while this page was rendering at devicePixelRatio 2.01 - so a window sized off the DPI gave
  // the quick-panel a 284x463 viewport for a layout that needs 380x620, and its header and nav
  // bar filled the window with the session content left nowhere to go. Only the page can
  // measure this ratio, so the page is what reports it.
  const layoutMode = isUnderMaintenance ? 'bubble' : showLockScreen ? 'full' : showTimeAlert ? 'alert' : isMinimized ? 'bubble' : 'panel';
  useEffect(() => {
    // "bubble" mode itself is what the host needs to hear to position this window like a
    // corner widget rather than a full-screen gate - the size is a separate question, and
    // during maintenance it is the 1x1 "hidden" size rather than the real bubble's 60x60.
    const size = isUnderMaintenance ? OVERLAY_SIZES.hidden : OVERLAY_SIZES[layoutMode];
    postToHost({
      type: 'overlay-layout',
      mode: layoutMode,
      ...(size && { width: size.width, height: size.height, dpr: window.devicePixelRatio || 1 }),
    });
  }, [layoutMode, isUnderMaintenance]);

  // Same bubble-sized window an active session already uses to stay out of the way - just
  // with nothing drawn inside it, so the desktop underneath reads as a normal, empty PC rather
  // than one with an app window (however small) sitting on top of it.
  if (isUnderMaintenance) {
    return null;
  }

  if (showLockScreen) {
    return (
      <>
        <PcLockScreen />
        <WalletApprovalModal />
      </>
    );
  }

  // When minimized, we only show a tiny floating widget that can be expanded - or dragged
  // anywhere on screen. A tap (pointer down+up with no real movement) expands it; anything
  // that actually moves is a drag instead, so the two can share one small target.
  if (isMinimized && !showTimeAlert) {
    return (
      <>
        <div className="fixed inset-0 z-50 flex items-center justify-center">
          <div
            onPointerDown={bubbleDrag.onPointerDown}
            onPointerMove={bubbleDrag.onPointerMove}
            onPointerUp={(e) => {
              bubbleDrag.onPointerUp(e);
              if (!bubbleDrag.wasDragged()) setIsMinimized(false);
            }}
            className="w-14 h-14 bg-bg-2/90 backdrop-blur-xl border border-accent/50 rounded-full flex items-center justify-center text-accent shadow-[0_0_15px_rgba(220,38,38,0.3)] hover:bg-accent/20 transition-all cursor-move touch-none select-none"
          >
            <Maximize2 className="w-5 h-5 pointer-events-none" />
          </div>
        </div>
        <WalletApprovalModal />
      </>
    );
  }

  // Expanded quick-panel - a small movable floating widget now, not a permanently docked
  // right-hand strip. Drag the header to move it; the native window resizes to fit either
  // state and keeps wherever the customer last left it (see MainForm.cs's ApplyOverlayLayout).
  return (
    <>
      <div className="fixed inset-0 flex flex-col bg-bg-2/95 backdrop-blur-xl border border-border/60 rounded-2xl shadow-2xl shadow-black/80 z-50 overflow-hidden font-body text-text">

      {/* Header Strip - drag handle */}
      <div
        onPointerDown={panelDrag.onPointerDown}
        onPointerMove={panelDrag.onPointerMove}
        onPointerUp={panelDrag.onPointerUp}
        className="h-14 bg-black/40 border-b border-border/50 flex items-center justify-between px-5 shrink-0 cursor-move touch-none select-none"
      >
        <div className="flex items-center gap-3 pointer-events-none">
          <div className="w-2.5 h-2.5 rounded-full bg-neon-green animate-pulse shadow-[0_0_8px_#22d3a6]" />
          <span className="font-heading font-bold tracking-widest uppercase text-accent">Apple Esports</span>
        </div>
        <button
          onClick={() => setIsMinimized(true)}
          className="text-text-3 hover:text-accent transition-colors p-1.5 hover:bg-accent/10 rounded-md"
          title="Minimize Overlay"
        >
          <Minimize2 className="w-5 h-5" />
        </button>
      </div>

      {showTimeAlert && (
        <div className="shrink-0 bg-neon-red/20 border-b border-neon-red/60 px-5 py-3 text-center">
          <div className="font-heading font-extrabold uppercase tracking-wider text-neon-red text-lg">
            {timeWarning} minute{timeWarning > 1 ? 's' : ''} left
          </div>
          <div className="text-sm text-text font-bold mt-0.5">Save your game now.</div>
          <div className="text-xs text-text-2 mt-0.5">All games will close automatically when your time ends.</div>
        </div>
      )}

      {/* Content Area */}
      <div className="flex-1 overflow-y-auto relative bg-bg/50 scrollbar-thin">
        <Routes>
          <Route path="/" element={<SessionInfoScreen />} />
          <Route path="/login" element={<OverlayMemberLoginScreen />} />
          <Route path="/food" element={<FoodOrderScreen />} />
          <Route path="/extend" element={<TimeExtensionScreen />} />
          <Route path="/call" element={<CallOperatorScreen />} />
          <Route path="/bill" element={<CurrentBillScreen />} />
          <Route path="*" element={<Navigate to="" replace />} />
        </Routes>
      </div>

      {/* Navigation Bar */}
      <div className="shrink-0 h-16 border-t border-border/50 bg-bg-3/80">
        <OverlayNavBar />
      </div>
    </div>
    <WalletApprovalModal />
    </>
  );
}

export default function UserOverlayApp() {
  const { pcId } = useParams();
  const [isMinimized, setIsMinimized] = useState(true);

  return (
    <OverlaySocketProvider pcId={pcId} isMinimized={isMinimized}>
      <OverlayContent isMinimized={isMinimized} setIsMinimized={setIsMinimized} />
    </OverlaySocketProvider>
  );
}
