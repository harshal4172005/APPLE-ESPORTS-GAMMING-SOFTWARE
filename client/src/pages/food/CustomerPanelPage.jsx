import { useState, useEffect, useCallback } from 'react';
import { useStockVersion } from '../../components/food/SharedStockLive';
import { motion, AnimatePresence } from 'framer-motion';
import { 
  UtensilsCrossed, 
  ShoppingCart, 
  Plus, 
  Minus, 
  Monitor, 
  User, 
  Lock, 
  Clock, 
  Sparkles, 
  Coffee, 
  CupSoda, 
  Pizza, 
  CheckCircle, 
  RefreshCw,
  LogOut
} from 'lucide-react';
import api from '../../config/api';
import axios from 'axios';
import { useSocket } from '../../contexts/SocketContext';

export default function CustomerPanelPage() {
  const { subscribe, connected, SIGNALR_HUBS } = useSocket();

  // Authentication & Station State
  const [branches, setBranches] = useState([]);
  const [selectedBranchId, setSelectedBranchId] = useState('');
  const [activeSessions, setActiveSessions] = useState([]);
  const [selectedSessionId, setSelectedSessionId] = useState('');
  const [pcNumber, setPcNumber] = useState('');
  const [customerName, setCustomerName] = useState('');
  const [isInitialized, setIsInitialized] = useState(false);
  const [member, setMember] = useState(null);

  // Login credentials (for member checkout/ordering if needed)
  const [isMemberMode, setIsMemberMode] = useState(false);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [authError, setAuthError] = useState('');

  // Menu & Cart State
  const [inventory, setInventory] = useState([]);
  const [activeTab, setActiveTab] = useState('all'); // all, cold, hot, snacks
  const [cart, setCart] = useState([]); // { item, quantity }
  const [orders, setOrders] = useState([]);
  const [loading, setLoading] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(null);

  // Direct order state & Confirmation details
  const [itemQuantities, setItemQuantities] = useState({});
  const [confirmationData, setConfirmationData] = useState(null);
  const [directOrderModalItem, setDirectOrderModalItem] = useState(null);

  // Fetch branches on mount
  useEffect(() => {
    const fetchBranches = async () => {
      try {
        const response = await api.get('/auth/branches');
        setBranches(response.data?.data || []);
        if (response.data?.data?.length > 0) {
          setSelectedBranchId(response.data.data[0].id);
        }
      } catch (err) {
        console.error('Failed to load branches:', err);
      }
    };
    fetchBranches();
  }, []);

  // Fetch active sessions for selected branch
  useEffect(() => {
    if (!selectedBranchId) return;
    const fetchSessions = async () => {
      try {
        const response = await api.get('/sessions', { 
          params: { branchId: selectedBranchId, page: 1, pageSize: 100 } 
        });
        const active = (response.data?.data?.items || []).filter(
          s => s.status === 1 || s.status === 'Active'
        );
        setActiveSessions(active);
      } catch (err) {
        console.warn('Failed to load active sessions for selection');
      }
    };
    fetchSessions();
  }, [selectedBranchId]);

  // Load Menu and Orders when initialized
  const loadMenuAndOrders = useCallback(async () => {
    if (!selectedBranchId) return;
    setLoading(true);
    setError(null);
    try {
      // Get all active items
      const invRes = await api.get('/inventory', { 
        params: { branchId: selectedBranchId, includeAll: false } 
      });
      setInventory(invRes.data?.data || []);

      // Get customer's orders
      const orderRes = await api.get('/food-orders', { 
        params: { branchId: selectedBranchId, page: 1, pageSize: 100 } 
      });
      
      const allOrders = orderRes.data?.data?.items || [];
      // Filter orders by either session or pc/name matching this user
      const userOrders = allOrders.filter(o => {
        if (selectedSessionId && o.sessionId === selectedSessionId) return true;
        if (pcNumber && o.pcNumber === pcNumber && o.customerName === customerName) return true;
        if (member && o.customerName === member.fullName) return true;
        return false;
      });
      setOrders(userOrders);
    } catch (err) {
      setError('Could not retrieve menu or active orders.');
    } finally {
      setLoading(false);
    }
  }, [selectedBranchId, selectedSessionId, pcNumber, customerName, member]);

  const stockVersion = useStockVersion();
  useEffect(() => {
    if (isInitialized) {
      loadMenuAndOrders();
    }
  }, [isInitialized, loadMenuAndOrders, stockVersion]);

  // Real-Time update subscription
  useEffect(() => {
    if (!connected || !isInitialized || !selectedBranchId) return;

    const unsub = subscribe(SIGNALR_HUBS.FOOD_ORDERS, 'FoodOrderUpdated', () => {
      loadMenuAndOrders();
    });

    return () => unsub();
  }, [connected, subscribe, SIGNALR_HUBS.FOOD_ORDERS, isInitialized, selectedBranchId, loadMenuAndOrders]);

  // Fallback Polling (every 8s if socket is offline or guest is unauthenticated)
  useEffect(() => {
    if (!isInitialized || !selectedBranchId) return;

    if (!connected) {
      console.log("[SignalR Offline] Starting fallback polling (8s) for Customer Menu...");
      const interval = setInterval(() => {
        loadMenuAndOrders();
      }, 8000);
      return () => clearInterval(interval);
    }
  }, [connected, isInitialized, selectedBranchId, loadMenuAndOrders]);

  // Handle Member Login
  const handleMemberLogin = async (e) => {
    e.preventDefault();
    setAuthError('');
    try {
      const res = await axios.post('/api/members/login', { identifier: username, password });
      const memberData = res.data?.data;
      if (memberData) {
        localStorage.setItem('accessToken', memberData.token);
        setMember(memberData);
        setCustomerName(memberData.fullName);
        
        // Link to active session if exists
        const matchedSession = activeSessions.find(s => s.customerName === memberData.fullName);
        if (matchedSession) {
          setSelectedSessionId(matchedSession.id);
          setPcNumber(matchedSession.pcNumber || '');
        }
        
        setIsInitialized(true);
      }
    } catch (err) {
      setAuthError(err.response?.data?.message || 'Invalid member credentials.');
    }
  };

  // Initialize guest station
  const handleGuestInit = (e) => {
    e.preventDefault();
    if (!pcNumber) {
      setError('Please provide PC/Station information.');
      return;
    }
    
    // Check if session was selected
    if (selectedSessionId) {
      const sess = activeSessions.find(s => s.id === selectedSessionId);
      if (sess) {
        setCustomerName(sess.customerName || 'Guest');
        setPcNumber(sess.pcNumber || '');
      }
    } else {
      if (!customerName) {
        setCustomerName('Guest');
      }
    }

    setIsInitialized(true);
  };

  // Cart operations
  const addToCart = (item) => {
    if (item.currentStock < 1 || item.status === 'OutOfStock') return;
    
    setCart(prev => {
      const existing = prev.find(i => i.item.id === item.id);
      if (existing) {
        if (existing.quantity >= item.currentStock) return prev;
        return prev.map(i => i.item.id === item.id ? { ...i, quantity: i.quantity + 1 } : i);
      }
      return [...prev, { item, quantity: 1 }];
    });
  };

  const removeFromCart = (itemId) => {
    setCart(prev => {
      const existing = prev.find(i => i.item.id === itemId);
      if (!existing) return prev;
      if (existing.quantity > 1) {
        return prev.map(i => i.item.id === itemId ? { ...i, quantity: i.quantity - 1 } : i);
      }
      return prev.filter(i => i.item.id !== itemId);
    });
  };

  const cartTotal = cart.reduce((sum, item) => sum + (item.item.price * item.quantity), 0);

  // Direct Order helpers
  const adjustItemQty = (itemId, delta, maxStock) => {
    setItemQuantities(prev => {
      const current = prev[itemId] || 1;
      const next = current + delta;
      if (next < 1 || next > maxStock) return prev;
      return { ...prev, [itemId]: next };
    });
  };

  const handlePlaceDirectOrder = async (item, quantity) => {
    setSubmitting(true);
    setError(null);
    try {
      let matchedPcId = null;
      if (selectedSessionId) {
        const sess = activeSessions.find(s => s.id === selectedSessionId);
        if (sess) matchedPcId = sess.pcId;
      }

      const payload = {
        sessionId: selectedSessionId || null,
        pcId: matchedPcId,
        customerName: customerName || 'Guest',
        items: [{
          inventoryId: item.id,
          quantity: quantity
        }]
      };

      const res = await api.post('/food-orders', payload);
      const placedOrder = res.data?.data;
      
      setItemQuantities(prev => ({ ...prev, [item.id]: 1 }));
      setDirectOrderModalItem(null);

      if (placedOrder) {
        setConfirmationData({
          orderNumber: placedOrder.orderNumber,
          totalAmount: placedOrder.totalAmount,
          pcNumber: pcNumber || 'Walk-in',
          items: `${item.itemName} (x${quantity})`
        });
      }

      loadMenuAndOrders();
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Failed to place order.');
    } finally {
      setSubmitting(false);
    }
  };

  // Submit Order from Cart
  const handlePlaceOrder = async () => {
    if (cart.length === 0) return;
    setSubmitting(true);
    setError(null);

    try {
      let matchedPcId = null;
      if (selectedSessionId) {
        const sess = activeSessions.find(s => s.id === selectedSessionId);
        if (sess) matchedPcId = sess.pcId;
      }

      const payload = {
        sessionId: selectedSessionId || null,
        pcId: matchedPcId,
        customerName: customerName || 'Guest',
        items: cart.map(c => ({
          inventoryId: c.item.id,
          quantity: c.quantity
        }))
      };

      const res = await api.post('/food-orders', payload);
      const placedOrder = res.data?.data;
      
      const cartItemsSummary = cart.map(c => `${c.item.itemName} (x${c.quantity})`).join(', ');
      setCart([]);
      
      if (placedOrder) {
        setConfirmationData({
          orderNumber: placedOrder.orderNumber,
          totalAmount: placedOrder.totalAmount,
          pcNumber: pcNumber || 'Walk-in',
          items: cartItemsSummary
        });
      }
      
      loadMenuAndOrders();
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Failed to place order.');
    } finally {
      setSubmitting(false);
    }
  };

  const handleLogout = () => {
    localStorage.removeItem('accessToken');
    setMember(null);
    setIsInitialized(false);
    setCart([]);
    setOrders([]);
    setCustomerName('');
    setPcNumber('');
    setSelectedSessionId('');
  };

  // Category filters
  const filteredInventory = inventory.filter(item => {
    if (activeTab === 'all') return true;
    const cat = (item.category || '').toLowerCase();
    if (activeTab === 'cold') return cat.includes('cold') || cat.includes('drink') || cat.includes('beverage');
    if (activeTab === 'hot') return cat.includes('hot') || cat.includes('tea') || cat.includes('coffee');
    if (activeTab === 'snacks') return cat.includes('snack') || cat.includes('food') || cat.includes('pizza') || cat.includes('burger');
    return true;
  });

  // Render initialization panel
  if (!isInitialized) {
    return (
      <div className="min-h-screen bg-bg-1 text-text flex items-center justify-center p-4 relative overflow-hidden">
        {/* Glow Effects */}
        <div className="absolute top-1/4 left-1/4 w-96 h-96 bg-accent/10 rounded-full blur-3xl" />
        <div className="absolute bottom-1/4 right-1/4 w-96 h-96 bg-neon-blue/10 rounded-full blur-3xl" />

        <div className="w-full max-w-md bg-bg-2/80 backdrop-blur-xl border border-border p-8 rounded-2xl shadow-2xl relative z-10">
          <div className="flex flex-col items-center text-center mb-8">
            <div className="w-16 h-16 rounded-2xl bg-gradient-to-tr from-accent to-neon-blue p-0.5 shadow-lg shadow-accent/20 mb-4 flex items-center justify-center">
              <div className="w-full h-full rounded-[14px] bg-bg-2 flex items-center justify-center">
                <UtensilsCrossed className="w-8 h-8 text-accent animate-pulse" />
              </div>
            </div>
            <h1 className="font-heading font-extrabold text-2xl tracking-wider text-text uppercase">
              Apple Esports <span className="text-accent">Cafe Menu</span>
            </h1>
            <p className="text-text-3 text-sm mt-1">Order food & drinks directly to your PC</p>
          </div>

          {/* Toggle Member/Guest Modes */}
          <div className="flex bg-bg-3 p-1 rounded-lg border border-border mb-6">
            <button
              onClick={() => { setIsMemberMode(false); setAuthError(''); }}
              className={`flex-1 py-2 rounded-md font-heading font-bold text-xs uppercase tracking-wider transition-all ${
                !isMemberMode ? 'bg-bg-2 text-accent border border-border/30' : 'text-text-3 hover:text-text'
              }`}
            >
              🎮 Guest/Walk-in
            </button>
            <button
              onClick={() => { setIsMemberMode(true); setAuthError(''); }}
              className={`flex-1 py-2 rounded-md font-heading font-bold text-xs uppercase tracking-wider transition-all ${
                isMemberMode ? 'bg-bg-2 text-accent border border-border/30' : 'text-text-3 hover:text-text'
              }`}
            >
              🔒 Member Login
            </button>
          </div>

          {isMemberMode ? (
            <form onSubmit={handleMemberLogin} className="space-y-4">
              {authError && (
                <div className="p-3 bg-neon-red/10 border border-neon-red/30 rounded-lg text-neon-red text-xs">
                  {authError}
                </div>
              )}
              <div className="space-y-1">
                <label className="text-[10px] uppercase font-bold text-text-3 tracking-wider flex items-center gap-1.5">
                  <User className="w-3.5 h-3.5" /> Username
                </label>
                <input
                  type="text"
                  required
                  value={username}
                  onChange={e => setUsername(e.target.value)}
                  placeholder="Enter your member username"
                  className="w-full bg-bg-3 border border-border text-text text-sm rounded-lg p-3 focus:border-accent outline-none transition-colors"
                />
              </div>
              <div className="space-y-1">
                <label className="text-[10px] uppercase font-bold text-text-3 tracking-wider flex items-center gap-1.5">
                  <Lock className="w-3.5 h-3.5" /> Password
                </label>
                <input
                  type="password"
                  required
                  value={password}
                  onChange={e => setPassword(e.target.value)}
                  placeholder="••••••••"
                  className="w-full bg-bg-3 border border-border text-text text-sm rounded-lg p-3 focus:border-accent outline-none transition-colors"
                />
              </div>
              <button
                type="submit"
                className="w-full btn-primary py-3.5 font-bold uppercase tracking-wider rounded-lg mt-6 shadow-lg shadow-accent/25 hover:shadow-accent/40"
              >
                Sign In & Browse
              </button>
            </form>
          ) : (
            <form onSubmit={handleGuestInit} className="space-y-4">
              <div className="space-y-1">
                <label className="text-[10px] uppercase font-bold text-text-3 tracking-wider">
                  Select Branch
                </label>
                <select
                  value={selectedBranchId}
                  onChange={e => setSelectedBranchId(e.target.value)}
                  className="w-full bg-bg-3 border border-border text-text text-sm rounded-lg p-3 focus:border-accent outline-none transition-colors"
                >
                  {branches.map(b => (
                    <option key={b.id} value={b.id}>{b.name}</option>
                  ))}
                </select>
              </div>

              <div className="space-y-1">
                <label className="text-[10px] uppercase font-bold text-text-3 tracking-wider flex items-center gap-1.5">
                  <Monitor className="w-3.5 h-3.5" /> Link to Session (PC)
                </label>
                <select
                  value={selectedSessionId}
                  onChange={e => {
                    setSelectedSessionId(e.target.value);
                    if (e.target.value) {
                      const sess = activeSessions.find(s => s.id === e.target.value);
                      if (sess) {
                        setPcNumber(sess.pcNumber || '');
                        setCustomerName(sess.customerName || '');
                      }
                    } else {
                      setPcNumber('');
                      setCustomerName('');
                    }
                  }}
                  className="w-full bg-bg-3 border border-border text-text text-sm rounded-lg p-3 focus:border-accent outline-none transition-colors"
                >
                  <option value="">Guest Walk-In (Not Linked)</option>
                  {activeSessions.map(s => (
                    <option key={s.id} value={s.id}>
                      {s.pcName} — {s.customerName}
                    </option>
                  ))}
                </select>
              </div>

              {!selectedSessionId && (
                <>
                  <div className="grid grid-cols-2 gap-4">
                    <div className="space-y-1">
                      <label className="text-[10px] uppercase font-bold text-text-3 tracking-wider">
                        PC / Table Number
                      </label>
                      <input
                        type="text"
                        required
                        placeholder="e.g. PC-05"
                        value={pcNumber}
                        onChange={e => setPcNumber(e.target.value)}
                        className="w-full bg-bg-3 border border-border text-text text-sm rounded-lg p-3 focus:border-accent outline-none transition-colors"
                      />
                    </div>
                    <div className="space-y-1">
                      <label className="text-[10px] uppercase font-bold text-text-3 tracking-wider">
                        Your Name
                      </label>
                      <input
                        type="text"
                        placeholder="Optional"
                        value={customerName}
                        onChange={e => setCustomerName(e.target.value)}
                        className="w-full bg-bg-3 border border-border text-text text-sm rounded-lg p-3 focus:border-accent outline-none transition-colors"
                      />
                    </div>
                  </div>
                </>
              )}

              <button
                type="submit"
                className="w-full btn-primary py-3.5 font-bold uppercase tracking-wider rounded-lg mt-6 shadow-lg shadow-accent/25 hover:shadow-accent/40"
              >
                Enter Menu
              </button>
            </form>
          )}
        </div>
      </div>
    );
  }

  // Render core Customer Menu Panel
  return (
    <div className="min-h-screen bg-bg-1 text-text flex flex-col md:flex-row">
      {/* Menu Area */}
      <div className="flex-1 flex flex-col p-4 md:p-6 min-w-0">
        
        {/* Header */}
        <div className="flex justify-between items-center mb-6 pb-4 border-b border-border">
          <div>
            <h1 className="font-heading font-extrabold text-2xl tracking-wider text-text uppercase flex items-center gap-2">
              <Sparkles className="w-6 h-6 text-accent animate-spin-slow" />
              Menu
            </h1>
            <p className="text-text-3 text-xs">
              Branch: {branches.find(b => b.id === selectedBranchId)?.name || 'Local'} | Station:{' '}
              <span className="text-accent font-bold">{pcNumber || 'Walk-in'}</span>
              {customerName && ` (${customerName})`}
            </p>
          </div>

          <button
            onClick={handleLogout}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-bg-3 border border-border hover:border-neon-red/40 hover:text-neon-red transition-all text-xs"
          >
            <LogOut className="w-4 h-4" /> Change Station
          </button>
        </div>

        {/* Categories Tabs */}
        <div className="flex gap-2 overflow-x-auto pb-4 mb-6 border-b border-border">
          {[
            { id: 'all', label: 'All Menu', icon: <UtensilsCrossed className="w-4 h-4" /> },
            { id: 'cold', label: 'Cold Drinks', icon: <CupSoda className="w-4 h-4" /> },
            { id: 'hot', label: 'Hot Drinks', icon: <Coffee className="w-4 h-4" /> },
            { id: 'snacks', label: 'Snacks', icon: <Pizza className="w-4 h-4" /> }
          ].map(tab => (
            <button
              key={tab.id}
              onClick={() => setActiveTab(tab.id)}
              className={`flex items-center gap-2 px-4 py-2.5 rounded-full border font-bold text-xs uppercase tracking-wider whitespace-nowrap transition-all ${
                activeTab === tab.id 
                  ? 'bg-accent/10 border-accent text-accent shadow-md shadow-accent/5' 
                  : 'bg-bg-2 border-border text-text-3 hover:text-text hover:border-text-3'
              }`}
            >
              {tab.icon}
              {tab.label}
            </button>
          ))}
        </div>

        {/* Menu Grid */}
        <div className="flex-1 overflow-y-auto">
          {loading ? (
            <div className="flex justify-center items-center h-48">
              <div className="w-8 h-8 rounded-full border-2 border-accent border-t-transparent animate-spin" />
            </div>
          ) : filteredInventory.length === 0 ? (
            <div className="text-center text-text-3 text-sm py-12 italic">
              No items available in this category.
            </div>
          ) : (
            <div className="grid grid-cols-2 sm:grid-cols-3 xl:grid-cols-4 gap-4">
              {filteredInventory.map(item => {
                const isOutOfStock = item.currentStock < 1 || item.status === 'OutOfStock';
                const qty = itemQuantities[item.id] || 1;
                return (
                  <div
                    key={item.id}
                    className={`bg-bg-2 border p-4 rounded-xl flex flex-col justify-between transition-all ${
                      isOutOfStock 
                        ? 'border-border/40 opacity-55' 
                        : 'border-border hover:border-accent/30 hover:shadow-lg hover:shadow-accent/5'
                    }`}
                  >
                    <div>
                      {item.imageUrl ? (
                        <img
                          src={item.imageUrl}
                          alt={item.itemName}
                          className="w-full h-24 object-cover rounded-lg mb-3 border border-border"
                        />
                      ) : (
                        <div className="w-full h-24 bg-bg-3 rounded-lg mb-3 flex items-center justify-center border border-border">
                          <UtensilsCrossed className="w-8 h-8 text-text-3/40" />
                        </div>
                      )}
                      <h3 className="font-heading font-extrabold text-sm text-text truncate">{item.itemName}</h3>
                      <p className="text-text-3 text-[10px] uppercase font-mono mt-0.5">{item.category}</p>
                    </div>

                    <div className="mt-4 flex flex-col gap-3">
                      <div className="flex items-center justify-between">
                        <div className="font-mono font-bold text-accent text-base">₹{item.price}</div>
                        {isOutOfStock && (
                          <span className="text-[10px] font-mono px-2 py-1 rounded bg-neon-red/10 text-neon-red">
                            OUT OF STOCK
                          </span>
                        )}
                      </div>

                      {!isOutOfStock && (
                        <div className="flex items-center justify-between gap-1.5 pt-1.5 border-t border-border/20">
                          {/* +/- Qty Selector */}
                          <div className="flex items-center bg-bg-3 rounded border border-border px-1">
                            <button
                              onClick={(e) => { e.stopPropagation(); adjustItemQty(item.id, -1, item.currentStock); }}
                              disabled={qty <= 1}
                              className="p-1 text-text-3 hover:text-accent disabled:opacity-30"
                            >
                              <Minus className="w-3.5 h-3.5" />
                            </button>
                            <span className="font-mono text-xs font-bold w-4 text-center">{qty}</span>
                            <button
                              onClick={(e) => { e.stopPropagation(); adjustItemQty(item.id, 1, item.currentStock); }}
                              disabled={qty >= item.currentStock}
                              className="p-1 text-text-3 hover:text-accent disabled:opacity-30"
                            >
                              <Plus className="w-3.5 h-3.5" />
                            </button>
                          </div>

                          {/* Direct Order Button */}
                          <button
                            onClick={() => setDirectOrderModalItem({ item, quantity: qty })}
                            className="px-2.5 py-1.5 rounded-lg bg-accent text-white font-heading font-bold text-[10px] uppercase hover:bg-accent/80 transition-colors shadow-sm shadow-accent/15"
                          >
                            Order
                          </button>
                        </div>
                      )}
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      </div>

      {/* Cart & Orders Sidebar */}
      <div className="w-full md:w-80 lg:w-96 bg-bg-3 border-t md:border-t-0 md:border-l border-border flex flex-col h-[70vh] md:h-screen sticky top-0 shrink-0">
        
        {/* Cart Tab */}
        <div className="p-4 border-b border-border flex justify-between items-center bg-bg-2">
          <h2 className="font-heading font-bold text-text uppercase tracking-wider text-sm flex items-center gap-2">
            <ShoppingCart className="w-4 h-4 text-accent" /> Cart ({cart.reduce((s, i) => s + i.quantity, 0)})
          </h2>
          <span className="font-mono text-xs text-text-3">Checkout as Link</span>
        </div>

        {/* Cart Items List */}
        <div className="flex-1 overflow-y-auto p-4 space-y-3">
          {error && (
            <div className="p-2 bg-neon-red/10 border border-neon-red/20 rounded-lg text-neon-red text-xs">
              {error}
            </div>
          )}

          {cart.length === 0 ? (
            <div className="text-center text-text-3 text-xs italic py-8">
              Your cart is empty. Add food items to proceed.
            </div>
          ) : (
            <div className="space-y-2">
              {cart.map(c => (
                <div key={c.item.id} className="flex justify-between items-center bg-bg-2 p-2.5 rounded-lg border border-border">
                  <div className="text-xs">
                    <div className="font-bold text-text truncate max-w-[150px]">{c.item.itemName}</div>
                    <div className="font-mono text-text-3 mt-0.5">₹{c.item.price} × {c.quantity}</div>
                  </div>
                  <div className="flex items-center gap-2 bg-bg-3 rounded-md border border-border px-1">
                    <button onClick={() => removeFromCart(c.item.id)} className="p-1 text-text-3 hover:text-accent">
                      <Minus className="w-3.5 h-3.5" />
                    </button>
                    <span className="font-mono text-xs font-bold w-4 text-center">{c.quantity}</span>
                    <button
                      onClick={() => addToCart(c.item)}
                      disabled={c.quantity >= c.item.currentStock}
                      className="p-1 text-text-3 hover:text-accent disabled:opacity-30"
                    >
                      <Plus className="w-3.5 h-3.5" />
                    </button>
                  </div>
                </div>
              ))}

              <div className="pt-4 border-t border-border space-y-3">
                <div className="flex justify-between items-center">
                  <span className="text-text-3 font-bold text-xs uppercase tracking-wider">Subtotal</span>
                  <span className="font-mono font-bold text-lg text-accent">₹{cartTotal}</span>
                </div>
                <button
                  onClick={handlePlaceOrder}
                  disabled={submitting || cart.length === 0}
                  className="w-full btn-primary py-3 rounded-lg uppercase tracking-wider font-heading font-bold text-xs shadow-lg shadow-accent/20 flex items-center justify-center gap-2"
                >
                  {submitting ? (
                    <div className="w-4 h-4 rounded-full border-2 border-white border-t-transparent animate-spin" />
                  ) : (
                    <>✓ Confirm & Order</>
                  )}
                </button>
              </div>
            </div>
          )}

          {/* Active Orders Status Tracking List */}
          <div className="pt-6 border-t border-border">
            <h3 className="font-heading font-extrabold text-xs uppercase tracking-wider text-text-2 mb-3 flex items-center gap-1.5">
              <Clock className="w-3.5 h-3.5 text-accent" /> Track My Orders
            </h3>
            
            {orders.length === 0 ? (
              <div className="text-center text-text-3 text-[11px] italic py-4">
                No orders placed yet.
              </div>
            ) : (
              <div className="space-y-2 max-h-48 overflow-y-auto">
                {orders.map(order => {
                  let statusColor = 'text-text-3 bg-text-3/10';
                  let statusLabel = order.status;
                  if (order.status === 0 || order.status === 'Pending') {
                    statusColor = 'text-neon-orange bg-neon-orange/10';
                    statusLabel = 'Pending';
                  } else if (order.status === 1 || order.status === 'Preparing') {
                    statusColor = 'text-neon-blue bg-neon-blue/10';
                    statusLabel = 'Preparing';
                  } else if (order.status === 2 || order.status === 'Ready') {
                    statusColor = 'text-accent bg-accent/10';
                    statusLabel = 'Ready';
                  } else if (order.status === 3 || order.status === 'Delivered') {
                    statusColor = 'text-neon-green bg-neon-green/10';
                    statusLabel = 'Delivered';
                  }
                  
                  return (
                    <div key={order.id} className="p-2.5 bg-bg-2 rounded-lg border border-border text-[11px]">
                      <div className="flex justify-between items-center mb-1">
                        <span className="font-mono font-bold text-text-2">{order.orderNumber}</span>
                        <span className={`px-1.5 py-0.5 rounded font-mono font-bold text-[9px] uppercase ${statusColor}`}>
                          {statusLabel}
                        </span>
                      </div>
                      <div className="text-text-3 truncate">
                        {order.items?.map(i => `${i.itemName} (x${i.quantity})`).join(', ')}
                      </div>
                      <div className="mt-1 font-mono text-right font-bold text-text">₹{order.totalAmount}</div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        </div>
      </div>

      {/* CONFIRM DIRECT ORDER MODAL */}
      {directOrderModalItem && (
        <div className="fixed inset-0 z-[990] flex items-center justify-center p-4 bg-black/80 backdrop-blur-sm">
          <div className="w-full max-w-sm bg-bg-2 border border-border rounded-xl shadow-2xl p-6">
            <h3 className="font-heading font-extrabold text-lg uppercase text-text mb-2 flex items-center gap-2">
              <UtensilsCrossed className="w-5 h-5 text-accent" />
              Confirm Food Order
            </h3>
            <p className="text-text-2 text-xs mb-4">
              Do you want to send this order request directly to the kitchen?
            </p>
            <div className="bg-bg-3 border border-border p-4 rounded-lg text-xs space-y-2 mb-6 font-heading">
              <div className="flex justify-between">
                <span className="text-text-3 font-semibold uppercase">Item:</span>
                <span className="font-bold text-text">{directOrderModalItem.item.itemName}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-text-3 font-semibold uppercase">Quantity:</span>
                <span className="font-mono font-bold text-text-2">{directOrderModalItem.quantity} units</span>
              </div>
              <div className="flex justify-between">
                <span className="text-text-3 font-semibold uppercase">Station:</span>
                <span className="font-bold text-accent">{pcNumber ? `Station ${pcNumber}` : 'Walk-in'}</span>
              </div>
              <div className="flex justify-between border-t border-border/40 pt-2 text-sm">
                <span className="text-text-3 font-semibold uppercase">Total Price:</span>
                <span className="font-mono font-extrabold text-neon-orange">₹{directOrderModalItem.item.price * directOrderModalItem.quantity}</span>
              </div>
            </div>
            <div className="flex gap-3">
              <button
                onClick={() => setDirectOrderModalItem(null)}
                className="flex-1 btn-secondary py-2.5 font-bold uppercase tracking-wider text-xs"
              >
                Cancel
              </button>
              <button
                onClick={() => handlePlaceDirectOrder(directOrderModalItem.item, directOrderModalItem.quantity)}
                disabled={submitting}
                className="flex-1 btn-primary py-2.5 font-bold uppercase tracking-wider text-xs flex justify-center items-center gap-1.5"
              >
                {submitting ? (
                  <div className="w-3.5 h-3.5 rounded-full border-2 border-white border-t-transparent animate-spin" />
                ) : (
                  'Confirm Order'
                )}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* PLACED ORDER SUCCESS CONFIRMATION MODAL */}
      {confirmationData && (
        <div className="fixed inset-0 z-[990] flex items-center justify-center p-4 bg-black/85 backdrop-blur-md">
          <div className="w-full max-w-md bg-bg-2 border-2 border-accent rounded-2xl shadow-2xl p-6 relative overflow-hidden text-center">
            {/* Glow effect */}
            <div className="absolute top-0 left-1/2 -translate-x-1/2 w-48 h-48 bg-accent/10 rounded-full blur-2xl" />
            
            <div className="w-16 h-16 rounded-full bg-accent/10 border border-accent/30 mx-auto flex items-center justify-center mb-4 text-accent">
              <CheckCircle className="w-10 h-10" />
            </div>

            <h3 className="font-heading font-extrabold text-xl uppercase tracking-wider text-text mb-1">
              Order Placed Successfully!
            </h3>
            <p className="text-text-3 text-xs mb-6 font-mono uppercase tracking-wider">
              Order Number: <span className="text-accent font-bold">{confirmationData.orderNumber}</span>
            </p>

            <div className="bg-bg-3 border border-border p-4 rounded-xl text-xs space-y-2.5 text-left font-heading mb-6">
              <div className="flex justify-between">
                <span className="text-text-3 font-semibold uppercase">Station/PC:</span>
                <span className="font-bold text-text">{confirmationData.pcNumber}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-text-3 font-semibold uppercase">Total Amount:</span>
                <span className="font-mono font-bold text-neon-green text-sm">₹{confirmationData.totalAmount}</span>
              </div>
              <div className="border-t border-border/40 pt-2.5">
                <span className="text-text-3 font-semibold uppercase block mb-1">Ordered Items:</span>
                <span className="text-text-2 font-medium break-words leading-relaxed">{confirmationData.items}</span>
              </div>
            </div>

            <p className="text-[11px] text-text-3 italic mb-6">
              The kitchen has been notified. We will deliver to your station shortly!
            </p>

            <button
              onClick={() => setConfirmationData(null)}
              className="w-full btn-primary py-3 font-heading font-extrabold text-xs uppercase tracking-wider rounded-xl shadow-lg shadow-accent/20"
            >
              Back to Menu
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
