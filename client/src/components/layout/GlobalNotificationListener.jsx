import { useState, useEffect } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { Bell, MonitorPlay, X, Check, Loader2 } from 'lucide-react';
import { useSocket } from '../../contexts/SocketContext';
import { useToast } from '../ui/Toast';
import { useBranch } from '../../contexts/BranchContext';
import { useAuth } from '../../contexts/AuthContext';
import api from '../../config/api';

export default function GlobalNotificationListener() {
  const { subscribe, SIGNALR_HUBS, connected } = useSocket();
  const { activeBranch } = useBranch();
  const { user } = useAuth();
  const toast = useToast();
  
  // Array of active walk-in requests
  const [requests, setRequests] = useState([]);
  const [isProcessing, setIsProcessing] = useState(false);

  useEffect(() => {
    if (!connected) return;

    const unsubscribe = subscribe(SIGNALR_HUBS.NOTIFICATIONS, 'Alert', async (data) => {
      const type = data.type || data.Type;
      
      let pcName = data.pcId || data.PcId;
      let branchName = "";
      let ratePerHour = 0;
      try {
        const res = await api.get(`/public/pcs/${pcName}`);
        if (res.data.success) {
          pcName = res.data.data.name;
          branchName = res.data.data.branchName || "";
          ratePerHour = res.data.data.ratePerHour ?? 0;
        }
      } catch (e) {
        // silent fallback to ID
      }

      // Filter notifications for Admins and SuperAdmins so they only see them for the actively selected branch
      const isAdminOrSuperAdmin = user?.role === 'admin' || user?.role === 'super_admin';
      const alertBranchId = data.branchId || data.BranchId;
      if (isAdminOrSuperAdmin && alertBranchId && activeBranch?.id && String(alertBranchId).toLowerCase() !== String(activeBranch.id).toLowerCase()) {
        return; // Ignore this notification as it's not for the currently viewed branch
      }

      if (type === 'WalkinSessionRequest') {
        // Add to our list of active requests
        setRequests(prev => {
          const pcId = data.pcId || data.PcId;
          // avoid duplicates if same PC sends multiple times
          const filtered = prev.filter(r => (r.pcId || r.PcId) !== pcId);
          return [...filtered, { ...data, resolvedPcName: pcName, resolvedBranchName: branchName, resolvedRatePerHour: ratePerHour }];
        });
        toast.info(`Walk-in request from ${pcName}`);
      } else if (type === 'OperatorCall') {
        const pcId = data.pcId || data.PcId;
        const msg = data.message || data.Message || `Assistance required at ${pcId}`;
        
        setRequests(prev => {
          const filtered = prev.filter(r => (r.pcId || r.PcId) !== pcId || r.type !== 'OperatorCall');
          return [...filtered, { ...data, type: 'OperatorCall', resolvedPcName: pcName, resolvedBranchName: branchName, resolvedRatePerHour: ratePerHour }];
        });
        
        // Speak the notification
        if ('speechSynthesis' in window) {
           const utterance = new SpeechSynthesisUtterance(`Attention Operator. ${pcName} is calling you.`);
           utterance.rate = 0.9;
           utterance.pitch = 1.1;
           window.speechSynthesis.speak(utterance);
        }
      }
    });

    const unsubscribeStatus = subscribe(SIGNALR_HUBS.PC_STATUS, 'PcStatusChanged', (payload) => {
      const data = payload.payload || payload.Payload || payload.data || payload.Data || payload;
      // If PC becomes active, remove any pending walk-in requests for it
      // Wait, our backend sends 'PcStatusChanged' on PC_STATUS hub
      const status = data.status || data.State || data.state;
      if (status === 'active' || status === 'Active' || status === 1 || status === '1') {
        setRequests(prev => prev.filter(r => {
          const reqPcId = r.pcId || r.PcId;
          return reqPcId !== (data.pcId || data.id) && reqPcId !== (data.name || data.Name);
        }));
      }
    });

    const unsubscribeExtension = subscribe(SIGNALR_HUBS.SESSIONS, 'ExtensionRequested', async (data) => {
      // The server now sends the PC number, customer and price with the request. Looking the PC
      // up is only needed for an older server that sent a bare id.
      let pcName = data.pcNumber || data.PcNumber || data.pcId || data.PcId;
      let branchName = "";
      let ratePerHour = 0;
      if (!(data.pcNumber || data.PcNumber)) {
        try {
          const res = await api.get(`/public/pcs/${pcName}`);
          if (res.data.success) {
              pcName = res.data.data.name;
              branchName = res.data.data.branchName || "";
              ratePerHour = res.data.data.ratePerHour ?? 0;
          }
        } catch (e) { }
      }

      const isAdminOrSuperAdmin = user?.role === 'admin' || user?.role === 'super_admin';
      const alertBranchId = data.branchId || data.BranchId;
      if (isAdminOrSuperAdmin && alertBranchId && activeBranch?.id && String(alertBranchId).toLowerCase() !== String(activeBranch.id).toLowerCase()) {
        return; 
      }

      setRequests(prev => {
        const pcId = data.pcId || data.PcId;
        const reqData = { ...data, type: 'ExtensionRequested', resolvedPcName: pcName, resolvedBranchName: branchName, resolvedRatePerHour: ratePerHour };
        const filtered = prev.filter(r => (r.pcId || r.PcId) !== pcId);
        return [...filtered, reqData];
      });
      toast.info(`Time extension request from ${pcName}`);
    });

    // A customer asks to move to another package (priced and checked by the server already).
    const unsubscribePackage = subscribe(SIGNALR_HUBS.SESSIONS, 'PackageChangeRequested', (data) => {
      const isAdminOrSuperAdmin = user?.role === 'admin' || user?.role === 'super_admin';
      const alertBranchId = data.branchId || data.BranchId;
      if (isAdminOrSuperAdmin && alertBranchId && activeBranch?.id && String(alertBranchId).toLowerCase() !== String(activeBranch.id).toLowerCase()) {
        return;
      }
      setRequests(prev => {
        const pcId = data.pcId || data.PcId;
        const filtered = prev.filter(r => !((r.pcId || r.PcId) === pcId && r.type === 'PackageChangeRequested'));
        return [...filtered, { ...data, type: 'PackageChangeRequested', resolvedPcName: data.pcNumber }];
      });
      toast.info(`Package change request from ${data.pcNumber}`);
    });

    return () => {
      unsubscribe();
      if (unsubscribeStatus) unsubscribeStatus();
      if (unsubscribeExtension) unsubscribeExtension();
      if (unsubscribePackage) unsubscribePackage();
    };
  }, [connected, subscribe, SIGNALR_HUBS.NOTIFICATIONS, SIGNALR_HUBS.PC_STATUS, SIGNALR_HUBS.SESSIONS, toast, user, activeBranch]);

  const handleApprove = async (req) => {
    setIsProcessing(true);
    try {
      const pcId = req.pcId || req.PcId;
      const customerName = req.customerName || req.CustomerName;
      const duration = req.duration || req.Duration || 0;

      if (req.type === 'PackageChangeRequested') {
        const res = await api.post(`/sessions/${req.sessionId}/change-package`, { packageId: req.packageId }, {
          headers: { 'X-Branch-Id': req.branchId }
        });
        if (res.data.success) {
          toast.success(`${req.pcNumber}: now ${req.toName}`);
          window.dispatchEvent(new CustomEvent('refresh-pcs', { detail: { pcId } }));
        }
        setRequests(prev => prev.filter(r => !((r.pcId || r.PcId) === pcId && r.type === 'PackageChangeRequested')));
        setIsProcessing(false);
        return;
      }

      if (req.type === 'ExtensionRequested') {
        const sessionId = req.sessionId || req.SessionId;
        
        // Use the resolved rate from PricingProfile, or fetch from session as fallback
        let ratePerHour = req.resolvedRatePerHour ?? 0;
        try {
            const sessionRes = await api.get(`/public/session/pc/${pcId}`);
            if (sessionRes.data.success && sessionRes.data.data) {
                ratePerHour = sessionRes.data.data.ratePerHour ?? ratePerHour;
            }
        } catch (e) {
            console.warn('Failed to fetch session rate, using PricingProfile rate');
        }
        
        // Informational only - the server prices the extension itself (same function as the card).
        const expectedAmount = req.expectedAmount ?? req.ExpectedAmount ?? (duration ? (duration / 60) * ratePerHour : 0);

        const extendRes = await api.post(`/sessions/${sessionId}/extend`, {
            AdditionalMinutes: duration,
            AdditionalAmount: expectedAmount,
            PackageName: `Extension - ${duration}m`
        }, {
            headers: { 'X-Branch-Id': req.branchId || req.BranchId }
        });
        if (extendRes.data.success) {
          toast.success(`Time extended for ${pcId}`);
          setRequests(prev => prev.filter(r => (r.pcId || r.PcId) !== pcId));
          window.dispatchEvent(new CustomEvent('refresh-pcs', { detail: { pcId } }));
        }
        setIsProcessing(false);
        return;
      }

      // 1. Resolve PC Guid (since the name might have been sent)
      const pcRes = await api.get(`/public/pcs/${pcId}`);
      if (!pcRes.data.success) {
        toast.error('PC not found in database');
        setIsProcessing(false);
        return;
      }
      
      const actualPcId = pcRes.data.data.id;
      const resolvedBranchId = pcRes.data.data.branchId || req.branchId || req.BranchId;
      const ratePerHour = pcRes.data.data.ratePerHour ?? 0;
      const expectedAmount = duration ? (duration / 60) * ratePerHour : 0;

      // 2. Start Session 
      const startRes = await api.post('/sessions/start', {
        pcId: actualPcId,
        memberId: null,
        customerName: customerName,
        durationMinutes: duration,
        packageName: req.packageName || req.PackageName || 'Walk-in',
        expectedAmount: expectedAmount
      }, {
        headers: { 'X-Branch-Id': resolvedBranchId }
      });

      if (startRes.data.success) {
        toast.success(`Session started for ${pcId}`);
        setRequests(prev => prev.filter(r => (r.pcId || r.PcId) !== pcId));
        window.dispatchEvent(new CustomEvent('refresh-pcs', { detail: { pcId: actualPcId } }));
      }
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to approve request');
    } finally {
      setIsProcessing(false);
    }
  };

  const handleDecline = async (req) => {
    try {
      const pcId = req.pcId || req.PcId;
      const branchId = req.branchId || req.BranchId;
      if (req.type === 'PackageChangeRequested') {
        await api.post(`/public/pcs/${pcId}/decline-package-change`, {}, { headers: { 'X-Branch-Id': branchId } });
        setRequests(prev => prev.filter(r => !((r.pcId || r.PcId) === pcId && r.type === 'PackageChangeRequested')));
        toast.info(`Declined package change for ${req.pcNumber}`);
      } else if (req.type === 'ExtensionRequested') {
        setRequests(prev => prev.filter(r => (r.pcId || r.PcId) !== pcId));
        toast.info(`Declined extension for ${pcId}`);
      } else {
        await api.post(`/public/pcs/${pcId}/decline-walkin`, {}, {
          headers: { 'X-Branch-Id': branchId }
        });
        setRequests(prev => prev.filter(r => (r.pcId || r.PcId) !== pcId));
        toast.info(`Declined walk-in for ${pcId}`);
      }
    } catch (err) {
      toast.error('Failed to decline request');
    }
  };

  const handleAcknowledgeCall = (req) => {
    const pcId = req.pcId || req.PcId;
    setRequests(prev => prev.filter(r => (r.pcId || r.PcId) !== pcId || r.type !== 'OperatorCall'));
    toast.success(`Acknowledged call from ${pcId}`);
  };

  if (requests.length === 0) return null;

  return (
    <div className="fixed bottom-6 left-1/2 -translate-x-1/2 z-[100] flex flex-col gap-4">
      <AnimatePresence>
        {requests.map((req) => {
          const isExtension = req.type === 'ExtensionRequested';
          const isPackage = req.type === 'PackageChangeRequested';
          const hm = (m) => `${Math.floor(m / 60)} hr${m % 60 ? ` ${m % 60} min` : ''}`;
          const isOperatorCall = req.type === 'OperatorCall';
          const pcId = req.pcId || req.PcId;
          const pcName = req.resolvedPcName || pcId;
          const customerName = req.customerName || req.CustomerName || 'Walk-in';
          const duration = req.duration || req.Duration || 0;
          const billNumber = req.billNumber || req.BillNumber;
          const serverPrice = req.expectedAmount ?? req.ExpectedAmount;
          const durationLabel = duration === 0 ? 'Pay As You Go'
            : duration % 60 === 0 ? `${duration / 60} Hr` : `${duration} Min`;
          return (
            <motion.div
              key={pcId + req.type}
              initial={{ opacity: 0, y: 50, scale: 0.9 }}
              animate={{ opacity: 1, y: 0, scale: 1 }}
              exit={{ opacity: 0, scale: 0.9 }}
              className="bg-bg-2 border border-accent shadow-[0_0_30px_rgba(220,38,38,0.3)] rounded-lg p-5 w-[400px]"
            >
              <div className="flex items-center gap-3 mb-4">
                <div className="w-10 h-10 bg-accent/20 rounded-full flex items-center justify-center shrink-0">
                  <Bell className="w-5 h-5 text-accent animate-pulse" />
                </div>
                <div>
                  <h3 className="font-heading font-bold text-text uppercase tracking-widest text-sm">
                    {isOperatorCall ? 'Operator Call' : isPackage ? 'Package Change' : isExtension ? 'Time Extension' : 'Walk-in Request'}
                  </h3>
                  {isExtension || isPackage ? (
                    <>
                      <p className="text-text font-heading font-bold text-base mt-0.5">
                        <span className="text-accent">{pcName}</span> — {customerName}
                      </p>
                      {billNumber && <p className="text-text-3 font-mono text-[10px]">{billNumber}</p>}
                    </>
                  ) : (
                  <p className="text-text-2 font-body text-xs mt-0.5">
                    Station <span className="text-accent font-semibold">{pcName}</span>
                    {req.resolvedBranchName && (
                      <span className="text-text-3"> • {req.resolvedBranchName}</span>
                    )}
                  </p>
                  )}
                </div>
              </div>

              {isPackage && (
              <div className="bg-bg-3 border border-border p-3 rounded-md mb-5 space-y-2">
                <div className="flex justify-between items-center">
                  <span className="text-text-3 font-body text-xs">Package</span>
                  <span className="text-text font-bold font-heading">{req.fromName} → {req.toName}</span>
                </div>
                <div className="flex justify-between items-center">
                  <span className="text-text-3 font-body text-xs">Bill</span>
                  <span className="font-mono font-bold text-sm text-accent">₹{Number(req.fromPrice).toFixed(0)} → ₹{Number(req.toPrice).toFixed(0)}</span>
                </div>
                <div className="flex justify-between items-center">
                  <span className="text-text-3 font-body text-xs">Time</span>
                  <span className="text-text font-mono text-sm">Played {hm(req.playedMinutes)} · {hm(req.minutesLeftAfter)} left</span>
                </div>
              </div>
              )}

              {!isOperatorCall && !isPackage && (
              <div className="bg-bg-3 border border-border p-3 rounded-md mb-5">
                <div className="flex justify-between items-center mb-2">
                  <span className="text-text-3 font-body text-xs">Customer</span>
                  <span className="text-text font-bold font-heading">{customerName}</span>
                </div>
                <div className="flex justify-between items-center mb-2">
                  <span className="text-text-3 font-body text-xs">Duration</span>
                  <span className="text-text font-mono font-bold text-sm">
                    {durationLabel}
                  </span>
                </div>
                <div className="flex justify-between items-center">
                  <span className="text-text-3 font-body text-xs">Expected Bill</span>
                  <span className="text-text font-mono font-bold text-sm text-accent">
                    {duration === 0 ? 'Variable'
                      : serverPrice != null ? `₹${Number(serverPrice).toFixed(0)}`
                      : `₹${(duration / 60) * (req.resolvedRatePerHour || 100)}`}
                  </span>
                </div>
              </div>
              )}

              <div className="flex gap-3">
                {isOperatorCall ? (
                  <button
                    onClick={() => handleAcknowledgeCall(req)}
                    disabled={isProcessing}
                    className="flex-1 bg-accent hover:bg-accent-dark text-white rounded-sm py-2 text-sm font-semibold flex items-center justify-center gap-2 disabled:opacity-50 shadow-[0_0_15px_rgba(220,38,38,0.4)]"
                  >
                    <Check className="w-4 h-4" /> Acknowledge
                  </button>
                ) : (
                  <>
                    <button
                      onClick={() => handleDecline(req)}
                      disabled={isProcessing}
                      className="flex-1 bg-bg-3 hover:bg-bg-3/80 text-text-2 border border-border hover:border-text-3 transition-colors rounded-sm py-2 text-sm font-semibold flex items-center justify-center gap-2 disabled:opacity-50"
                    >
                      <X className="w-4 h-4" /> Decline
                    </button>
                    <button
                      onClick={() => handleApprove(req)}
                      disabled={isProcessing}
                      className="flex-1 bg-accent hover:bg-accent-dark text-white rounded-sm py-2 text-sm font-semibold shadow-[0_0_15px_rgba(220,38,38,0.4)] flex items-center justify-center gap-2 disabled:opacity-50"
                    >
                      {isProcessing ? <Loader2 className="w-4 h-4 animate-spin" /> : <Check className="w-4 h-4" />}
                      Approve
                    </button>
                  </>
                )}
              </div>
            </motion.div>
          );
        })}
      </AnimatePresence>
    </div>
  );
}
