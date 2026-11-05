import { useState, useEffect } from 'react';
import { getPricingProfiles, createPricingProfile, updatePricingProfile, deletePricingProfile, createPricingPackage, updatePricingPackage, deletePricingPackage } from '../../api/settings.api';
import { useToast } from '../../components/ui/Toast';
import { Plus, Edit, Trash2, MapPin, Package, X } from 'lucide-react';
import { useBranch } from '../../contexts/BranchContext';

export default function PricingProfilesTab() {
  const { branches } = useBranch();
  const toast = useToast();
  
  const [selectedBranchId, setSelectedBranchId] = useState('');
  const [profiles, setProfiles] = useState([]);
  const [loading, setLoading] = useState(false);
  const [drawer, setDrawer] = useState({ isOpen: false, data: null });
  const [packageForm, setPackageForm] = useState({ id: null, name: '', durationMinutes: '', price: '', fromMinutes: '' });
  const [slabRows, setSlabRows] = useState([]);

  // Initially select first branch if none selected
  useEffect(() => {
    if (branches.length > 0 && !selectedBranchId) {
      setSelectedBranchId(branches[0].id);
    }
  }, [branches, selectedBranchId]);

  useEffect(() => {
    if (selectedBranchId) {
      fetchProfiles();
    }
  }, [selectedBranchId]);

  const fetchProfiles = async () => {
    try {
      setLoading(true);
      const res = await getPricingProfiles(selectedBranchId);
      setProfiles(res.data || []);
    } catch (err) {
      toast.error('Failed to load pricing profiles');
    } finally {
      setLoading(false);
    }
  };

  const handleSave = async (e) => {
    e.preventDefault();
    const formData = new FormData(e.target);
    const validSlabs = slabRows.filter(s => s.fromMinutes !== '' && s.toMinutes !== '' && s.amount !== '');
    const payload = {
      name: formData.get('name'),
      baseHourlyRate: Number(formData.get('baseHourlyRate')),
      bufferMinutes: Number(formData.get('bufferMinutes')),
      isActive: formData.get('isActive') === 'on',
      branchId: selectedBranchId,
      refreshRate: formData.get('refreshRate') || null,
      systemSpecs: formData.get('systemSpecs') || null,
      shortSessionSlabs: validSlabs.length > 0 ? validSlabs.map(s => ({ fromMinutes: Number(s.fromMinutes), toMinutes: Number(s.toMinutes), amount: Number(s.amount) })) : null
    };

    try {
      if (drawer.data) {
        await updatePricingProfile(drawer.data.id, payload);
        toast.success('Pricing profile updated successfully');
      } else {
        await createPricingProfile(payload);
        toast.success('Pricing profile created successfully');
      }
      setDrawer({ isOpen: false, data: null });
      fetchProfiles();
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to save profile');
    }
  };

  const handleDelete = async (id) => {
    if (window.confirm('Are you sure you want to deactivate this pricing profile? Existing historical bills will not be affected.')) {
      try {
        await deletePricingProfile(id);
        toast.success('Profile deactivated successfully');
        fetchProfiles();
      } catch (err) {
        toast.error(err.response?.data?.error || 'Failed to deactivate profile');
      }
    }
  };

  // Refetches profiles and keeps the open drawer's snapshot in sync, so the packages list
  // updates in place without closing the drawer after every add/edit/delete.
  const refreshDrawerPackages = async (profileId) => {
    try {
      const res = await getPricingProfiles(selectedBranchId);
      const list = res.data || [];
      setProfiles(list);
      const updated = list.find(p => p.id === profileId);
      if (updated) setDrawer(d => ({ ...d, data: updated }));
    } catch (err) {
      toast.error('Failed to refresh packages');
    }
  };

  const resetPackageForm = () => setPackageForm({ id: null, name: '', durationMinutes: '', price: '', fromMinutes: '' });

  const handlePackageSubmit = async (e) => {
    e.preventDefault();
    if (!packageForm.name || !packageForm.durationMinutes || packageForm.price === '') return;
    const fromMin = packageForm.fromMinutes === '' ? null : Number(packageForm.fromMinutes);

    try {
      if (packageForm.id) {
        await updatePricingPackage(packageForm.id, {
          name: packageForm.name,
          durationMinutes: Number(packageForm.durationMinutes),
          price: Number(packageForm.price),
          sortOrder: Number(packageForm.durationMinutes),
          packagePriceFromMin: fromMin,
          isActive: true
        });
        toast.success('Package updated');
      } else {
        await createPricingPackage({
          pricingProfileId: drawer.data.id,
          name: packageForm.name,
          durationMinutes: Number(packageForm.durationMinutes),
          price: Number(packageForm.price),
          sortOrder: Number(packageForm.durationMinutes),
          packagePriceFromMin: fromMin
        });
        toast.success('Package added');
      }
      resetPackageForm();
      refreshDrawerPackages(drawer.data.id);
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to save package');
    }
  };

  const handleEditPackage = (pkg) => {
    setPackageForm({ id: pkg.id, name: pkg.name, durationMinutes: pkg.durationMinutes, price: pkg.price, fromMinutes: pkg.packagePriceFromMin ?? '' });
  };

  const handleDeletePackage = async (pkg) => {
    if (!window.confirm(`Remove the "${pkg.name}" package?`)) return;
    try {
      await deletePricingPackage(pkg.id);
      toast.success('Package removed');
      if (packageForm.id === pkg.id) resetPackageForm();
      refreshDrawerPackages(drawer.data.id);
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to remove package');
    }
  };

  const renderDrawer = () => (
    <div className={`fixed inset-y-0 right-0 w-96 bg-bg-2 border-l border-border shadow-2xl transform transition-transform duration-300 z-50 ${drawer.isOpen ? 'translate-x-0' : 'translate-x-full'}`}>
      <div className="h-full flex flex-col">
        <div className="p-4 border-b border-border flex items-center justify-between bg-bg-3">
          <h2 className="font-heading font-bold text-accent tracking-wider">{drawer.data ? 'Edit Profile' : 'New Pricing Profile'}</h2>
          <button onClick={() => { setDrawer({ isOpen: false, data: null }); resetPackageForm(); }} className="text-text-3 hover:text-text transition-colors text-2xl leading-none">&times;</button>
        </div>
        <div className="flex-1 overflow-y-auto p-4 custom-scrollbar">
          <form onSubmit={handleSave} className="form-stack text-xs">
            <div className="form-group">
              <label>Profile / Zone Name *</label>
              <input name="name" required defaultValue={drawer.data?.name} className="form-control" placeholder="e.g. VIP ELITE HUB" />
            </div>
            
            <div className="form-group">
              <label>Base Hourly Rate (₹) *</label>
              <input type="number" step="1" name="baseHourlyRate" required defaultValue={drawer.data?.baseHourlyRate} className="form-control" placeholder="e.g. 80" />
            </div>

            <div className="form-group">
              <label>Free Buffer / Grace Period (minutes) *</label>
              <input type="number" step="1" min="0" name="bufferMinutes" required defaultValue={drawer.data?.bufferMinutes ?? 10} className="form-control" placeholder="e.g. 10" />
              <p className="text-[10px] text-text-3 mt-1">Customers who end their session within this many minutes are charged ₹0. Applies live everywhere — session, PC cards, billing counter, member overlay.</p>
            </div>

            <div className="mt-3 p-3 bg-bg-3 border border-border rounded">
              <label className="font-heading font-bold text-accent tracking-wider text-[10px] uppercase">Short Session Rates</label>
              <p className="text-[10px] text-text-3 mt-0.5 mb-2">Fixed charges for sub-hour sessions. After the last slab, normal hourly rate applies.</p>
              {slabRows.length > 0 && (
                <div className="space-y-1.5 mb-2">
                  {slabRows.map((row, i) => (
                    <div key={i} className="grid grid-cols-[1fr_1fr_1fr_auto] gap-1.5 items-end">
                      <div>
                        {i === 0 && <label className="text-[10px] text-text-3">From (min)</label>}
                        <input type="number" min="0" step="1" className="form-control text-xs" placeholder="0" value={row.fromMinutes} onChange={e => { const v = [...slabRows]; v[i] = { ...v[i], fromMinutes: e.target.value }; setSlabRows(v); }} />
                      </div>
                      <div>
                        {i === 0 && <label className="text-[10px] text-text-3">To (min)</label>}
                        <input type="number" min="1" step="1" className="form-control text-xs" placeholder="10" value={row.toMinutes} onChange={e => { const v = [...slabRows]; v[i] = { ...v[i], toMinutes: e.target.value }; setSlabRows(v); }} />
                      </div>
                      <div>
                        {i === 0 && <label className="text-[10px] text-text-3">Charge (₹)</label>}
                        <input type="number" min="0" step="1" className="form-control text-xs" placeholder="20" value={row.amount} onChange={e => { const v = [...slabRows]; v[i] = { ...v[i], amount: e.target.value }; setSlabRows(v); }} />
                      </div>
                      <button type="button" onClick={() => setSlabRows(slabRows.filter((_, j) => j !== i))} className="btn-icon mb-0.5" title="Remove row">
                        <X size={12} className="text-red-400" />
                      </button>
                    </div>
                  ))}
                </div>
              )}
              <button type="button" onClick={() => setSlabRows([...slabRows, { fromMinutes: '', toMinutes: '', amount: '' }])} className="text-[10px] text-accent hover:underline flex items-center gap-1">
                <Plus size={10} /> Add slab row
              </button>
            </div>

            <div className="form-group">
              <label>Refresh Rate (Hz)</label>
              <select name="refreshRate" defaultValue={drawer.data?.refreshRate || ''} className="form-control">
                <option value="">-- Standard / Not Specified --</option>
                <option value="60Hz">60Hz</option>
                <option value="144Hz">144Hz</option>
                <option value="240Hz">240Hz</option>
                <option value="360Hz">360Hz</option>
              </select>
            </div>

            <div className="form-group">
              <label>System Specifications</label>
              <input name="systemSpecs" defaultValue={drawer.data?.systemSpecs || ''} className="form-control" placeholder="e.g. RTX 4090 / i9 14900K / 32GB DDR5" />
            </div>

            <div className="mt-4">
              <label className="flex items-center gap-2 cursor-pointer">
                <input type="checkbox" name="isActive" defaultChecked={drawer.data ? drawer.data.isActive : true} className="rounded border-text-3 text-accent focus:ring-accent bg-transparent" />
                <span className="font-medium text-text">Profile Active</span>
              </label>
              <p className="text-[10px] text-text-3 mt-1 ml-6">If inactive, this profile will not be selectable when adding new PCs.</p>
            </div>

            <div className="drawer-footer pt-4 mt-6 border-t border-border">
              <button type="submit" className="btn-primary w-full flex justify-center items-center gap-2">
                SAVE PROFILE
              </button>
            </div>
          </form>

          {drawer.data && (
            <div className="mt-6 pt-4 border-t border-border">
              <h3 className="font-heading font-bold text-accent tracking-wider text-xs flex items-center gap-2">
                <Package size={14} /> CUSTOM PACKAGES
              </h3>
              <p className="text-[10px] text-text-3 mt-1">
                Fixed duration/price deals (e.g. "4 hrs - ₹180") shown to customers instead of the auto 1/2/3-hour multiples.
                Leave empty to keep using the hourly-rate multiples.
              </p>

              {(drawer.data.packages || []).length > 0 && (
                <div className="mt-3 space-y-1.5">
                  {drawer.data.packages.map(pkg => (
                    <div key={pkg.id} className="flex items-center justify-between bg-bg-3 border border-border rounded px-2 py-1.5 text-xs">
                      <span className="text-text">{pkg.name}</span>
                      <span className="text-text-2 font-mono">
                        {pkg.durationMinutes}m{pkg.packagePriceFromMin ? ` (from ${pkg.packagePriceFromMin}m)` : ''}
                      </span>
                      <span className="text-accent font-mono">₹{Number(pkg.price).toFixed(2)}</span>
                      <div className="flex gap-1.5">
                        <button type="button" onClick={() => handleEditPackage(pkg)} className="btn-icon" title="Edit package">
                          <Edit size={12} className="text-blue-400" />
                        </button>
                        <button type="button" onClick={() => handleDeletePackage(pkg)} className="btn-icon" title="Remove package">
                          <Trash2 size={12} className="text-red-400" />
                        </button>
                      </div>
                    </div>
                  ))}
                </div>
              )}

              <form onSubmit={handlePackageSubmit} className="form-stack text-xs mt-3 bg-bg-3 border border-border rounded p-3">
                <div className="grid grid-cols-3 gap-2">
                  <div className="form-group col-span-3">
                    <label>Package Name</label>
                    <input
                      className="form-control"
                      placeholder="e.g. 30 min"
                      value={packageForm.name}
                      onChange={(e) => setPackageForm(f => ({ ...f, name: e.target.value }))}
                    />
                  </div>
                  <div className="form-group">
                    <label>Minutes</label>
                    <input
                      type="number" min="1" step="1"
                      className="form-control"
                      placeholder="30"
                      value={packageForm.durationMinutes}
                      onChange={(e) => setPackageForm(f => ({ ...f, durationMinutes: e.target.value }))}
                    />
                  </div>
                  <div className="form-group col-span-2">
                    <label>Price (₹)</label>
                    <input
                      type="number" min="0" step="1"
                      className="form-control"
                      placeholder="30"
                      value={packageForm.price}
                      onChange={(e) => setPackageForm(f => ({ ...f, price: e.target.value }))}
                    />
                  </div>
                  <div className="form-group col-span-3">
                    <label>Package price from (minutes played)</label>
                    <input
                      type="number" min="1" step="1"
                      className="form-control"
                      placeholder={packageForm.durationMinutes ? `Empty = from ${Math.max(1, packageForm.durationMinutes - (drawer.data.bufferMinutes ?? 10))} min` : 'e.g. 210 for 3 hr 30 min'}
                      value={packageForm.fromMinutes}
                      onChange={(e) => setPackageForm(f => ({ ...f, fromMinutes: e.target.value }))}
                    />
                    <span className="text-[10px] text-text-3">
                      Played less than this is charged by the hour. Customers never see it - they only see the package.
                    </span>
                  </div>
                </div>
                <div className="flex gap-2 mt-2">
                  <button type="submit" className="btn-primary flex-1 flex justify-center items-center gap-2 text-xs py-1.5">
                    <Plus size={12} /> {packageForm.id ? 'UPDATE PACKAGE' : 'ADD PACKAGE'}
                  </button>
                  {packageForm.id && (
                    <button type="button" onClick={resetPackageForm} className="btn-icon" title="Cancel edit">
                      <X size={14} />
                    </button>
                  )}
                </div>
              </form>
            </div>
          )}
        </div>
      </div>
    </div>
  );

  return (
    <div className="tab-pane fade-in space-y-4">
      <div className="pane-header flex-col md:flex-row md:items-center gap-4">
        <div>
          <h2>Branch-Wise Pricing Profiles</h2>
          <p className="text-text-2 text-xs mt-1">Manage dynamic PC zones and pricing overlays tailored to each physical branch.</p>
        </div>
        <div className="flex items-center gap-3 w-full md:w-auto">
          <div className="relative flex-1 md:w-48">
            <div className="absolute inset-y-0 left-0 pl-2.5 flex items-center pointer-events-none">
              <MapPin className="h-4 w-4 text-text-3" />
            </div>
            <select
              value={selectedBranchId}
              onChange={(e) => setSelectedBranchId(e.target.value)}
              className="input w-full pl-9 py-1.5 text-xs bg-bg-3 border border-border rounded"
            >
              <option value="" disabled>Select Branch...</option>
              {branches.map(b => (
                <option key={b.id} value={b.id}>{b.name}</option>
              ))}
            </select>
          </div>
          <button 
            className="btn-primary flex items-center gap-2 text-xs py-1.5"
            onClick={() => { setDrawer({ isOpen: true, data: null }); resetPackageForm(); setSlabRows([]); }}
            disabled={!selectedBranchId}
          >
            <Plus size={14} /> NEW PROFILE
          </button>
        </div>
      </div>

      {loading ? (
        <div className="flex flex-col items-center justify-center py-16 space-y-2">
          <div className="w-6 h-6 rounded-full border-2 border-accent border-t-transparent animate-spin" />
        </div>
      ) : profiles.length === 0 ? (
        <div className="text-center py-12 border border-dashed border-border rounded-lg bg-bg-3">
          <p className="text-text-3 text-sm">No pricing profiles defined for this branch.</p>
        </div>
      ) : (
        <div className="table-container overflow-x-auto">
          <table className="data-table text-xs w-full">
            <thead>
              <tr className="border-b border-border">
                <th>Zone / Profile Name</th>
                <th>Hourly Base Rate</th>
                <th>Free Buffer</th>
                <th>Refresh Rate (Hz)</th>
                <th>System Specs</th>
                <th>Packages</th>
                <th>Status</th>
                <th className="w-16"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/60">
              {profiles.map(p => (
                <tr key={p.id} className="hover:bg-bg-3/50 transition-colors group">
                  <td className="font-medium text-text">{p.name}</td>
                  <td className="font-mono text-accent">₹{p.baseHourlyRate.toFixed(2)}/hr</td>
                  <td className="text-text-2 font-mono">{p.bufferMinutes ?? 10}m free</td>
                  <td className="text-text-2">{p.refreshRate || '-'}</td>
                  <td className="text-text-2">{p.systemSpecs || '-'}</td>
                  <td className="text-text-2">
                    {(p.packages || []).length > 0 ? `${p.packages.length} custom` : <span className="text-text-3">hourly x1/2/3</span>}
                  </td>
                  <td>
                    {p.isActive ? (
                      <span className="px-2 py-0.5 rounded text-[10px] font-semibold bg-green-500/20 text-green-400">ACTIVE</span>
                    ) : (
                      <span className="px-2 py-0.5 rounded text-[10px] font-semibold bg-red-500/20 text-red-400">INACTIVE</span>
                    )}
                  </td>
                  <td className="flex justify-end gap-2 opacity-0 group-hover:opacity-100 transition-opacity">
                    <button onClick={() => { setDrawer({ isOpen: true, data: p }); resetPackageForm(); setSlabRows((p.shortSessionSlabs || []).map(s => ({ fromMinutes: String(s.fromMinutes), toMinutes: String(s.toMinutes), amount: String(s.amount) }))); }} className="btn-icon" title="Edit Profile">
                      <Edit size={14} className="text-blue-400" />
                    </button>
                    {p.isActive && (
                      <button onClick={() => handleDelete(p.id)} className="btn-icon" title="Deactivate">
                        <Trash2 size={14} className="text-red-400" />
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {renderDrawer()}
      {drawer.isOpen && (
        <div className="fixed inset-0 bg-bg/60 backdrop-blur-sm z-40" onClick={() => { setDrawer({ isOpen: false, data: null }); resetPackageForm(); setSlabRows([]); }} />
      )}
    </div>
  );
}
