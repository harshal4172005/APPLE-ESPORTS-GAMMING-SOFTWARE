import React, { useState, useEffect } from 'react';
import { useOverlaySocket } from '../../../contexts/OverlaySocketContext';
import { Coffee, Plus, Minus, ShoppingCart, Loader2, CheckCircle2, ShoppingBag } from 'lucide-react';
import axios from 'axios';

export default function FoodOrderScreen() {
  const { placeFoodOrder, foodOrders, sessionData, branchId, stockVersion } = useOverlaySocket();
  const [cart, setCart] = useState([]);
  const [activeTab, setActiveTab] = useState('Snacks');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [orderSuccess, setOrderSuccess] = useState(false);
  const [orderError, setOrderError] = useState(null);
  const [menuItems, setMenuItems] = useState([]);
  const [loadingMenu, setLoadingMenu] = useState(true);

  const categories = [...new Set(menuItems.map(m => m.category || 'Other'))];
  if (categories.length === 0) categories.push('Snacks');

  useEffect(() => {
    const fetchMenu = async () => {
      if (!branchId) return;
      try {
        if (!stockVersion) setLoadingMenu(true);   // a live stock update reloads quietly
        const res = await axios.get(`/api/public/branches/${branchId}/menu`);
        if (res.data.success) {
          setMenuItems(res.data.data);
          const cats = [...new Set(res.data.data.map(m => m.category || 'Other'))];
          if (cats.length > 0 && !cats.includes(activeTab)) {
             setActiveTab(cats[0]);
          }
        }
      } catch (err) {
        console.error('Failed to fetch branch menu:', err);
      } finally {
        setLoadingMenu(false);
      }
    };
    fetchMenu();
  }, [branchId, stockVersion]);

  const addToCart = (item) => {
    setCart(prev => {
      const existing = prev.find(i => i.id === item.id);
      if (existing) {
        return prev.map(i => i.id === item.id ? { ...i, qty: i.qty + 1 } : i);
      }
      return [...prev, { ...item, qty: 1 }];
    });
  };

  const removeFromCart = (itemId) => {
    setCart(prev => {
      const existing = prev.find(i => i.id === itemId);
      if (existing.qty === 1) {
        return prev.filter(i => i.id !== itemId);
      }
      return prev.map(i => i.id === itemId ? { ...i, qty: i.qty - 1 } : i);
    });
  };

  const cartTotal = cart.reduce((sum, item) => sum + (item.price * item.qty), 0);
  const totalItems = cart.reduce((sum, item) => sum + item.qty, 0);

  const handleCheckout = async () => {
    if (cart.length === 0) return;
    
    if (sessionData?.memberLinked && sessionData?.foodBalance != null) {
      const remainingFoodBalance = sessionData.foodBalance - (sessionData.foodCharges || 0);
      if (cartTotal > remainingFoodBalance) {
        setOrderError(`Insufficient Food Balance. You only have ₹${remainingFoodBalance.toFixed(2)} left. Please top up.`);
        return;
      }
    }

    setIsSubmitting(true);
    
    const orderItems = cart.map(item => ({
      menuItemId: item.id,
      name: item.name,
      quantity: item.qty,
      price: item.price
    }));

    try {
      setOrderError(null);
      const res = await placeFoodOrder(orderItems, cartTotal);
      if (res?.success) {
        setCart([]);
        setOrderSuccess(true);
        setTimeout(() => setOrderSuccess(false), 3000);
      } else {
        setOrderError(res?.error || "Failed to place order. Please try again.");
      }
    } catch (err) {
      console.error(err);
      setOrderError(err.message || "An unexpected error occurred.");
    } finally {
      setIsSubmitting(false);
    }
  };

  if (!sessionData) {
    return <div className="p-6 text-center text-text-3 font-body">Session not active</div>;
  }

  if (sessionData.sessionStatus === 'awaiting_billing') {
    return (
      <div className="p-6 text-center h-full flex flex-col items-center justify-center">
        <ShoppingCart className="w-16 h-16 text-text-3 mb-4 opacity-50" />
        <h2 className="font-heading text-xl font-bold text-text-2 tracking-wide uppercase">Session Ended</h2>
        <p className="text-text-3 font-body text-sm mt-2">Food ordering is disabled while awaiting billing.</p>
      </div>
    );
  }

  if (orderSuccess) {
    return (
      <div className="p-6 text-center h-full flex flex-col items-center justify-center bg-bg/90">
        <div className="w-20 h-20 bg-neon-green/20 rounded-full flex items-center justify-center mb-6 shadow-[0_0_30px_rgba(34,211,166,0.3)]">
          <CheckCircle2 className="w-10 h-10 text-neon-green" />
        </div>
        <h2 className="font-heading text-2xl font-bold text-text tracking-wide uppercase">Order Placed!</h2>
        <p className="text-text-2 font-body mt-2">Your order will be served at your desk shortly.</p>
      </div>
    );
  }

  return (
    <div className="flex flex-col h-full bg-bg relative">
      <div className="flex overflow-x-auto shrink-0 bg-bg-3 border-b border-border/50 scrollbar-none">
        {categories.map(cat => (
          <button
            key={cat}
            onClick={() => setActiveTab(cat)}
            className={`px-4 py-3 font-heading text-sm uppercase tracking-wider font-bold shrink-0 border-b-2 transition-colors ${
              activeTab === cat 
                ? 'border-accent text-accent' 
                : 'border-transparent text-text-3 hover:text-text-2'
            }`}
          >
            {cat}
          </button>
        ))}
      </div>

      <div className="flex-1 overflow-y-auto p-4 space-y-3 scrollbar-thin">
        {loadingMenu ? (
          <div className="flex justify-center items-center h-32">
            <Loader2 className="w-8 h-8 animate-spin text-accent" />
          </div>
        ) : menuItems.length === 0 ? (
          <div className="text-center text-text-3 font-body py-8 italic">
            No items available in this category.
          </div>
        ) : (
          menuItems.filter(m => (m.category || 'Other') === activeTab).map(item => {
            const cartItem = cart.find(c => c.id === item.id);
            return (
              <div key={item.id} className="bg-bg-2 border border-border/60 rounded-xl p-3 flex items-center justify-between shadow-sm hover:border-accent/30 transition-colors">
                <div>
                  <h3 className={`font-body font-semibold ${item.inStock ? 'text-text' : 'text-text-3 line-through'}`}>
                    {item.name}
                  </h3>
                <p className="text-accent font-mono text-sm font-bold">₹{item.price}</p>
                {!item.inStock && <p className="text-[10px] text-neon-orange uppercase tracking-wider mt-1">Out of Stock</p>}
              </div>

              {item.inStock && (
                <div className="flex items-center gap-3 bg-bg-3 rounded-lg border border-border/50 p-1">
                  {cartItem ? (
                    <>
                      <button onClick={() => removeFromCart(item.id)} className="p-1 hover:bg-white/5 rounded text-text-2">
                        <Minus className="w-4 h-4" />
                      </button>
                      <span className="font-mono text-text font-bold w-4 text-center">{cartItem.qty}</span>
                      <button onClick={() => addToCart(item)} className="p-1 hover:bg-white/5 rounded text-accent">
                        <Plus className="w-4 h-4" />
                      </button>
                    </>
                  ) : (
                    <button onClick={() => addToCart(item)} className="px-3 py-1 font-heading text-xs uppercase tracking-wider text-accent font-bold hover:bg-accent/10 rounded transition-colors">
                      Add
                    </button>
                  )}
                </div>
              )}
            </div>
          );
        }))}
      </div>

      {cart.length > 0 && (
        <div className="shrink-0 bg-bg-2 border-t border-border/50 p-4 pb-6">
          {orderError && (
            <div className="mb-3 p-2 bg-red-500/10 border border-red-500/30 rounded text-red-500 text-xs font-body text-center">
              {orderError}
            </div>
          )}
          <div className="flex justify-between items-center mb-4">
            <span className="font-body text-text-2">Total ({totalItems} items)</span>
            <span className="font-mono text-xl font-bold text-accent">₹{cartTotal}</span>
          </div>
          
          <button 
            onClick={handleCheckout}
            disabled={isSubmitting}
            className="w-full bg-accent hover:bg-accent/90 text-bg font-heading uppercase tracking-widest font-bold py-3 px-4 rounded-xl transition-all shadow-[0_0_20px_rgba(255,215,0,0.15)] hover:shadow-[0_0_30px_rgba(255,215,0,0.3)] disabled:opacity-50 disabled:cursor-not-allowed flex items-center justify-center gap-2"
          >
            {isSubmitting ? <Loader2 className="w-5 h-5 animate-spin" /> : (
              <>
                <ShoppingBag className="w-5 h-5" />
                Confirm Order
              </>
            )}
          </button>
        </div>
      )}
    </div>
  );
}
