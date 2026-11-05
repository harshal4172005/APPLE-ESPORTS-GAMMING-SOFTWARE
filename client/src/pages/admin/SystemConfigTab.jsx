import React, { useState, useEffect } from 'react';
import { getSystemConfigs, saveSystemConfig, getWalletTopUpRules, saveWalletTopUpRules, testEmailConfig } from '../../api/settings.api';
import { useAuth } from '../../contexts/AuthContext';
import { useToast } from '../../components/ui/Toast';
import { Save, Wallet, Send, Plus, Trash2 } from 'lucide-react';

// ── Lets an admin find out *why* mail isn't arriving without reading a log file on the
// server. SendEmailAsync (the real forgot-password/top-up path) never throws - a wrong app
// password or an unreachable Head Office both look identical to "nothing happened" - so this
// is the only way to see the actual error. ──
function TestEmailButton({ defaultTo }) {
  const toast = useToast();
  const [to, setTo] = useState(defaultTo || '');
  const [sending, setSending] = useState(false);

  useEffect(() => {
    if (!to && defaultTo) setTo(defaultTo);
  }, [defaultTo]);

  const handleTest = async () => {
    if (!to.trim()) {
      toast.error('Enter an address to send the test to first.');
      return;
    }
    setSending(true);
    try {
      const res = await testEmailConfig(to.trim());
      toast.success(res.data?.message || 'Test email sent.');
    } catch (err) {
      toast.error(err.response?.data?.error || err.response?.data?.message || 'Test email failed.');
    } finally {
      setSending(false);
    }
  };

  return (
    <div className="form-group md:col-span-2 mt-2 flex items-end gap-2">
      <div className="flex-1">
        <label>Send a test email to</label>
        <input
          type="email"
          value={to}
          onChange={(e) => setTo(e.target.value)}
          className="form-control"
          placeholder="you@gmail.com"
        />
      </div>
      <button
        type="button"
        onClick={handleTest}
        disabled={sending}
        className="btn-secondary flex items-center gap-1.5 text-xs py-2 px-3 disabled:opacity-50"
      >
        <Send size={13} /> {sending ? 'SENDING...' : 'SEND TEST'}
      </button>
    </div>
  );
}

function WalletTopUpSettingsCard() {
  const toast = useToast();
  const [minTopUp, setMinTopUp] = useState(500);
  const [defaultBonus, setDefaultBonus] = useState(0);
  const [tiers, setTiers] = useState([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    getWalletTopUpRules()
      .then(res => {
        if (res?.data) {
          setMinTopUp(res.data.minGamingTopUp);
          setDefaultBonus(res.data.defaultBonusPercent);
          setTiers(res.data.bonusTiers || []);
        }
      })
      .catch(() => toast.error('Failed to load Member Amount top-up settings'))
      .finally(() => setLoading(false));
  }, []);

  const addTier = () => setTiers(t => [...t, { minAmount: '', bonusPercent: '' }]);
  const removeTier = (i) => setTiers(t => t.filter((_, idx) => idx !== i));
  const updateTier = (i, field, val) => setTiers(t => t.map((r, idx) => idx === i ? { ...r, [field]: val } : r));

  const handleSave = async (e) => {
    e.preventDefault();
    const validTiers = tiers
      .filter(t => Number(t.minAmount) > 0 && Number(t.bonusPercent) >= 0)
      .map(t => ({ minAmount: Number(t.minAmount), bonusPercent: Number(t.bonusPercent) }))
      .sort((a, b) => a.minAmount - b.minAmount);
    const payload = {
      minGamingTopUp: Number(minTopUp),
      defaultBonusPercent: Number(defaultBonus),
      bonusTiers: validTiers,
    };
    setSaving(true);
    try {
      await saveWalletTopUpRules(payload);
      setTiers(validTiers);
      toast.success('Member Amount top-up settings saved');
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to save Member Amount top-up settings');
    } finally {
      setSaving(false);
    }
  };

  if (loading) return null;

  return (
    <form onSubmit={handleSave} className="bg-bg-2 p-5 rounded-lg border border-border">
      <h3 className="text-sm font-semibold mb-1 text-accent flex items-center gap-2">
        <Wallet size={14} /> Member Amount Top-Up Settings
      </h3>
      <p className="text-xs text-text-2 mb-4">Controls every Gaming Member Amount top-up across all branches — the minimum amount allowed and the default bonus % applied automatically.</p>
      <div className="grid grid-cols-2 gap-4">
        <div className="form-group">
          <label>Minimum Gaming Top-Up (₹)</label>
          <input type="number" min="1" value={minTopUp} onChange={e => setMinTopUp(e.target.value)} className="form-control" />
        </div>
        <div className="form-group">
          <label>Default Bonus (%)</label>
          <input type="number" min="0" step="0.1" value={defaultBonus} onChange={e => setDefaultBonus(e.target.value)} className="form-control" />
          <span className="text-[10px] text-text-3 mt-1 block">Used when no tier matches the top-up amount</span>
        </div>
      </div>

      <div className="mt-5 border-t border-border pt-4">
        <div className="flex items-center justify-between mb-3">
          <div>
            <h4 className="text-xs font-bold text-text uppercase tracking-widest">Bonus Tiers</h4>
            <p className="text-[10px] text-text-3 mt-0.5">Specific bonus % for specific top-up amounts. The highest matching tier wins.</p>
          </div>
          <button type="button" onClick={addTier} className="flex items-center gap-1 px-2.5 py-1 rounded border border-accent/40 bg-accent/10 text-accent text-[11px] font-bold uppercase tracking-wider hover:bg-accent/20 transition-colors">
            <Plus size={12} /> Add Tier
          </button>
        </div>
        {tiers.length === 0 && (
          <p className="text-[11px] text-text-3 italic">No tiers — every top-up uses the default bonus % above.</p>
        )}
        <div className="space-y-2">
          {tiers.map((tier, i) => (
            <div key={i} className="flex items-center gap-2">
              <div className="flex-1">
                <input
                  type="number" min="1" placeholder="Min ₹ amount"
                  value={tier.minAmount} onChange={e => updateTier(i, 'minAmount', e.target.value)}
                  className="form-control text-sm"
                />
              </div>
              <span className="text-text-3 text-xs font-bold">→</span>
              <div className="w-24">
                <input
                  type="number" min="0" step="0.1" placeholder="%"
                  value={tier.bonusPercent} onChange={e => updateTier(i, 'bonusPercent', e.target.value)}
                  className="form-control text-sm"
                />
              </div>
              <span className="text-text-3 text-[10px]">%</span>
              <button type="button" onClick={() => removeTier(i)} className="p-1.5 rounded text-neon-red/60 hover:text-neon-red hover:bg-neon-red/10 transition-colors">
                <Trash2 size={14} />
              </button>
            </div>
          ))}
        </div>
        {tiers.length > 0 && (
          <p className="text-[10px] text-text-3 mt-2">
            Example: if tiers are ₹500 → 5% and ₹1000 → 10%, a ₹700 top-up gets 5% bonus, ₹1500 gets 10%.
          </p>
        )}
      </div>

      <div className="flex justify-end mt-4">
        <button type="submit" disabled={saving} className="btn-primary flex items-center gap-2 shadow-lg shadow-accent/25 disabled:opacity-50">
          <Save size={14} /> {saving ? 'SAVING...' : 'SAVE MEMBER AMOUNT SETTINGS'}
        </button>
      </div>
    </form>
  );
}

export default function SystemConfigTab() {
  const [configs, setConfigs] = useState({});
  const [loading, setLoading] = useState(false);
  const toast = useToast();
  const { hasDashboardAccess } = useAuth();

  useEffect(() => {
    loadConfigs();
  }, []);

  const loadConfigs = async () => {
    setLoading(true);
    try {
      const res = await getSystemConfigs();
      const configMap = {};
      res.data?.forEach(c => {
        configMap[c.configKey] = c.configValue;
      });
      setConfigs(configMap);
    } catch (err) {
      toast.error('Failed to load system configurations');
    } finally {
      setLoading(false);
    }
  };

  const handleSave = async (e) => {
    e.preventDefault();
    const formData = new FormData(e.target);

    const data = {
      reservation: {
        gracePeriodMinutes: Number(formData.get('gracePeriodMinutes')),
        maxAdvanceDays: Number(formData.get('maxAdvanceDays'))
      },
      loyalty: {
        pointsPerRupee: Number(formData.get('pointsPerRupee')),
        minRedemption: Number(formData.get('minRedemption'))
      },
      emailNotifications: {
        receivers: formData.get('emailReceivers'),
        sender: formData.get('emailSender'),
        appPassword: formData.get('emailAppPassword')
      }
    };

    try {
      await saveSystemConfig({
        configKey: 'global_system_rules',
        configValue: data,
        description: 'Global system rules for reservations and loyalty'
      });
      toast.success('System configuration saved successfully');
    } catch (err) {
      toast.error('Failed to save configurations');
    }
  };

  if (loading) return <div className="text-center py-10 text-text-2">Loading configs...</div>;

  const currentRules = configs['global_system_rules'] || {
    reservation: { gracePeriodMinutes: 15, maxAdvanceDays: 7 },
    loyalty: { pointsPerRupee: 0.1, minRedemption: 100 },
    emailNotifications: {
      receivers: '',
      sender: '',
      appPassword: ''
    }
  };

  return (
    <div className="tab-pane fade-in space-y-6">
      <div className="pane-header">
        <div>
          <h2>System Configuration</h2>
          <p className="text-text-2 text-xs mt-1">Configure reservation rules and global loyalty settings. Pricing lives entirely in Settings → Pricing Profiles.</p>
        </div>
      </div>

      <form onSubmit={handleSave} className="space-y-6">
        <div className="bg-bg-2 p-5 rounded-lg border border-border">
          <h3 className="text-sm font-semibold mb-4 text-accent">Reservation Rules</h3>
          <div className="grid grid-cols-2 gap-4">
            <div className="form-group">
              <label>Grace Period (Minutes before auto-cancel)</label>
              <input type="number" name="gracePeriodMinutes" defaultValue={currentRules.reservation.gracePeriodMinutes} className="form-control" />
            </div>
            <div className="form-group">
              <label>Max Advance Booking (Days)</label>
              <input type="number" name="maxAdvanceDays" defaultValue={currentRules.reservation.maxAdvanceDays} className="form-control" />
            </div>
          </div>
        </div>

        <div className="bg-bg-2 p-5 rounded-lg border border-border">
          <h3 className="text-sm font-semibold mb-4 text-accent">Loyalty & Member Amount Rules</h3>
          <div className="grid grid-cols-2 gap-4">
            <div className="form-group">
              <label>Points Per ₹ Spent</label>
              <input type="number" step="0.01" name="pointsPerRupee" defaultValue={currentRules.loyalty.pointsPerRupee} className="form-control" />
            </div>
            <div className="form-group">
              <label>Minimum Points for Redemption</label>
              <input type="number" name="minRedemption" defaultValue={currentRules.loyalty?.minRedemption} className="form-control" />
            </div>
          </div>
        </div>

        <div className="bg-bg-2 p-5 rounded-lg border border-border">
          <h3 className="text-sm font-semibold mb-1 text-accent">Email Verifier & Notifications</h3>
          <p className="text-xs text-text-2 mb-4">Set up email sender credentials and receivers to get alerts when members or operators join/leave.</p>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
            <div className="form-group">
              <label>Sender Mail ID (Super Admin)</label>
              <input type="email" name="emailSender" defaultValue={currentRules.emailNotifications?.sender} className="form-control" placeholder="superadmin@gmail.com" />
            </div>
            <div className="form-group">
              <label>App Password (16-letter Gmail App Password)</label>
              <input type="password" name="emailAppPassword" defaultValue={currentRules.emailNotifications?.appPassword} className="form-control" placeholder="abcd efgh ijkl mnop" />
            </div>
            <div className="form-group md:col-span-2 mt-2">
              <label>Notification Receiver Email(s) (Comma separated)</label>
              <input type="text" name="emailReceivers" defaultValue={currentRules.emailNotifications?.receivers} className="form-control" placeholder="receiver1@gmail.com, receiver2@gmail.com" />
            </div>
          </div>

          <p className="text-xs text-text-2 mb-2">
            Save your sender + app password above first, then send a test to confirm it actually works — a wrong password otherwise fails silently, with no error anywhere.
          </p>
          <TestEmailButton defaultTo={currentRules.emailNotifications?.sender} />
        </div>

        <div className="flex justify-end">
          <button type="submit" className="btn-primary flex items-center gap-2 shadow-lg shadow-accent/25">
            <Save size={14} /> SAVE CONFIGURATIONS
          </button>
        </div>
      </form>

      {hasDashboardAccess('wallet_settings') && <WalletTopUpSettingsCard />}
    </div>
  );
}
