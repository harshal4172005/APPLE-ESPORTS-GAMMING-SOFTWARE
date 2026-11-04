import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { Repeat, X, Loader2 } from 'lucide-react';
import api from '../../config/api';
import { useToast } from '../ui/Toast';

const hm = (min) => {
  const h = Math.floor(min / 60), m = min % 60;
  return h > 0 ? `${h} hr${m ? ` ${m} min` : ''}` : `${m} min`;
};

/** Operator changes the running package directly - same server rules as a customer's request. */
export default function ChangePackageModal({ pc, onClose, onDone }) {
  const toast = useToast();
  const [data, setData] = useState(null);
  const [error, setError] = useState(null);
  const [picked, setPicked] = useState(null);
  const [saving, setSaving] = useState(false);
  const sessionId = pc.activeSessionId;

  useEffect(() => {
    api.get(`/sessions/${sessionId}/package-options`)
      .then(res => setData(res.data?.data))
      .catch(err => setError(err.response?.data?.error || 'Could not load packages.'));
  }, [sessionId]);

  const confirm = async () => {
    if (!picked) return;
    setSaving(true);
    setError(null);
    try {
      await api.post(`/sessions/${sessionId}/change-package`, { packageId: picked.packageId });
      toast.success(`${pc.name}: now ${picked.name} (₹${Number(picked.price).toFixed(0)}, ${hm(picked.minutesLeftAfter)} left)`);
      onDone?.();
      onClose();
    } catch (err) {
      setError(err.response?.data?.error || 'Could not change the package.');
    } finally {
      setSaving(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-[9999] flex items-center justify-center p-4 bg-black/70 backdrop-blur-sm">
      <div className="w-full max-w-md bg-bg-2 border border-border rounded-lg shadow-2xl overflow-hidden">
        <div className="flex items-center justify-between p-4 border-b border-border bg-bg-3">
          <div className="flex items-center gap-2 text-text">
            <Repeat className="w-5 h-5 text-accent" />
            <h2 className="font-heading font-bold tracking-widest text-lg">CHANGE PACKAGE</h2>
          </div>
          <button onClick={onClose} className="p-1 rounded text-text-3 hover:text-text"><X className="w-5 h-5" /></button>
        </div>

        <div className="p-5 space-y-3">
          {!data && !error && <div className="flex justify-center py-6"><Loader2 className="w-6 h-6 animate-spin text-accent" /></div>}
          {data && (
            <>
              <p className="text-sm text-text-2">
                <span className="font-mono font-bold text-accent">{pc.name}</span> · now {data.currentName} (₹{Number(data.currentPrice).toFixed(0)}) ·
                played {hm(data.playedMinutes)}. The new package counts from the start.
              </p>
              {data.unavailable && <p className="text-sm text-neon-red">{data.unavailable}</p>}
              <div className="space-y-2 max-h-[50vh] overflow-y-auto">
                {data.options.map(opt => (
                  <button
                    key={opt.packageId}
                    type="button"
                    disabled={!opt.allowed}
                    onClick={() => setPicked(opt)}
                    className={`w-full text-left flex items-center justify-between gap-3 rounded border px-3 py-2.5 ${
                      picked?.packageId === opt.packageId ? 'border-accent bg-accent/10'
                        : opt.isCurrent ? 'border-accent/40' : 'border-border'
                    } ${opt.allowed ? 'hover:border-accent/60' : 'opacity-50 cursor-not-allowed'}`}
                  >
                    <div>
                      <div className="font-bold text-text">{opt.name}</div>
                      <div className="text-xs text-text-3">
                        {opt.isCurrent ? 'Current package' : opt.allowed ? `${hm(opt.minutesLeftAfter)} left after change` : opt.reason}
                      </div>
                    </div>
                    <span className="font-mono font-bold text-text">₹{Number(opt.price).toFixed(0)}</span>
                  </button>
                ))}
              </div>
            </>
          )}
          {error && <p className="text-sm text-neon-red">{error}</p>}
          <button
            onClick={confirm}
            disabled={!picked || saving}
            className="w-full py-3 rounded border border-pc-active/50 bg-pc-active/10 text-pc-active font-bold tracking-widest uppercase disabled:opacity-50"
          >
            {saving ? 'CHANGING…' : picked ? `CHANGE TO ${picked.name} · ₹${Number(picked.price).toFixed(0)}` : 'PICK A PACKAGE'}
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
