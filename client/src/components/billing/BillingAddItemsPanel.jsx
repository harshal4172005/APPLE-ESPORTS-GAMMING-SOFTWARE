import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { ShoppingBasket, Plus, Minus } from 'lucide-react';
import api from '../../config/api';
import { useBranch } from '../../contexts/BranchContext';
import { useToast } from '../ui/Toast';
import { useStockVersion, SharedStockBanner } from '../food/SharedStockLive';

export default function BillingAddItemsPanel({ bill, onOrderPlaced }) {
  const { activeBranch } = useBranch();
  const stockVersion = useStockVersion();
  const toast = useToast();
  const navigate = useNavigate();
  
  const [inventory, setInventory] = useState([]);
  const [cart, setCart] = useState([]);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [activeTab, setActiveTab] = useState('All');

  useEffect(() => {
    const fetchInventory = async () => {
      try {
        const res = await api.get('/inventory', { params: { branchId: activeBranch?.id } });
        setInventory(res.data?.data || []);
      } catch (err) {
        toast.error('Failed to load menu items.');
      } finally {
        setLoading(false);
      }
    };
    fetchInventory();
  }, [activeBranch, stockVersion]);

  // Clear cart if selected bill changes to prevent accidental wrong-bill orders
  useEffect(() => {
    setCart([]);
  }, [bill?.id]);

  const addToCart = (invItem) => {
    setCart(prev => {
      const existing = prev.find(i => i.item.id === invItem.id);
      if (existing) {
        if (existing.quantity >= invItem.currentStock) return prev;
        return prev.map(i => i.item.id === invItem.id ? { ...i, quantity: i.quantity + 1 } : i);
      }
      if (invItem.currentStock < 1) return prev;
      return [...prev, { item: invItem, quantity: 1 }];
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

  const handleSubmit = async () => {
    if (cart.length === 0) return;
    setSubmitting(true);
    
    try {
      let payload = {
        sessionId: bill?.sessionId || null,
        pcId: bill?.pcId || null,
        customerName: bill?.customerName || '',
        items: cart.map(c => ({
          inventoryId: c.item.id,
          quantity: c.quantity
        }))
      };

      const res = await api.post('/food-orders', payload);

      toast.success('Food order sent to kitchen successfully');
      setCart([]);
      onOrderPlaced?.(res.data?.data?.billId);

      // Straight to the kitchen queue - this is exactly where the order needs marking
      // Delivered once it is actually handed over, not left for someone to remember to
      // go find later.
      navigate('/app/food-orders');
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to add items to bill');
    } finally {
      setSubmitting(false);
    }
  };

  const dynamicCategories = ['All', ...new Set(inventory.map(i => i.category).filter(Boolean))];

  const getFilteredItems = () => {
    if (activeTab === 'All') return inventory;
    return inventory.filter(i => i.category === activeTab);
  };

  const filteredItems = getFilteredItems();

  return (
    <div className="flex flex-col h-full bg-bg-2 border border-border rounded-xl overflow-hidden shadow-lg">
      <div className="bg-bg-3 px-5 py-3.5 border-b border-border flex items-center justify-between shrink-0">
        <div className="flex items-center gap-2">
          <ShoppingBasket className="w-4 h-4 text-neon-green" />
          <span className="font-heading font-bold text-text uppercase tracking-wider text-sm">
            Add Items to Bill
          </span>
        </div>
      </div>

      <div className="flex gap-2 p-3 border-b border-border overflow-x-auto hide-scrollbar shrink-0">
        {dynamicCategories.map(cat => (
          <button
            key={cat}
            onClick={() => setActiveTab(cat)}
            className={`px-4 py-1.5 rounded-full text-xs font-bold whitespace-nowrap transition-colors border ${
              activeTab === cat
                ? 'bg-neon-green/10 border-neon-green text-neon-green'
                : 'bg-bg-3 border-border text-text-3 hover:text-text hover:border-text-3/40'
            }`}
          >
            {cat}
          </button>
        ))}
      </div>

      <div className="flex-1 overflow-y-auto p-4">
        <SharedStockBanner />
        {loading ? (
          <div className="flex justify-center items-center h-full">
            <div className="w-6 h-6 border-2 border-accent border-t-transparent rounded-full animate-spin" />
          </div>
        ) : (
          <div className="grid grid-cols-1 xl:grid-cols-2 gap-3">
            {filteredItems.map(item => {
              const cartItem = cart.find(c => c.item.id === item.id);
              const qty = cartItem ? cartItem.quantity : 0;
              return (
                <div
                  key={item.id}
                  className="flex items-center justify-between p-3 rounded-lg border border-border bg-bg-3 hover:border-neon-green/30 transition-colors"
                >
                  <div className="flex-1 min-w-0 mr-3">
                    <div className="font-heading font-bold text-sm text-text truncate w-full">{item.itemName}</div>
                    <div className="font-mono text-neon-green text-sm font-bold mt-0.5">₹{item.price}</div>
                  </div>
                  <div className="flex items-center gap-3 bg-bg-2 rounded-lg border border-border px-1.5 py-1">
                    <button onClick={() => removeFromCart(item.id)} disabled={qty === 0} className="p-1 text-text-3 hover:text-neon-red disabled:opacity-30">
                      <Minus className="w-3.5 h-3.5" />
                    </button>
                    <span className="font-mono text-sm w-4 text-center font-bold text-text">{qty}</span>
                    <button onClick={() => addToCart(item)} disabled={qty >= item.currentStock} className="p-1 text-text-3 hover:text-neon-green disabled:opacity-30">
                      <Plus className="w-3.5 h-3.5" />
                    </button>
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {cart.length > 0 && (
        <div className="p-4 border-t border-border bg-bg-3 shrink-0">
          <button
            onClick={handleSubmit}
            disabled={submitting}
            className="w-full btn-primary bg-neon-green border-neon-green hover:bg-neon-green/90 text-bg py-3 flex justify-center items-center gap-2 shadow-[0_0_14px_rgba(0,255,153,0.15)]"
          >
            {submitting ? (
              <div className="w-4 h-4 rounded-full border-2 border-bg border-t-transparent animate-spin" />
            ) : (
              <>+ ADD {cart.reduce((s,c) => s + c.quantity, 0)} ITEMS (₹{cart.reduce((s,c) => s + (c.item.price * c.quantity), 0)})</>
            )}
          </button>
        </div>
      )}
    </div>
  );
}
