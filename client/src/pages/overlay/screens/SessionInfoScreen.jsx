import React, { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { useOverlaySocket } from '../../../contexts/OverlaySocketContext';
import { MonitorPlay, Clock, IndianRupee, User, AlertTriangle, LogOut, CheckCircle2, X } from 'lucide-react';
import { format } from 'date-fns';
import api from '../../../config/api';
import { formatMoney } from '../../../utils/money';
import { computeRoundedBreakdown } from '../../../utils/billRounding';
import { MIN_GAMING_BALANCE_TO_START } from '../../../utils/memberWalletRules';

export default function SessionInfoScreen() {
  const { sessionData, pcId, connectionStatus, memberCheckout, lowBalanceWarning } = useOverlaySocket();
  const navigate = useNavigate();
  const [now, setNow] = useState(Date.now());
  const [checkoutLoading, setCheckoutLoading] = useState(false);
  const [checkoutError, setCheckoutError] = useState(null);
  const [resumeChecking, setResumeChecking] = useState(false);
  const [resumeError, setResumeError] = useState(null);

  // Logging out is a single tap — the bill is settled straight from the wallet with no
  // confirmation step, because the member has already agreed to wallet billing by playing.
  const handleLogout = async () => {
    if (!sessionData?.sessionId || checkoutLoading) return;
    setCheckoutLoading(true);
    setCheckoutError(null);

    const res = await memberCheckout(sessionData.sessionId);

    if (!res?.success) {
      setCheckoutError(res?.error || 'Failed to log out. Please see the operator.');
      setCheckoutLoading(false);
      return;
    }

    // Logging out finishes here, rather than waiting to be told it happened.
    //
    // This used to end at the request and leave the screen alone, on the assumption that the PC
    // flipping to Idle over SignalR would unmount it. When that message did not arrive - socket
    // dropped, shop wifi blipped, PC state update lost on the way - nothing else ever completed
    // the logout. The button stayed on "Paying..." and stayed disabled, so the member could not
    // even try again: from where they sat, Logout simply did not work. The session really had
    // ended and been billed on the server the whole time, which is the confusing part.
    //
    // The member's token is cleared as part of it, and that matters beyond tidiness. These are
    // shared machines; leaving a member logged in on a PC they have walked away from hands their
    // wallet to whoever sits down next. handleWalletEmptyLogout already did this properly - the
    // normal, far more common path was the one that did not.
    localStorage.removeItem('memberToken');
    localStorage.removeItem('memberProfile');
    localStorage.removeItem('walletEmptyAlert');

    setCheckoutLoading(false);
    navigate(`/pc-overlay/${pcId}/login`);
  };

  // After the wallet runs dry the member can top up at the counter and come straight back —
  // re-check the live balance rather than trusting the stale cached profile.
  const handleToppedUpResume = async () => {
    setResumeChecking(true);
    setResumeError(null);
    try {
      const token = localStorage.getItem('memberToken');
      const res = await api.get('/public/members/me', {
        headers: { Authorization: `Bearer ${token}` }
      });
      const profile = res.data?.data;

      if (!profile) {
        setResumeError('Could not read your Member Amount. Please see the operator.');
        return;
      }

      if (profile.gamingBalance < MIN_GAMING_BALANCE_TO_START) {
        setResumeError(`Your Gaming Member Amount is still ₹${profile.gamingBalance.toFixed(2)}. Please complete the top-up at the counter.`);
        return;
      }

      localStorage.setItem('memberProfile', JSON.stringify(profile));
      localStorage.removeItem('walletEmptyAlert');
      navigate(`/pc-overlay/${pcId}/login`);
    } catch (err) {
      setResumeError(err.response?.data?.error || 'Could not check your Member Amount. Please see the operator.');
    } finally {
      setResumeChecking(false);
    }
  };

  const handleWalletEmptyLogout = () => {
    localStorage.removeItem('walletEmptyAlert');
    localStorage.removeItem('memberToken');
    localStorage.removeItem('memberProfile');
    navigate(`/pc-overlay/${pcId}/login`);
  };

  useEffect(() => {
    const interval = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(interval);
  }, []);

  const { liveGamingCharge, liveTotalBill } = React.useMemo(() => {
    if (!sessionData) return { liveGamingCharge: 0, liveTotalBill: 0 };
    // Free during the branch's buffer window, then billed for exact elapsed time —
    // for every session type (fixed package or Pay-As-You-Go), same as the backend.
    const elapsedSeconds = Math.max(0, Math.floor((now - (sessionData.playAnchorMs ?? new Date(sessionData.sessionStart).getTime())) / 1000));
    const elapsedMin = elapsedSeconds / 60;
    const ratePerHour = sessionData.ratePerHour ?? 0;
    const bufferMinutes = sessionData.bufferMinutes ?? 10;
    const hours = Math.max(elapsedMin / 60, 1 / 60);
    const gaming = elapsedMin <= bufferMinutes ? 0 : Number((hours * ratePerHour).toFixed(2));
    const { displayGaming, roundedTotal } = computeRoundedBreakdown(gaming, sessionData.foodCharges || 0);
    return { liveGamingCharge: displayGaming, liveTotalBill: roundedTotal };
  }, [sessionData, now]);

  // Wallet-exhausted auto-checkout itself now runs at the OverlaySocketContext provider
  // level (see there) so it keeps working no matter which overlay screen is open — this
  // component just reacts to sessionData going null / walletEmptyAlert being set afterward.

  const formatTime = (seconds) => {
    if (seconds <= 0) return '00:00:00';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = Math.floor(seconds % 60);
    return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
  };

  if (!sessionData) {
    const showWalletEmpty = localStorage.getItem('walletEmptyAlert') === 'true';

    return (
      <div className="flex flex-col items-center justify-center h-full p-6 text-center">
        <MonitorPlay className="w-16 h-16 text-text-3 mb-4 opacity-50" />
        
        {showWalletEmpty ? (
          <div className="bg-neon-red/10 border border-neon-red/30 p-6 rounded-xl max-w-md animate-in zoom-in mb-6">
            <AlertTriangle className="w-12 h-12 text-neon-red mx-auto mb-4" />
            <h2 className="font-heading text-2xl font-bold text-neon-red tracking-wide uppercase mb-2">Session Ended</h2>
            <p className="text-neon-red font-body font-bold text-lg">
              Your gaming Member Amount is empty.<br/>Your bill has been paid from your Member Amount.
            </p>
            <p className="text-text-2 font-body text-sm mt-3">
              Top up at the counter, then tap below to jump straight back in.
            </p>

            {resumeError && (
              <div className="mt-4 bg-neon-orange/10 border border-neon-orange/30 p-3 rounded-md text-neon-orange text-sm font-body">
                {resumeError}
              </div>
            )}

            <div className="grid grid-cols-1 gap-2 mt-5">
              <button
                onClick={handleToppedUpResume}
                disabled={resumeChecking}
                className="w-full py-3 rounded-xl bg-neon-green/15 hover:bg-neon-green/25 border border-neon-green/50 text-neon-green font-heading uppercase tracking-widest font-bold text-sm transition-all disabled:opacity-50 disabled:cursor-not-allowed flex justify-center items-center gap-2"
              >
                {resumeChecking ? (
                  <div className="w-5 h-5 border-2 border-neon-green/30 border-t-neon-green rounded-full animate-spin" />
                ) : (
                  <>
                    <CheckCircle2 className="w-4 h-4" />
                    I've Topped Up — Resume
                  </>
                )}
              </button>
              <button
                onClick={handleWalletEmptyLogout}
                className="w-full py-3 rounded-xl bg-bg-3 hover:bg-bg-2 border border-border text-text-2 hover:text-text font-heading uppercase tracking-widest font-bold text-sm transition-colors flex justify-center items-center gap-2"
              >
                <LogOut className="w-4 h-4" />
                Log Out
              </button>
            </div>
          </div>
        ) : (
          <>
            <h2 className="font-heading text-2xl font-bold text-text-2 tracking-wide uppercase">No Active Session</h2>
            <p className="text-text-3 font-body mt-2">Please see the counter to start a session on {pcId}.</p>
          </>
        )}
        
        <div className="mt-8">
          <p className="text-text-3 text-sm mb-3">Or login to start session automatically</p>
          <button 
            onClick={() => window.location.href = `/pc-overlay/${pcId}/login`}
            className="flex items-center gap-2 bg-accent/10 hover:bg-accent/20 text-accent border border-accent/30 py-2 px-6 rounded transition-colors"
          >
            <User className="w-4 h-4" />
            <span className="font-heading tracking-wider uppercase font-bold text-sm">Member Login</span>
          </button>
        </div>

        {connectionStatus === 'disconnected' && (
          <div className="mt-8 bg-neon-orange/10 border border-neon-orange/30 p-3 rounded-md flex items-center gap-2">
            <AlertTriangle className="w-4 h-4 text-neon-orange" />
            <span className="text-neon-orange text-sm font-body">Disconnected from server</span>
          </div>
        )}
      </div>
    );
  }

  if (sessionData.sessionStatus === 'awaiting_billing') {
    return (
      <div className="flex flex-col items-center justify-center h-full p-6 text-center">
        <div className="bg-neon-orange/10 border border-neon-orange/30 p-6 rounded-xl max-w-md animate-in zoom-in">
          <Clock className="w-12 h-12 text-neon-orange mx-auto mb-4" />
          <h2 className="font-heading text-2xl font-bold text-neon-orange tracking-wide uppercase mb-2">Session Ended</h2>
          <p className="text-text-2 font-body text-lg mb-1">Your plan time is up.</p>
          <p className="text-text-3 font-body">
            Please pay ₹{formatMoney(sessionData.totalBill || 0)} at the counter to continue.
          </p>
        </div>
      </div>
    );
  }

  const isPayAsYouGo = !sessionData.plannedDurationMin || sessionData.plannedDurationMin === 0;
  
  let displayTime = '';
  let timeLabel = '';
  let isLowTime = false;

  if (isPayAsYouGo) {
    const elapsedSeconds = Math.max(0, Math.floor((now - (sessionData.playAnchorMs ?? new Date(sessionData.sessionStart).getTime())) / 1000));
    displayTime = formatTime(elapsedSeconds);
    timeLabel = 'Elapsed Time';
    isLowTime = false;
  } else {
    // Dynamically calculate remaining time based on start time and planned duration
    const expectedEndTimeMs = (sessionData.playAnchorMs ?? new Date(sessionData.sessionStart).getTime()) + (sessionData.plannedDurationMin * 60 * 1000);
    const remainingSeconds = Math.max(0, Math.floor((expectedEndTimeMs - now) / 1000));
    
    displayTime = formatTime(remainingSeconds);
    timeLabel = 'Remaining Time';
    isLowTime = remainingSeconds < 900;
  }

  return (
    <div className="p-6 h-full flex flex-col">
      {/* Session Status Header */}
      <div className="flex items-center justify-between mb-8">
        <div>
          {/* pcId here is the raw route param the desktop client's overlay window opens with -
              the PC's internal Guid (see desktop-client/AppConfig.cs's PcId), never meant to be
              read by a customer. It used to be the fallback here, so on the rare response where
              sessionData.pcName came back empty this heading showed a bare GUID like
              "F702A3E4-F94D-4A57-A587-4EAEBC28A693" instead of anything meaningful - even though
              sessionData.customerName is already always populated (backend defaults it to
              "Guest") and rendered correctly a few sections below under the Customer label. */}
          <h1 className="font-heading text-3xl font-bold text-text tracking-wider uppercase">
            {sessionData.pcName || sessionData.customerName || 'Session'}
          </h1>
          <div className="flex items-center gap-2 mt-1">
            <span className="w-2 h-2 rounded-full bg-neon-green shadow-[0_0_5px_#22d3a6]" />
            <span className="text-neon-green font-body text-sm uppercase tracking-wide font-bold">Session Active</span>
          </div>
        </div>
        <div className="flex gap-3">
          {sessionData.memberLinked && (
            <button
              onClick={handleLogout}
              disabled={checkoutLoading}
              className="bg-neon-red/10 hover:bg-neon-red/20 border border-neon-red/30 p-2 sm:p-3 rounded-xl transition-colors shadow-inner flex items-center gap-2 group disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {checkoutLoading ? (
                <div className="w-5 h-5 border-2 border-neon-red/30 border-t-neon-red rounded-full animate-spin" />
              ) : (
                <LogOut className="w-5 h-5 text-neon-red group-hover:scale-110 transition-transform" />
              )}
              <span className="text-neon-red font-heading uppercase text-sm font-bold tracking-widest hidden sm:inline-block">
                {checkoutLoading ? 'Paying…' : 'Logout'}
              </span>
            </button>
          )}
          {!sessionData.memberLinked && (
            <div className="bg-bg-3 p-3 rounded-xl border border-border shadow-inner flex items-center justify-center">
              <User className="w-5 h-5 text-accent" />
            </div>
          )}
        </div>
      </div>

      {/* Member Profile Block (Full Width) */}
      {sessionData.memberLinked && (
        <div className="bg-bg-3 border border-border p-4 rounded-xl shadow-inner flex flex-col gap-4 mb-6">
          <div className="flex items-center gap-3">
            <div className="bg-accent/10 p-2 rounded-lg">
              <User className="w-6 h-6 text-accent" />
            </div>
            <span className="text-text font-heading uppercase text-lg font-bold tracking-widest">{sessionData.customerName}</span>
          </div>
          <div className="h-px w-full bg-border"></div>
          <div className="grid grid-cols-2 gap-4">
            <div className="flex flex-col bg-bg-2 p-3 rounded-lg border border-border/50">
              <span className="text-text-3 text-[11px] uppercase tracking-wider font-bold mb-1">Gaming Balance</span>
              <span className="text-neon-green font-mono font-bold text-lg">₹{formatMoney(Math.max(0, (sessionData.gamingBalance || 0) - liveGamingCharge))}</span>
            </div>
            <div className="flex flex-col bg-bg-2 p-3 rounded-lg border border-border/50">
              <span className="text-text-3 text-[11px] uppercase tracking-wider font-bold mb-1">Food Balance</span>
              <span className="text-neon-orange font-mono font-bold text-lg">₹{formatMoney(Math.max(0, (sessionData.foodBalance || 0) - (sessionData.foodCharges || 0)))}</span>
            </div>
          </div>
        </div>
      )}

      {/* Low-balance reminder — a banner, never a popup, so it can't interrupt play. */}
      {lowBalanceWarning && (
        <div className={`mb-4 rounded-xl border p-4 flex items-start gap-3 animate-in fade-in slide-in-from-top-2 ${
          lowBalanceWarning.isFinal
            ? 'bg-neon-red/10 border-neon-red/40'
            : 'bg-neon-orange/10 border-neon-orange/40'
        }`}>
          <AlertTriangle className={`w-6 h-6 shrink-0 mt-0.5 ${lowBalanceWarning.isFinal ? 'text-neon-red' : 'text-neon-orange'}`} />
          <div>
            <p className={`font-heading font-bold uppercase tracking-wider text-sm ${lowBalanceWarning.isFinal ? 'text-neon-red' : 'text-neon-orange'}`}>
              {lowBalanceWarning.isFinal ? 'Final reminder' : 'Low gaming balance'}
            </p>
            <p className="text-text-2 font-body text-sm mt-1">
              <strong className="text-text">₹{formatMoney(lowBalanceWarning.remaining)}</strong> remaining
              {lowBalanceWarning.minutes > 0 && <> — about <strong className="text-text">{lowBalanceWarning.minutes} min</strong> of play left</>}.
              {' '}Please top up at the counter to keep playing.
            </p>
          </div>
        </div>
      )}

      {checkoutError && (
        <div className="mb-4 bg-neon-orange/10 border border-neon-orange/30 p-3 rounded-md flex items-center gap-2">
          <AlertTriangle className="w-4 h-4 text-neon-orange shrink-0" />
          <span className="text-neon-orange text-sm font-body">{checkoutError}</span>
        </div>
      )}

      {/* Main Info Grid */}
      <div className="grid grid-cols-2 gap-4 mb-6 flex-1">

        {/* Time Display */}
        <div className={`col-span-2 p-6 rounded-xl border relative overflow-hidden ${isLowTime ? 'bg-neon-orange/10 border-neon-orange/50 shadow-[0_0_15px_rgba(255,165,0,0.2)]' : 'bg-bg-3 border-border shadow-inner'}`}>
          <div className="flex items-center justify-between mb-2 relative z-10">
            <span className="text-text-2 font-heading tracking-widest uppercase text-sm font-bold">{timeLabel}</span>
            <Clock className={`w-5 h-5 ${isLowTime ? 'text-neon-orange' : 'text-accent'}`} />
          </div>
          <div className={`font-mono text-5xl font-bold relative z-10 ${isLowTime ? 'text-neon-orange' : 'text-text'}`}>
            {displayTime}
          </div>
          
          {/* Progress bar background indicator */}
          <div className="absolute bottom-0 left-0 h-1 bg-accent/20 w-full" />
        </div>

        {/* Current Bill */}
        <div className="p-4 rounded-xl border border-border bg-bg-3 shadow-inner">
          <div className="flex items-center justify-between mb-2">
            <span className="text-text-3 font-heading tracking-widest uppercase text-xs font-bold">Current Bill</span>
            <IndianRupee className="w-4 h-4 text-text-2" />
          </div>
          <div className="font-mono text-2xl font-bold text-text">
            ₹{formatMoney(liveTotalBill)}
          </div>
        </div>

        {/* Start Time */}
        <div className="p-4 rounded-xl border border-border bg-bg-3 shadow-inner">
          <div className="flex items-center justify-between mb-2">
            <span className="text-text-3 font-heading tracking-widest uppercase text-xs font-bold">Started At</span>
            <Clock className="w-4 h-4 text-text-2" />
          </div>
          <div className="font-mono text-lg font-bold text-text-2">
            {format(new Date(sessionData.sessionStart), 'hh:mm a')}
          </div>
        </div>
      </div>

      <div className="mt-auto">
        <div className="bg-bg-3 border border-border rounded-xl p-4 flex items-center justify-between shadow-inner">
          <div>
            <p className="text-text-3 text-xs font-heading tracking-widest uppercase mb-1">Customer</p>
            <p className="text-text font-body font-bold">{sessionData.customerName}</p>
          </div>
          {sessionData.memberLinked && (
            <span className="px-2 py-1 bg-accent/20 text-accent border border-accent/30 rounded text-xs font-bold uppercase tracking-wider">
              Member
            </span>
          )}
        </div>
      </div>
      
    </div>
  );
}
