import { useState, useEffect } from 'react';
import { useBranch } from '../../contexts/BranchContext';
import { useToast } from '../../components/ui/Toast';
import Drawer from '../../components/ui/Drawer';
import { getFoodGroups, createFoodGroup, setFoodGroupBranches, deleteFoodGroup, setFoodGroupOfflineSeller } from '../../api/settings.api';
import { Plus, Trash2, Utensils, Save } from 'lucide-react';

export default function FoodSharingTab() {
  const { branches } = useBranch();
  const toast = useToast();

  const [groups, setGroups] = useState([]);
  const [loading, setLoading] = useState(false);
  const [createDrawer, setCreateDrawer] = useState(false);
  const [editDrawer, setEditDrawer] = useState({ isOpen: false, group: null, selectedIds: [] });

  const fetchGroups = async () => {
    setLoading(true);
    try {
      const res = await getFoodGroups();
      setGroups(res.data || []);
    } catch (err) {
      toast.error('Failed to load food groups');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { fetchGroups(); }, []);

  const handleCreate = async (e) => {
    e.preventDefault();
    const name = new FormData(e.target).get('name');
    try {
      await createFoodGroup(name);
      toast.success('Food group created');
      setCreateDrawer(false);
      fetchGroups();
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to create food group');
    }
  };

  const openEdit = (group) => {
    setEditDrawer({ isOpen: true, group, selectedIds: group.branches.map(b => b.id) });
  };

  const toggleBranch = (id) => {
    setEditDrawer(prev => ({
      ...prev,
      selectedIds: prev.selectedIds.includes(id)
        ? prev.selectedIds.filter(x => x !== id)
        : [...prev.selectedIds, id],
    }));
  };

  const handleSaveBranches = async () => {
    try {
      await setFoodGroupBranches(editDrawer.group.id, editDrawer.selectedIds);
      toast.success('Updated branch membership');
      setEditDrawer({ isOpen: false, group: null, selectedIds: [] });
      fetchGroups();
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to update branch membership');
    }
  };

  const handleOfflineSeller = async (group, branchId) => {
    try {
      await setFoodGroupOfflineSeller(group.id, branchId);
      toast.success(branchId ? 'Saved - this branch sells the shared fridge when the internet is down' : 'Saved');
      fetchGroups();
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to save');
    }
  };

  const handleDelete = async (id) => {
    if (!window.confirm('Delete this food group? Its branches go back to being fully independent — their existing menus are untouched.')) return;
    try {
      await deleteFoodGroup(id);
      toast.success('Food group deleted');
      fetchGroups();
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to delete food group');
    }
  };

  return (
    <div className="tab-pane fade-in space-y-4">
      <div className="pane-header flex-col md:flex-row md:items-center gap-4">
        <div>
          <h2>Food & Snacks Sharing</h2>
          <p className="text-text-2 text-xs mt-1">
            Link branches that share one pantry so their menu and stock count are the same
            number everywhere, instead of two independently-tracked numbers. A branch not in
            any group here is fully independent, exactly as before — PCs, cash and shifts are
            never affected either way.
          </p>
        </div>
        <button
          onClick={() => setCreateDrawer(true)}
          className="btn-primary flex items-center gap-2 whitespace-nowrap"
        >
          <Plus size={16} /> New Group
        </button>
      </div>

      {loading ? (
        <div className="text-center py-10 text-text-3 text-sm">Loading...</div>
      ) : groups.length === 0 ? (
        <div className="text-center py-10 text-text-3 text-sm border border-dashed border-border rounded-lg">
          No food groups yet. Create one to link branches that should share a menu and stock count.
        </div>
      ) : (
        <div className="grid gap-3">
          {groups.map(g => (
            <div key={g.id} className="p-4 bg-bg-2 border border-border rounded-lg flex items-center justify-between gap-4">
              <div className="min-w-0">
                <div className="font-heading font-bold text-text flex items-center gap-2">
                  <Utensils size={14} className="text-accent shrink-0" /> {g.name}
                </div>
                <div className="text-xs text-text-2 mt-1">
                  {g.branches.length === 0
                    ? 'No branches linked yet'
                    : g.branches.map(b => b.name).join(', ')}
                </div>
                {g.branches.length > 1 && (
                  <label className="flex flex-wrap items-center gap-2 text-xs text-text-2 mt-2">
                    When the internet is down, sell the shared fridge only at:
                    <select
                      value={g.offlineSellerBranchId || ''}
                      onChange={e => handleOfflineSeller(g, e.target.value)}
                      className="form-control text-xs py-1 w-auto"
                    >
                      <option value="">Not chosen - no branch sells it offline</option>
                      {g.branches.map(b => <option key={b.id} value={b.id}>{b.name}</option>)}
                    </select>
                  </label>
                )}
              </div>
              <div className="flex items-center gap-2 shrink-0">
                <button onClick={() => openEdit(g)} className="btn-secondary text-xs px-3 py-1.5 whitespace-nowrap">
                  Manage Branches
                </button>
                <button
                  onClick={() => handleDelete(g.id)}
                  className="text-neon-red hover:text-red-400 p-2"
                  title="Delete group"
                >
                  <Trash2 size={16} />
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      <Drawer isOpen={createDrawer} onClose={() => setCreateDrawer(false)} title="New Food Group" width="400px">
        <form onSubmit={handleCreate} className="form-stack">
          <div className="form-group">
            <label>Group Name *</label>
            <input name="name" required className="form-control" placeholder="e.g. Citylight Snacks" />
          </div>
          <div className="drawer-footer pt-4">
            <button type="submit" className="btn-primary w-full flex justify-center items-center gap-2 font-heading tracking-wider">
              <Save size={16} /> CREATE GROUP
            </button>
          </div>
        </form>
      </Drawer>

      <Drawer
        isOpen={editDrawer.isOpen}
        onClose={() => setEditDrawer({ isOpen: false, group: null, selectedIds: [] })}
        title={`Branches sharing "${editDrawer.group?.name || ''}"`}
        width="400px"
      >
        <div className="space-y-1">
          {branches.map(b => (
            <label key={b.id} className="flex items-center gap-2 p-2 rounded hover:bg-bg-3 cursor-pointer">
              <input
                type="checkbox"
                checked={editDrawer.selectedIds.includes(b.id)}
                onChange={() => toggleBranch(b.id)}
                className="rounded border-text-3 text-accent focus:ring-accent bg-transparent"
              />
              <span className="text-sm text-text">{b.name}</span>
            </label>
          ))}
          <div className="drawer-footer pt-4">
            <button
              onClick={handleSaveBranches}
              className="btn-primary w-full flex justify-center items-center gap-2 font-heading tracking-wider"
            >
              <Save size={16} /> SAVE
            </button>
          </div>
        </div>
      </Drawer>
    </div>
  );
}
