import { useState, useEffect, useCallback, useRef } from 'react';
import { useLocation } from 'react-router-dom';
import { UtensilsCrossed, Plus, LayoutGrid, List, Bell, Volume2, VolumeX, Settings, Calendar, Download, History, X } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useBranch } from '../../contexts/BranchContext';
import { useSocket } from '../../contexts/SocketContext';
import api from '../../config/api';
import PageHeader from '../../components/layout/PageHeader';
import { format } from 'date-fns';
import { createReport, addTable, save } from '../../utils/pdfReport';

import FoodOrderKanban from '../../components/food/FoodOrderKanban';
import FoodOrderQueue from '../../components/food/FoodOrderQueue';
import CreateFoodOrderModal from '../../components/food/CreateFoodOrderModal';

const todayIso = () => format(new Date(), 'yyyy-MM-dd');

// Same per-browser remembered range as the other desks.
const readStoredDate = (key) => {
  try {
    return localStorage.getItem(key) || todayIso();
  } catch {
    return todayIso();
  }
};

// Synth beep notification sound
const playNotificationSound = () => {
  try {
    const audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    const oscillator = audioCtx.createOscillator();
    const gainNode = audioCtx.createGain();
    
    oscillator.connect(gainNode);
    gainNode.connect(audioCtx.destination);
    
    oscillator.type = 'sine';
    oscillator.frequency.setValueAtTime(880, audioCtx.currentTime); // A5 note
    gainNode.gain.setValueAtTime(0.1, audioCtx.currentTime);
    
    oscillator.start();
    oscillator.stop(audioCtx.currentTime + 0.2);
  } catch (err) {
    console.warn('AudioContext failed to play sound:', err);
  }
};

export default function FoodOrdersPage() {
  const { isSuperAdmin, user } = useAuth();
  const { activeBranch } = useBranch();
  const { subscribe, connected, SIGNALR_HUBS } = useSocket();

  const [orders, setOrders] = useState([]);
  const [isLoading, setIsLoading] = useState(true);
  const [isCreateModalOpen, setIsCreateModalOpen] = useState(false);
  const [isHistoryOpen, setIsHistoryOpen] = useState(false);

  // Settings: viewMode (kanban vs queue), soundEnabled
  const [viewMode, setViewMode] = useState(() => localStorage.getItem('food_order_view_mode') || 'queue');
  const [soundEnabled, setSoundEnabled] = useState(() => localStorage.getItem('food_order_sound_enabled') !== 'false');

  const prevPendingCount = useRef(0);

  const targetBranchId = isSuperAdmin ? activeBranch?.id : user?.branchId;

  // Read-only history, separate from the live board above - GetActiveOrders (what the board
  // reads) deliberately excludes finished orders, so a past-day lookup needs its own endpoint.
  const [historyFrom, setHistoryFrom] = useState(() => readStoredDate('foodOrders.historyFrom'));
  const [historyTo, setHistoryTo] = useState(() => readStoredDate('foodOrders.historyTo'));
  const [history, setHistory] = useState([]);
  const [historyLoading, setHistoryLoading] = useState(false);

  useEffect(() => {
    try { localStorage.setItem('foodOrders.historyFrom', historyFrom); } catch { /* ignore */ }
  }, [historyFrom]);

  useEffect(() => {
    try { localStorage.setItem('foodOrders.historyTo', historyTo); } catch { /* ignore */ }
  }, [historyTo]);

  const fetchHistory = useCallback(async () => {
    if (isSuperAdmin && !targetBranchId) {
      setHistory([]);
      return;
    }
    setHistoryLoading(true);
    try {
      const { data } = await api.get('/food-orders/history', {
        params: { branchId: targetBranchId, fromDate: historyFrom, toDate: historyTo },
      });
      setHistory(data?.data || []);
    } catch (err) {
      console.error('Failed to load food order history:', err);
    } finally {
      setHistoryLoading(false);
    }
  }, [targetBranchId, isSuperAdmin, historyFrom, historyTo]);

  useEffect(() => { fetchHistory(); }, [fetchHistory]);

  const resetHistoryToToday = () => {
    setHistoryFrom(todayIso());
    setHistoryTo(todayIso());
  };

  const handleDownloadHistoryPdf = () => {
    if (history.length === 0) return;
    const rangeLabel = historyFrom === historyTo ? historyFrom : `${historyFrom} to ${historyTo}`;
    const subtitle = `${activeBranch?.name || 'Branch'}  •  ${rangeLabel}`;
    const { doc } = createReport({ title: 'Food Orders History', subtitle });
    const total = history.reduce((sum, o) => sum + (o.totalAmount || 0), 0);

    addTable(doc, 90, {
      title: 'Food Orders History', subtitle,
      heading: `Total: Rs ${total.toFixed(2)}`,
      head: ['Order #', 'Time', 'PC', 'Customer', 'Items', 'Status', 'Amount'],
      body: history.map(o => [
        o.orderNumber || '-',
        o.orderTime ? format(new Date(o.orderTime), 'MMM d, hh:mm a') : '-',
        o.pcNumber || '-',
        o.customerName || 'Walk-in',
        (o.items || []).map(i => `${i.quantity}x ${i.itemName}`).join(', '),
        o.status,
        `Rs ${(o.totalAmount || 0).toFixed(2)}`,
      ]),
    });

    save(doc, `food-orders-history-${historyFrom}${historyFrom !== historyTo ? `_to_${historyTo}` : ''}.pdf`);
  };

  const { state } = useLocation();
  const autoSelectPcId = state?.autoSelectPcId;

  // Auto-open create modal if navigating from PC card
  useEffect(() => {
    if (autoSelectPcId) {
      setIsCreateModalOpen(true);
    }
  }, [autoSelectPcId]);

  // Request browser notification permission
  useEffect(() => {
    if (typeof window !== 'undefined' && 'Notification' in window && Notification.permission === 'default') {
      Notification.requestPermission();
    }
  }, []);

  const fetchOrders = useCallback(async () => {
    if (isSuperAdmin && !targetBranchId) {
      setOrders([]);
      setIsLoading(false);
      return;
    }

    try {
      const { data } = await api.get('/food-orders', { params: { page: 1, pageSize: 200, branchId: targetBranchId } });
      setOrders(data?.data?.items || []);
    } catch (err) {
      console.error("Failed to load food orders:", err);
    } finally {
      setIsLoading(false);
    }
  }, [targetBranchId, isSuperAdmin]);

  useEffect(() => {
    setIsLoading(true);
    fetchOrders();
  }, [fetchOrders]);

  // Real-Time SignalR
  useEffect(() => {
    if (!connected || !targetBranchId) return;

    const unsubFood = subscribe(SIGNALR_HUBS.FOOD_ORDERS, 'FoodOrderUpdated', () => {
      fetchOrders();
    });

    const unsubNewFood = subscribe(SIGNALR_HUBS.FOOD_ORDERS, 'NewFoodOrder', () => {
      fetchOrders();
    });

    return () => {
      unsubFood();
      unsubNewFood();
    };
  }, [connected, subscribe, SIGNALR_HUBS.FOOD_ORDERS, targetBranchId, fetchOrders]);

  // Fallback Polling (every 5 seconds when SignalR is offline)
  useEffect(() => {
    if (!targetBranchId) return;

    if (!connected) {
      console.log("[SignalR Offline] Starting fallback polling (5s) for Food Orders...");
      const interval = setInterval(() => {
        fetchOrders();
      }, 5000);
      return () => clearInterval(interval);
    }
  }, [connected, targetBranchId, fetchOrders]);

  const pendingOrders = orders.filter(o => o.status === 0 || o.status === 'Pending');

  // Handle settings updates
  const handleToggleView = (mode) => {
    setViewMode(mode);
    localStorage.setItem('food_order_view_mode', mode);
  };

  const handleToggleSound = () => {
    setSoundEnabled(prev => {
      localStorage.setItem('food_order_sound_enabled', String(!prev));
      return !prev;
    });
  };

  // Filter out orders that are not from today
  const activeOrders = orders.filter(o => {
    if (!o.orderTime) return true; // fallback
    const orderDate = new Date(o.orderTime);
    const today = new Date();
    return (
      orderDate.getDate() === today.getDate() &&
      orderDate.getMonth() === today.getMonth() &&
      orderDate.getFullYear() === today.getFullYear()
    );
  });

  if (isSuperAdmin && !activeBranch) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
        <UtensilsCrossed className="w-12 h-12 text-text-3 mb-4" />
        <h2 className="text-xl font-heading font-bold text-text mb-2">Select a Branch</h2>
        <p className="text-text-2">You must select a branch to view the Food Orders Dashboard.</p>
      </div>
    );
  }

  const handleTestNotification = () => {
    if (soundEnabled) {
      playNotificationSound();
    }
    
    if (typeof window !== 'undefined' && 'Notification' in window) {
      if (Notification.permission === 'granted') {
        new Notification("TEST NOTIFICATION", {
          body: "New food order from Station PC-01! (This is a test)",
          icon: "/favicon.png"
        });
      } else if (Notification.permission !== 'denied') {
        Notification.requestPermission().then(permission => {
          if (permission === 'granted') {
            new Notification("TEST NOTIFICATION", {
              body: "New food order from Station PC-01! (This is a test)",
              icon: "/favicon.png"
            });
          }
        });
      }
    }
  };

  return (
    <div className="flex flex-col h-full bg-bg">
      <div className="flex flex-col md:flex-row md:items-center justify-between gap-4 p-6 shrink-0 border-b border-border bg-bg-2 shadow-sm">
        <PageHeader 
          title="Food Orders" 
          subtitle="Manage active kitchen/pantry requests"
          icon="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4"
          badge="LIVE"
        />
        
        {/* Header Controls Panel */}
        <div className="flex flex-wrap items-center gap-3 w-full md:w-auto">
          {/* Bell Icon with Badge Count */}
          <button 
            onClick={handleTestNotification}
            title="Click to test notification sound and popup"
            className="relative p-2 rounded-lg bg-bg-3 border border-border flex items-center justify-center text-text-2 hover:border-accent hover:text-accent transition-colors"
          >
            <Bell className={`w-5 h-5 ${pendingOrders.length > 0 ? 'text-neon-orange animate-swing' : ''}`} />
            {pendingOrders.length > 0 && (
              <span className="absolute -top-1.5 -right-1.5 bg-neon-orange text-black font-bold text-[10px] w-5 h-5 rounded-full flex items-center justify-center border-2 border-bg-2 font-mono">
                {pendingOrders.length}
              </span>
            )}
          </button>

          {/* Sound Toggle */}
          <button
            onClick={handleToggleSound}
            title={soundEnabled ? 'Disable Notification Sound' : 'Enable Notification Sound'}
            className="p-2 rounded-lg bg-bg-3 border border-border hover:border-accent text-text-2 hover:text-accent transition-colors"
          >
            {soundEnabled ? <Volume2 className="w-5 h-5" /> : <VolumeX className="w-5 h-5 text-text-3" />}
          </button>

          {/* View Toggles */}
          <div className="flex bg-bg-3 border border-border p-1 rounded-lg">
            <button
              onClick={() => handleToggleView('queue')}
              className={`p-1.5 rounded-md transition-colors ${viewMode === 'queue' ? 'bg-bg-2 text-accent border border-border/30' : 'text-text-3 hover:text-text'}`}
              title="Queue Table View"
            >
              <List className="w-4 h-4" />
            </button>
            <button
              onClick={() => handleToggleView('kanban')}
              className={`p-1.5 rounded-md transition-colors ${viewMode === 'kanban' ? 'bg-bg-2 text-accent border border-border/30' : 'text-text-3 hover:text-text'}`}
              title="Kanban Board View"
            >
              <LayoutGrid className="w-4 h-4" />
            </button>
          </div>

          <button
            onClick={() => setIsHistoryOpen(true)}
            className="btn-secondary flex items-center gap-2 text-xs py-2 px-4 uppercase font-bold"
          >
            <History className="w-4 h-4" /> History
          </button>

          <button
            onClick={() => setIsCreateModalOpen(true)}
            className="btn-primary flex items-center gap-2 shadow-lg shadow-accent/20 text-xs py-2 px-4 uppercase font-bold"
          >
            <Plus className="w-4 h-4" /> New Order
          </button>
        </div>
      </div>

      {/* Main Board Area */}
      <div className="flex-1 min-h-0 relative">
        {isLoading ? (
          <div className="absolute inset-0 flex items-center justify-center">
            <div className="w-8 h-8 rounded-full border-2 border-accent border-t-transparent animate-spin" />
          </div>
        ) : viewMode === 'kanban' ? (
          <FoodOrderKanban orders={activeOrders} onOrderUpdated={fetchOrders} />
        ) : (
          <FoodOrderQueue orders={activeOrders} onOrderUpdated={fetchOrders} />
        )}
      </div>

      {isCreateModalOpen && (
        <CreateFoodOrderModal
          onClose={() => setIsCreateModalOpen(false)}
          onOrderPlaced={fetchOrders}
          initialPcId={autoSelectPcId}
        />
      )}

      {isHistoryOpen && (
        <div className="fixed inset-0 z-[9999] flex items-center justify-center p-4 bg-black/60 backdrop-blur-sm">
          <div className="w-full max-w-4xl max-h-[85vh] bg-bg-2 border border-border rounded-xl shadow-2xl flex flex-col overflow-hidden">
            <div className="px-5 py-4 border-b border-border bg-bg-3 flex items-center justify-between shrink-0">
              <h2 className="font-heading font-bold text-text uppercase tracking-wider text-base flex items-center gap-2">
                <History className="w-4 h-4 text-accent" /> Food Orders History
              </h2>
              <button onClick={() => setIsHistoryOpen(false)} className="p-1 text-text-3 hover:text-text rounded transition-colors">
                <X className="w-5 h-5" />
              </button>
            </div>

            <div className="p-5 flex flex-wrap items-center gap-3 border-b border-border shrink-0">
              <Calendar className="w-4 h-4 text-text-3 shrink-0" />
              <div className="flex items-center gap-2">
                <label className="text-xs text-text-3 uppercase tracking-wider">From</label>
                <input
                  type="date"
                  value={historyFrom}
                  max={historyTo}
                  onChange={(e) => setHistoryFrom(e.target.value)}
                  className="bg-bg-3 border border-border rounded-lg px-3 py-1.5 text-sm text-text"
                />
              </div>
              <div className="flex items-center gap-2">
                <label className="text-xs text-text-3 uppercase tracking-wider">To</label>
                <input
                  type="date"
                  value={historyTo}
                  min={historyFrom}
                  max={todayIso()}
                  onChange={(e) => setHistoryTo(e.target.value)}
                  className="bg-bg-3 border border-border rounded-lg px-3 py-1.5 text-sm text-text"
                />
              </div>
              <button onClick={resetHistoryToToday} className="text-xs font-medium text-accent hover:text-accent/80 transition-colors ml-auto">
                Today
              </button>
              <button
                onClick={handleDownloadHistoryPdf}
                disabled={history.length === 0}
                className="btn-secondary py-1.5 px-3 flex items-center gap-1.5 text-xs font-bold disabled:opacity-50 disabled:cursor-not-allowed"
              >
                <Download className="w-3.5 h-3.5" /> Download PDF
              </button>
            </div>

            <div className="flex-1 overflow-y-auto p-5">
              {historyLoading ? (
                <div className="flex justify-center py-12">
                  <div className="w-6 h-6 border-2 border-accent border-t-transparent rounded-full animate-spin" />
                </div>
              ) : history.length === 0 ? (
                <div className="text-center text-text-3 text-xs italic py-8 border border-dashed border-border rounded-lg">
                  No food orders found in this range.
                </div>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
                    <thead>
                      <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                        <th className="py-2 px-3">Order #</th>
                        <th className="py-2 px-3">Time</th>
                        <th className="py-2 px-3">PC</th>
                        <th className="py-2 px-3">Customer</th>
                        <th className="py-2 px-3">Items</th>
                        <th className="py-2 px-3 text-center">Status</th>
                        <th className="py-2 px-3 text-right">Amount</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-border/40 font-mono">
                      {history.map(o => (
                        <tr key={o.id}>
                          <td className="py-2 px-3 text-text-2">{o.orderNumber}</td>
                          <td className="py-2 px-3 text-text-2">{o.orderTime ? format(new Date(o.orderTime), 'MMM d, hh:mm a') : '-'}</td>
                          <td className="py-2 px-3 text-text font-bold">{o.pcNumber || '-'}</td>
                          <td className="py-2 px-3 text-text-2 font-sans">{o.customerName || 'Walk-in'}</td>
                          <td className="py-2 px-3 text-text-3 font-sans whitespace-normal max-w-xs">
                            {(o.items || []).map(i => `${i.quantity}x ${i.itemName}`).join(', ')}
                          </td>
                          <td className="py-2 px-3 text-center text-text-3 uppercase">{o.status}</td>
                          <td className="py-2 px-3 text-right text-text font-bold">₹{(o.totalAmount || 0).toFixed(2)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
