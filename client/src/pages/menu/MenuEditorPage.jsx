import { useState, useEffect, useCallback } from 'react';
import { useStockVersion } from '../../components/food/SharedStockLive';
import {
  Plus,
  ClipboardCheck,
  AlertTriangle,
  Package,
  PackagePlus,
  TrendingUp,
} from 'lucide-react';
import PageHeader from '../../components/layout/PageHeader';
import {
  getInventory,
  createInventoryItem,
  updateInventoryItem,
  deleteInventoryItem,
  reconcileStock,
  addStock,
} from '../../api/food.api';
import { useBranch } from '../../contexts/BranchContext';
import { useAuth } from '../../contexts/AuthContext';
import { useToast } from '../../components/ui/Toast';

const REFRESH_EVERY_MS = 15000;

export default function MenuEditorPage() {
  const { activeBranch, branches } = useBranch();
  const { isSuperAdmin, user } = useAuth();
  const toast = useToast();

  const targetBranchId = isSuperAdmin ? activeBranch?.id : user?.branchId;

  // Table & List State
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  // Forms Modals State
  const [isCreateOpen, setIsCreateOpen] = useState(false);
  const [isEditOpen, setIsEditOpen] = useState(false);

  // The item the Edit modal is open for - its details, delivery and count-correction
  // sections all act on this same item.
  const [activeItem, setActiveItem] = useState(null);

  // Form Fields
  const [itemName, setItemName] = useState('');
  const [category, setCategory] = useState('Snacks');
  const [price, setPrice] = useState(0);
  const [minStockLimit, setMinStockLimit] = useState(5);
  const [status, setStatus] = useState('Available'); // Available, OutOfStock, Disabled
  const [imageUrl, setImageUrl] = useState('');

  // Reconciliation Fields
  const [physicalCount, setPhysicalCount] = useState(0);
  const [reconcileReason, setReconcileReason] = useState('');

  // Add Stock (delivery) fields - a real branch applies this immediately; from Head Office
  // it is queued for that branch to carry out. Never a blind overwrite - see food.api.js.
  const [addStockQuantity, setAddStockQuantity] = useState('');
  const [addStockReason, setAddStockReason] = useState('');

  const [submitting, setSubmitting] = useState(false);

  // Fetch inventory when targetBranchId changes. `quiet` skips the loading spinner for the
  // background poll below, so a stock delivery from another admin just appears in the table
  // instead of flashing the whole screen every 15 seconds.
  const fetchMenu = useCallback(async (quiet = false) => {
    if (!targetBranchId) return;

    if (!quiet) setLoading(true);
    setError(null);
    try {
      const res = await getInventory({ branchId: targetBranchId, includeAll: true });
      setItems(res?.data || []);
    } catch (err) {
      if (!quiet) setError('Could not load inventory menu items.');
    } finally {
      if (!quiet) setLoading(false);
    }
  }, [targetBranchId]);

  useEffect(() => {
    if (targetBranchId) {
      fetchMenu();
    }
  }, [targetBranchId, fetchMenu]);

  // The shared fridge moved (a sale at the other counter, or here) - show it straight away.
  const stockVersion = useStockVersion();
  useEffect(() => {
    if (stockVersion) fetchMenu(true);
  }, [stockVersion, fetchMenu]);

  // Stock now only ever changes through Add Stock or Reconcile, both of which can be done by
  // another admin on another screen - or, from Head Office, land here once the branch actually
  // carries the command out and syncs back up. Polling is what makes that show up without a
  // manual refresh, for operators and admins alike.
  useEffect(() => {
    const tick = () => {
      if (document.visibilityState !== 'visible') return;
      fetchMenu(true);
    };
    const timer = setInterval(tick, REFRESH_EVERY_MS);
    document.addEventListener('visibilitychange', tick);
    return () => {
      clearInterval(timer);
      document.removeEventListener('visibilitychange', tick);
    };
  }, [fetchMenu]);

  // Keeps the Edit modal's "Current Stock" figure live after a delivery or a count
  // correction, without closing the modal - both actions stay on the same item.
  useEffect(() => {
    if (!activeItem) return;
    const fresh = items.find(i => i.id === activeItem.id);
    if (fresh && fresh.currentStock !== activeItem.currentStock) {
      setActiveItem(fresh);
    }
  }, [items, activeItem]);

  // Opens the one Edit modal, priming both the item-details fields and the two stock
  // sections (delivery, count-correction) together.
  const openEdit = (item) => {
    setActiveItem(item);
    setItemName(item.itemName);
    setCategory(item.category || 'Snacks');
    setPrice(item.price);
    setMinStockLimit(item.minStockLimit);
    setStatus(item.status);
    setImageUrl(item.imageUrl || '');
    setAddStockQuantity('');
    setAddStockReason('');
    setPhysicalCount(item.currentStock);
    setReconcileReason('');
    setIsEditOpen(true);
  };

  const resetForm = () => {
    setItemName('');
    setCategory('Snacks');
    setPrice(0);
    setMinStockLimit(5);
    setStatus('Available');
    setImageUrl('');
    setActiveItem(null);
  };

  // Actions
  const handleCreate = async (e) => {
    e.preventDefault();
    if (!targetBranchId) return;

    setSubmitting(true);
    try {
      await createInventoryItem({
        branchId: targetBranchId,
        itemName,
        category,
        price: Number(price),
        minStockLimit: Number(minStockLimit),
        status,
        imageUrl: imageUrl || null
      });
      setIsCreateOpen(false);
      resetForm();
      fetchMenu();
      toast.success(`Menu item "${itemName}" created. Use Add Stock to record its first delivery.`);
    } catch (err) {
      const errMsg = err.response?.data?.message || 'Failed to create item';
      setError(errMsg);
      toast.error(errMsg);
    } finally {
      setSubmitting(false);
    }
  };

  const handleUpdate = async (e) => {
    e.preventDefault();
    if (!activeItem) return;

    setSubmitting(true);
    try {
      await updateInventoryItem(activeItem.id, {
        itemName,
        category,
        price: Number(price),
        minStockLimit: Number(minStockLimit),
        status,
        imageUrl: imageUrl || null
      });
      setIsEditOpen(false);
      toast.success(`Menu item "${itemName}" updated successfully.`);
      resetForm();
      fetchMenu();
    } catch (err) {
      const errMsg = err.response?.data?.message || 'Failed to update item';
      setError(errMsg);
      toast.error(errMsg);
    } finally {
      setSubmitting(false);
    }
  };

  const handleAddStock = async (e) => {
    e.preventDefault();
    if (!activeItem) return;
    const quantity = Number(addStockQuantity);
    if (!quantity || quantity <= 0) {
      toast.error('Enter how many units arrived.');
      return;
    }

    setSubmitting(true);
    try {
      const res = await addStock(activeItem.id, {
        quantity,
        reason: addStockReason || 'Stock delivery'
      });
      if (res?.data?.queued) {
        toast.success(res.data.message || `Sent to the branch: +${quantity} units of "${activeItem.itemName}".`);
      } else {
        toast.success(`Added ${quantity} units to "${activeItem.itemName}".`);
      }
      setAddStockQuantity('');
      setAddStockReason('');
      fetchMenu();
    } catch (err) {
      const errMsg = err.response?.data?.message || 'Failed to add stock';
      setError(errMsg);
      toast.error(errMsg);
    } finally {
      setSubmitting(false);
    }
  };

  const handleDelete = async (id) => {
    if (!window.confirm('Are you sure you want to delete this item?')) return;
    try {
      await deleteInventoryItem(id);
      fetchMenu();
      toast.success('Menu item deleted successfully.');
    } catch (err) {
      toast.error('Failed to delete item.');
    }
  };

  const handleReconcile = async (e) => {
    e.preventDefault();
    if (!activeItem) return;

    setSubmitting(true);
    try {
      await reconcileStock(activeItem.id, {
        physicalCount: Number(physicalCount),
        reason: reconcileReason || 'Super Admin Physical Count Reconciliation'
      });
      fetchMenu();
      toast.success(`Reconciled stock for "${activeItem.itemName}" to ${physicalCount}.`);
    } catch (err) {
      const errMsg = err.response?.data?.message || 'Failed to reconcile stock';
      setError(errMsg);
      toast.error(errMsg);
    } finally {
      setSubmitting(false);
    }
  };

  const totalItems = items.length;
  const totalStock = items.reduce((sum, item) => sum + item.currentStock, 0);
  const totalSold = items.reduce((sum, item) => sum + (item.soldQty || 0), 0);
  const lowStockCount = items.filter(item => item.currentStock <= item.minStockLimit).length;

  if (isSuperAdmin && !activeBranch) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
        <Package className="w-12 h-12 text-text-3 mb-4" />
        <h2 className="text-xl font-heading font-bold text-text mb-2">Select a Branch</h2>
        <p className="text-text-2">You must select a branch from the header to view or edit the Menu/Inventory.</p>
      </div>
    );
  }

  return (
    <div className="space-y-6 p-1">
      
      {/* Header */}
      <div className="flex justify-between items-start">
        <PageHeader
          title="Menu Editor"
          subtitle={isSuperAdmin 
            ? "Super Admin dashboard to create, update, delete, and reconcile Cafe food items"
            : "Operator view of Cafe food menu and inventory (Read-Only)"
          }
          icon="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253"
        />

        <div className="flex items-center gap-3">
          {isSuperAdmin && (
            <button
              onClick={() => { resetForm(); setIsCreateOpen(true); }}
              className="btn-primary py-2 px-4 flex items-center gap-1.5 font-bold text-xs uppercase shadow-md shadow-accent/20"
            >
              <Plus className="w-4 h-4" /> Add Item
            </button>
          )}
        </div>
      </div>

      {/* Inventory Summary Panel */}
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
        <div className="card bg-bg-2 border border-border p-4 rounded-xl flex flex-col justify-between">
          <span className="text-text-3 font-semibold uppercase text-[10px] tracking-wider">Total Menu Items</span>
          <span className="text-2xl font-bold font-mono text-text mt-1">{totalItems}</span>
        </div>
        <div className="card bg-bg-2 border border-border p-4 rounded-xl flex flex-col justify-between">
          <span className="text-text-3 font-semibold uppercase text-[10px] tracking-wider">Total Stock Available</span>
          <span className="text-2xl font-bold font-mono text-text mt-1">{totalStock} units</span>
        </div>
        <div className="card bg-bg-2 border border-border p-4 rounded-xl flex flex-col justify-between">
          <span className="text-text-3 font-semibold uppercase text-[10px] tracking-wider">Total Sold Quantity</span>
          <span className="text-2xl font-bold font-mono text-neon-blue mt-1">{totalSold} sold</span>
        </div>
        <div className="card bg-bg-2 border border-border p-4 rounded-xl flex flex-col justify-between">
          <span className="text-text-3 font-semibold uppercase text-[10px] tracking-wider">Low Stock Warnings</span>
          <span className={`text-2xl font-bold font-mono mt-1 ${lowStockCount > 0 ? 'text-neon-red font-extrabold animate-pulse' : 'text-text-3'}`}>{lowStockCount} items</span>
        </div>
      </div>

      {/* Main Table Card */}
      <div className="card bg-bg-2 border border-border rounded-xl p-6">
        {error && (
          <div className="mb-4 p-3 bg-neon-red/10 border border-neon-red/20 text-neon-red text-xs rounded-lg flex items-center gap-2">
            <AlertTriangle className="w-4 h-4" />
            <span>{error}</span>
          </div>
        )}

        <div className="overflow-x-auto">
          {loading ? (
            <div className="flex justify-center items-center py-12">
              <div className="w-8 h-8 rounded-full border-2 border-accent border-t-transparent animate-spin" />
            </div>
          ) : items.length === 0 ? (
            <div className="text-center text-text-3 text-sm py-12 italic border border-dashed border-border rounded-xl">
              No menu items defined for this branch yet. Click "Add Item" to initialize.
            </div>
          ) : (
            <table className="w-full text-left border-collapse text-xs">
              <thead>
                <tr className="border-b border-border text-text-3 uppercase font-bold tracking-wider text-[10px]">
                  <th className="py-3 px-4">Item Details</th>
                  <th className="py-3 px-4">Category</th>
                  <th className="py-3 px-4 text-right">Price</th>
                  <th className="py-3 px-4 text-center">Stock Limit</th>
                  <th className="py-3 px-4 text-right">Expected Stock</th>
                  <th className="py-3 px-4 text-right">Sold Qty</th>
                  <th className="py-3 px-4 text-center">Status</th>
                  {isSuperAdmin && <th className="py-3 px-4 text-right">Actions</th>}
                </tr>
              </thead>
              <tbody className="divide-y divide-border/40 font-heading">
                {items.map(item => {
                  const isLowStock = item.currentStock <= item.minStockLimit;
                  const isOversold = item.currentStock < 0;
                  const itemStatus = item.status || 'Available';
                  
                  let statusColor = 'text-neon-green bg-neon-green/10';
                  if (itemStatus === 'OutOfStock' || item.currentStock === 0) {
                    statusColor = 'text-neon-red bg-neon-red/10';
                  } else if (itemStatus === 'Disabled') {
                    statusColor = 'text-text-3 bg-text-3/10';
                  }

                  return (
                    <tr key={item.id} className="hover:bg-bg-3/45 transition-colors">
                      <td className="py-3 px-4 flex items-center gap-3">
                        {item.imageUrl ? (
                          <img src={item.imageUrl} alt={item.itemName} className="w-10 h-10 object-cover rounded-md border border-border" />
                        ) : (
                          <div className="w-10 h-10 bg-bg-3 border border-border rounded-md flex items-center justify-center text-text-3">
                            <Package className="w-5 h-5" />
                          </div>
                        )}
                        <div>
                          <div className="font-bold text-text text-sm">{item.itemName}</div>
                          <div className="text-[10px] font-mono text-text-3 mt-0.5">{item.id}</div>
                        </div>
                      </td>
                      <td className="py-3 px-4">
                        <span className="bg-bg-3 px-2.5 py-1 border border-border text-text-2 text-[10px] rounded-full uppercase font-mono">
                          {item.category || 'Snacks'}
                        </span>
                      </td>
                      <td className="py-3 px-4 text-right font-mono font-bold text-accent text-sm">₹{item.price.toFixed(2)}</td>
                      <td className="py-3 px-4 text-center font-mono text-text-3">{item.minStockLimit} limit</td>
                      <td className="py-3 px-4 text-right font-mono font-bold">
                        <span className={isLowStock ? 'text-neon-red font-extrabold' : 'text-text'}>
                          {item.currentStock}
                        </span>
                        {isOversold && (
                          <div className="text-[9px] font-bold uppercase text-neon-red mt-0.5">
                            Oversold — recount
                          </div>
                        )}
                      </td>
                      <td className="py-3 px-4 text-right font-mono text-text-2 flex justify-end items-center gap-1">
                        <TrendingUp className="w-3.5 h-3.5 text-neon-blue" />
                        {item.soldQty || 0}
                      </td>
                      <td className="py-3 px-4 text-center">
                        <span className={`px-2 py-0.5 rounded font-mono font-bold text-[9px] uppercase ${statusColor}`}>
                          {itemStatus}
                        </span>
                      </td>
                      {isSuperAdmin && (
                        <td className="py-3 px-4 text-right">
                          <div className="flex justify-end gap-2">
                            <button
                              onClick={() => openEdit(item)}
                              className="px-3 py-1.5 rounded-lg border border-border hover:border-neon-blue hover:text-neon-blue transition-colors text-text-3 text-[10px] font-bold uppercase tracking-wider"
                            >
                              Edit
                            </button>
                            <button
                              onClick={() => handleDelete(item.id)}
                              className="px-3 py-1.5 rounded-lg border border-border hover:border-neon-red hover:text-neon-red transition-colors text-text-3 text-[10px] font-bold uppercase tracking-wider"
                            >
                              Delete
                            </button>
                          </div>
                        </td>
                      )}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {/* CREATE MODAL */}
      {isCreateOpen && (
        <div className="fixed inset-0 z-[100] flex items-start justify-center p-4 bg-black/70 backdrop-blur-sm overflow-y-auto">
          <div className="w-full max-w-md bg-bg-2 border border-border rounded-xl shadow-2xl p-6 my-8">
            <h2 className="font-heading font-extrabold text-lg uppercase text-text mb-4">Add Menu Item</h2>
            <form onSubmit={handleCreate} className="space-y-4 text-xs">
              <div className="space-y-1">
                <label className="font-bold text-text-3">Item Name</label>
                <input required type="text" value={itemName} onChange={e => setItemName(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none" />
              </div>
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-1">
                  <label className="font-bold text-text-3">Category</label>
                  <input
                    required
                    list="categories-list"
                    type="text"
                    value={category}
                    onChange={e => setCategory(e.target.value)}
                    placeholder="Select or type category..."
                    className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none"
                  />
                  <datalist id="categories-list">
                    <option value="Cold Drinks" />
                    <option value="Hot Drinks" />
                    <option value="Snacks" />
                    {[...new Set(items.map(i => i.category).filter(Boolean))].map(cat => (
                      <option key={cat} value={cat} />
                    ))}
                  </datalist>
                </div>
                <div className="space-y-1">
                  <label className="font-bold text-text-3">Price (₹)</label>
                  <input required type="number" min="0" step="0.01" value={price} onChange={e => setPrice(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none font-mono" />
                </div>
              </div>
              <div className="space-y-1">
                <label className="font-bold text-text-3">Branch Location</label>
                <div className="bg-bg-3 border border-border p-2.5 rounded-lg text-text font-mono font-bold">
                  {branches.find(b => b.id === targetBranchId)?.name || 'Active Branch'}
                </div>
              </div>
              <div className="space-y-1">
                <label className="font-bold text-text-3">Min Low-Stock Warning</label>
                <input required type="number" min="0" value={minStockLimit} onChange={e => setMinStockLimit(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none font-mono" />
              </div>
              <div className="space-y-1">
                <label className="font-bold text-text-3">Status Toggle</label>
                <select value={status} onChange={e => setStatus(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none">
                  <option value="Available">Available (Visible)</option>
                  <option value="OutOfStock">Out of Stock (Greyd Out)</option>
                  <option value="Disabled">Disabled (Hidden)</option>
                </select>
              </div>
              <div className="space-y-1">
                <label className="font-bold text-text-3">Image URL (Optional)</label>
                <input type="text" value={imageUrl} onChange={e => setImageUrl(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none" />
              </div>
              <p className="text-text-3 text-[11px] -mt-1">New items start at 0 stock. Use Add Stock afterwards to record its first delivery.</p>
              <div className="flex justify-end gap-3 pt-4 border-t border-border">
                <button type="button" onClick={() => setIsCreateOpen(false)} className="btn-secondary py-2 px-4">Cancel</button>
                <button type="submit" disabled={submitting} className="btn-primary py-2 px-6">
                  {submitting ? 'Adding...' : 'Add Item'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* EDIT MODAL - everything about one item in one place, in plain language, rather than
          spread across four separate icon buttons whose purpose had to be guessed from a
          tooltip. Item details, a delivery, and a physical-count correction are three
          genuinely different actions underneath (different audit trail, different math), so
          they stay as three clearly-labelled sections rather than being merged into one
          confusing "stock" number - but they now live in one modal instead of three. */}
      {/* items-start, not items-center: this modal is taller than a lot of real browser
          windows now that it holds three sections instead of one. Centering a taller-than-
          viewport flex child clips it top and bottom with no way to scroll to the missing
          part in some browsers - confirmed live, the bottom (Correct Count, Close) was
          unreachable. Starting from the top and scrolling the backdrop itself keeps the
          whole modal reachable regardless of window size. */}
      {isEditOpen && (
        <div className="fixed inset-0 z-[100] flex items-start justify-center p-4 bg-black/70 backdrop-blur-sm overflow-y-auto">
          <div className="w-full max-w-md bg-bg-2 border border-border rounded-xl shadow-2xl p-6 my-8">
            <h2 className="font-heading font-extrabold text-lg uppercase text-text mb-4">Edit Menu Item</h2>

            {/* -- Item details -- */}
            <form onSubmit={handleUpdate} className="space-y-4 text-xs">
              <div className="space-y-1">
                <label className="font-bold text-text-3">Item Name</label>
                <input required type="text" value={itemName} onChange={e => setItemName(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none" />
              </div>
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-1">
                  <label className="font-bold text-text-3">Category</label>
                  <input
                    required
                    list="categories-list"
                    type="text"
                    value={category}
                    onChange={e => setCategory(e.target.value)}
                    placeholder="Select or type category..."
                    className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none"
                  />
                  <datalist id="categories-list">
                    <option value="Cold Drinks" />
                    <option value="Hot Drinks" />
                    <option value="Snacks" />
                    {[...new Set(items.map(i => i.category).filter(Boolean))].map(cat => (
                      <option key={cat} value={cat} />
                    ))}
                  </datalist>
                </div>
                <div className="space-y-1">
                  <label className="font-bold text-text-3">Price (₹)</label>
                  <input required type="number" min="0" step="0.01" value={price} onChange={e => setPrice(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none font-mono" />
                </div>
              </div>
              <div className="space-y-1">
                <label className="font-bold text-text-3">Branch Location</label>
                <div className="bg-bg-3 border border-border p-2.5 rounded-lg text-text font-mono font-bold">
                  {branches.find(b => b.id === activeItem?.branchId)?.name || 'Local Branch'}
                </div>
              </div>
              <div className="space-y-1">
                <label className="font-bold text-text-3">Min Low-Stock Warning</label>
                <input required type="number" min="0" value={minStockLimit} onChange={e => setMinStockLimit(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none font-mono" />
              </div>
              <div className="space-y-1">
                <label className="font-bold text-text-3">Status Toggle</label>
                <select value={status} onChange={e => setStatus(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none">
                  <option value="Available">Available (Visible)</option>
                  <option value="OutOfStock">Out of Stock (Greyd Out)</option>
                  <option value="Disabled">Disabled (Hidden)</option>
                </select>
              </div>
              <div className="space-y-1">
                <label className="font-bold text-text-3">Image URL (Optional)</label>
                <input type="text" value={imageUrl} onChange={e => setImageUrl(e.target.value)} className="w-full bg-bg-3 border border-border p-2.5 rounded-lg text-text focus:border-accent outline-none" />
              </div>
              <div className="flex justify-end pt-2">
                <button type="submit" disabled={submitting} className="btn-primary py-2 px-6">
                  {submitting ? 'Saving...' : 'Save Item Details'}
                </button>
              </div>
            </form>

            {/* -- Stock -- */}
            <div className="mt-6 pt-5 border-t border-border space-y-5">
              <div className="flex items-center justify-between">
                <span className="font-bold text-text-3 uppercase text-[10px] tracking-wider">Current Stock</span>
                <span className="font-mono font-bold text-text text-sm">{activeItem?.currentStock} units</span>
              </div>

              {/* A delivery just arrived - adds to the shelf, never replaces the count. */}
              <div className="space-y-2 bg-bg-3/40 border border-border rounded-lg p-3">
                <div className="flex items-center gap-1.5 font-bold text-text text-xs">
                  <PackagePlus className="w-3.5 h-3.5 text-neon-green" /> New Delivery Arrived
                </div>
                <p className="text-text-3 text-[11px]">How many units just came in. This is added to the shelf, not a replacement.</p>
                <div className="flex gap-2">
                  <input type="number" min="1" placeholder="Units delivered" value={addStockQuantity} onChange={e => setAddStockQuantity(e.target.value)} className="flex-1 bg-bg-3 border border-border p-2 rounded-lg text-text focus:border-accent outline-none font-mono text-sm" />
                  <button type="button" disabled={submitting} onClick={handleAddStock} className="btn-primary py-2 px-4 text-[11px] shrink-0">
                    {submitting ? '...' : 'Add Delivery'}
                  </button>
                </div>
              </div>

              {/* A physical count came out different - corrects to the real number, and logs
                  the discrepancy rather than silently overwriting it. */}
              <div className="space-y-2 bg-bg-3/40 border border-border rounded-lg p-3">
                <div className="flex items-center gap-1.5 font-bold text-text text-xs">
                  <ClipboardCheck className="w-3.5 h-3.5 text-neon-orange" /> Correct the Count
                </div>
                <p className="text-text-3 text-[11px]">Use this only if you physically counted the shelf and it does not match the number above.</p>
                <input type="number" min="0" placeholder="Actual count on the shelf" value={physicalCount} onChange={e => setPhysicalCount(e.target.value)} className="w-full bg-bg-3 border border-border p-2 rounded-lg text-text focus:border-accent outline-none font-mono text-sm" />
                <input type="text" placeholder="Why does it not match? (required)" value={reconcileReason} onChange={e => setReconcileReason(e.target.value)} className="w-full bg-bg-3 border border-border p-2 rounded-lg text-text focus:border-accent outline-none text-[11px]" />
                <button type="button" disabled={submitting} onClick={handleReconcile} className="btn-secondary py-2 px-4 text-[11px] w-full">
                  {submitting ? '...' : 'Correct Count'}
                </button>
              </div>
            </div>

            <div className="flex justify-end pt-4 mt-2 border-t border-border">
              <button type="button" onClick={() => { setIsEditOpen(false); resetForm(); }} className="btn-secondary py-2 px-4">Close</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
