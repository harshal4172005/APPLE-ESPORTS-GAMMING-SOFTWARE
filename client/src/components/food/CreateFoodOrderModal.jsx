import { useState, useEffect } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { X, ShoppingCart, Plus, Minus, AlertTriangle, Monitor, User } from 'lucide-react';
import api from '../../config/api';
import { useBranch } from '../../contexts/BranchContext';
import { useStockVersion, SharedStockBanner } from './SharedStockLive';

export default function CreateFoodOrderModal({ onClose, onOrderPlaced, initialPcId }) {
  const { activeBranch } = useBranch();
  const stockVersion = useStockVersion();
  const [inventory, setInventory] = useState([]);
  const [cart, setCart] = useState([]); // { item, quantity }
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(null);

  // Linkage targets
  const [activeSessions, setActiveSessions] = useState([]);
  const [selectedSessionId, setSelectedSessionId] = useState('');
  const [walkInName, setWalkInName] = useState('');

  useEffect(() => {
    const fetchInitialData = async () => {
      try {
        const [invRes, sessRes] = await Promise.all([
          api.get('/inventory', { params: { branchId: activeBranch?.id } }),
          api.get('/sessions', { params: { branchId: activeBranch?.id, page: 1, pageSize: 100 } })
        ]);

        setInventory(invRes.data?.data || []);
        // Only active sessions
        const activeSess = (sessRes.data?.data?.items || []).filter(s => s.status === 1 || s.status === 'Active');
        setActiveSessions(activeSess);

        if (initialPcId) {
          const match = activeSess.find(s => s.pcId === initialPcId);
          if (match) setSelectedSessionId(match.id);
        }
      } catch (err) {
        setError('Failed to load menu or sessions.');
      } finally {
        setLoading(false);
      }
    };
    fetchInitialData();
  }, [activeBranch, stockVersion]);

  const addToCart = (invItem) => {
    setCart(prev => {
      const existing = prev.find(i => i.item.id === invItem.id);
      if (existing) {
        if (existing.quantity >= invItem.currentStock) return prev; // Cannot exceed stock
        return prev.map(i => i.item.id === invItem.id ? { ...i, quantity: i.quantity + 1 } : i);
      }
      if (invItem.currentStock < 1) return prev;
      return [...prev, { item: invItem, quantity: 1 }];
    });
  };

  const removeFromCart = (itemId) => {
    setCart(prev => {
      const existing = prev.find(i => i.item.id === itemId);
      if (existing.quantity > 1) {
        return prev.map(i => i.item.id === itemId ? { ...i, quantity: i.quantity - 1 } : i);
      }
      return prev.filter(i => i.item.id !== itemId);
    });
  };

  const cartTotal = cart.reduce((sum, cartItem) => sum + (cartItem.item.price * cartItem.quantity), 0);

  const handleSubmit = async () => {
    if (cart.length === 0) {
      setError("Cart is empty");
      return;
    }
    
    setSubmitting(true);
    setError(null);
    try {
      let pcId = null;
      let customerName = walkInName;

      if (selectedSessionId) {
        const sess = activeSessions.find(s => s.id === selectedSessionId);
        if (sess) {
          pcId = sess.pcId;
          customerName = sess.customerName;
        }
      }

      const payload = {
        sessionId: selectedSessionId || null,
        pcId: pcId,
        customerName: customerName,
        items: cart.map(c => ({
          inventoryId: c.item.id,
          quantity: c.quantity
        }))
      };

      await api.post('/food-orders', payload);
      onOrderPlaced();
      onClose();
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Failed to place order');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <AnimatePresence>
      <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-md">
        <motion.div
          initial={{ opacity: 0, scale: 0.95, y: 10 }}
          animate={{ opacity: 1, scale: 1, y: 0 }}
          exit={{ opacity: 0, scale: 0.95, y: 10 }}
          className="w-full max-w-4xl bg-bg-2 border border-border rounded-xl shadow-2xl overflow-hidden flex max-h-[85vh]"
        >
          {/* Left: Menu */}
          <div className="w-2/3 flex flex-col bg-bg-2 border-r border-border">
            <div className="p-4 border-b border-border bg-bg-3">
              <h2 className="font-heading font-bold text-text uppercase tracking-wider text-lg">Menu</h2>
            </div>
            
            <div className="flex-1 overflow-y-auto p-4">
              <SharedStockBanner />
              {loading ? (
                <div className="flex justify-center items-center h-full">
                  <div className="w-6 h-6 border-2 border-accent border-t-transparent rounded-full animate-spin" />
                </div>
              ) : (
                <div className="grid grid-cols-2 lg:grid-cols-3 gap-3">
                  {inventory.map(item => (
                    <button
                      key={item.id}
                      onClick={() => addToCart(item)}
                      disabled={item.currentStock < 1}
                      className="flex flex-col text-left p-3 rounded-lg border border-border bg-bg-3 hover:border-accent/50 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                      <div className="font-heading font-bold text-sm text-text truncate w-full">{item.itemName}</div>
                      <div className="font-mono text-accent text-sm my-1">₹{item.price}</div>
                      
                      <div className="mt-auto flex justify-between items-center w-full">
                        <span className={`text-[10px] font-mono px-1.5 py-0.5 rounded ${
                          item.currentStock === 0 ? 'bg-neon-red/20 text-neon-red' 
                          : item.isLowStock ? 'bg-neon-orange/20 text-neon-orange' 
                          : 'bg-text-3/20 text-text-3'
                        }`}>
                          STOCK: {item.currentStock}
                        </span>
                        {item.currentStock > 0 && <Plus className="w-3.5 h-3.5 text-text-3" />}
                      </div>
                    </button>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Right: Cart & Checkout */}
          <div className="w-1/3 flex flex-col bg-bg-3">
            <div className="p-4 border-b border-border flex justify-between items-center">
              <h2 className="font-heading font-bold text-text uppercase tracking-wider text-lg flex items-center gap-2">
                <ShoppingCart className="w-5 h-5 text-accent" /> Cart
              </h2>
              <button onClick={onClose} className="p-1 text-text-3 hover:text-text rounded-md transition-colors">
                <X className="w-5 h-5" />
              </button>
            </div>

            <div className="flex-1 overflow-y-auto p-4 space-y-3">
              {error && (
                <div className="p-2 mb-2 bg-neon-red/10 border border-neon-red/20 rounded-lg text-neon-red text-[10px] flex items-start gap-2">
                  <AlertTriangle className="w-3 h-3 shrink-0" />
                  <p>{error}</p>
                </div>
              )}

              {/* Cart Items */}
              <div className="space-y-2 mb-4">
                {cart.length === 0 ? (
                  <div className="text-center text-text-3 text-xs italic py-4">Cart is empty</div>
                ) : (
                  cart.map(c => (
                    <div key={c.item.id} className="flex justify-between items-center bg-bg-2 p-2 rounded-lg border border-border">
                      <div className="text-xs">
                        <div className="font-medium text-text">{c.item.itemName}</div>
                        <div className="font-mono text-text-3 mt-0.5">₹{c.item.price} × {c.quantity} = ₹{c.item.price * c.quantity}</div>
                      </div>
                      <div className="flex items-center gap-2 bg-bg-3 rounded-md border border-border px-1">
                        <button onClick={() => removeFromCart(c.item.id)} className="p-1 text-text-3 hover:text-accent"><Minus className="w-3 h-3"/></button>
                        <span className="font-mono text-xs">{c.quantity}</span>
                        <button onClick={() => addToCart(c.item)} disabled={c.quantity >= c.item.currentStock} className="p-1 text-text-3 hover:text-neon-blue disabled:opacity-30"><Plus className="w-3 h-3"/></button>
                      </div>
                    </div>
                  ))
                )}
              </div>

              {/* Linkage */}
              <div className="space-y-3 pt-3 border-t border-border">
                <div className="space-y-1">
                  <label className="text-[10px] uppercase tracking-wider font-bold text-text-3 flex items-center gap-1.5">
                    <Monitor className="w-3 h-3" /> Link to Session (Bill)
                  </label>
                  <select 
                    value={selectedSessionId} 
                    onChange={e => { setSelectedSessionId(e.target.value); setWalkInName(''); }}
                    className="w-full bg-bg-2 border border-border text-text text-xs rounded-md p-2 focus:border-accent outline-none"
                  >
                    <option value="">None (Walk-in Order)</option>
                    {activeSessions.map(s => (
                      <option key={s.id} value={s.id}>{s.pcName} - {s.customerName || 'Guest'}</option>
                    ))}
                  </select>
                </div>

                {!selectedSessionId && (
                  <div className="space-y-1">
                    <label className="text-[10px] uppercase tracking-wider font-bold text-text-3 flex items-center gap-1.5">
                      <User className="w-3 h-3" /> Customer Name (Optional)
                    </label>
                    <input 
                      type="text" 
                      value={walkInName} 
                      onChange={e => setWalkInName(e.target.value)}
                      placeholder="e.g. John"
                      className="w-full bg-bg-2 border border-border text-text text-xs rounded-md p-2 focus:border-accent outline-none"
                    />
                  </div>
                )}
              </div>

            </div>

            {/* Footer */}
            <div className="p-4 border-t border-border bg-bg-3">
              <div className="flex justify-between items-center mb-4">
                <span className="text-text-2 uppercase tracking-wider font-bold text-xs">Total</span>
                <span className="font-mono font-bold text-2xl text-accent drop-shadow-[0_0_8px_rgba(255,51,102,0.3)]">
                  ₹{cartTotal}
                </span>
              </div>
              <button
                onClick={handleSubmit}
                disabled={submitting || cart.length === 0}
                className="w-full btn-primary py-3 flex justify-center items-center gap-2"
              >
                {submitting ? (
                  <div className="w-4 h-4 rounded-full border-2 border-white border-t-transparent animate-spin" />
                ) : (
                  <>✓ PLACE ORDER</>
                )}
              </button>
            </div>
          </div>
        </motion.div>
      </div>
    </AnimatePresence>
  );
}
