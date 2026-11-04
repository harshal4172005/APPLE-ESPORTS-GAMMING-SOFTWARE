import { useCallback, useEffect, useState } from 'react';
import { Repeat, Loader2, CheckCircle2 } from 'lucide-react';
import api from '../../../config/api';
import { useOverlaySocket } from '../../../contexts/OverlaySocketContext';

const hm = (min) => {
  const h = Math.floor(min / 60), m = min % 60;
  return h > 0 ? `${h} hr${m ? ` ${m} min` : ''}` : `${m} min`;
};

/**
 * "Change package" on the gaming PC: every package with its price and the time left if switched
 * now, as the server works it out. Request goes to the operator to approve.
 */
export default function ChangePackageSection() {
  const { pcId, requestPackageChange, packageNotice } = useOverlaySocket();
  const [data, setData] = useState(null);
  const [sendingId, setSendingId] = useState(null);
  const [sentId, setSentId] = useState(null);
  const [message, setMessage] = useState(null);

  const load = useCallback(async () => {
    try {
      const res = await api.get(`/public/pcs/${pcId}/package-options`);
      setData(res.data?.success ? res.data.data : null);
    } catch { setData(null); }
  }, [pcId]);

  // Time left changes as they play - refresh the list every minute.
  useEffect(() => { load(); const t = setInterval(load, 60000); return () => clearInterval(t); }, [load]);

  useEffect(() => {
    if (!packageNotice) return;
    setSentId(null);
    setMessage(packageNotice.type === 'changed'
      ? { ok: true, text: 'Package changed. Your timer and bill are updated.' }
      : { ok: false, text: packageNotice.reason || 'The operator declined the package change.' });
    load();
  }, [packageNotice, load]);

  const request = async (opt) => {
    setSendingId(opt.packageId);
    setMessage(null);
    try {
      const res = await requestPackageChange(opt.packageId);
      if (res?.success) {
        setSentId(opt.packageId);
        setMessage({ ok: true, text: 'Request sent — the operator will approve shortly.' });
      } else {
        setMessage({ ok: false, text: res?.error || 'The request could not be sent. Please call the operator.' });
      }
    } finally {
      setSendingId(null);
    }
  };

  if (!data || data.unavailable || data.options.length === 0) return null;

  return (
    <div className="mt-8">
      <h3 className="font-heading text-lg font-bold text-text tracking-wider uppercase flex items-center gap-2">
        <Repeat className="w-5 h-5 text-accent" /> Change Package
      </h3>
      <p className="text-text-3 font-body text-xs mt-1 mb-3">
        You have played {hm(data.playedMinutes)}. The new package counts from when you started.
      </p>

      {message && (
        <p className={`mb-3 text-sm font-body flex items-center gap-1.5 ${message.ok ? 'text-neon-green' : 'text-neon-red'}`}>
          {message.ok && <CheckCircle2 className="w-4 h-4" />} {message.text}
        </p>
      )}

      <div className="space-y-2">
        {data.options.map(opt => (
          <div
            key={opt.packageId}
            className={`flex items-center justify-between gap-3 rounded-lg border px-4 py-3 ${
              opt.isCurrent ? 'border-accent/50 bg-accent/5' : opt.allowed ? 'border-border bg-bg-3' : 'border-border bg-bg-3 opacity-50'
            }`}
          >
            <div className="min-w-0">
              <div className="font-heading font-bold text-text">{opt.name}</div>
              <div className="text-xs font-body text-text-3">
                {opt.isCurrent ? 'Current package'
                  : opt.allowed ? `${hm(opt.minutesLeftAfter)} left after change`
                  : opt.reason}
              </div>
            </div>
            <div className="flex items-center gap-3 shrink-0">
              <span className="font-mono font-bold text-text">₹{Number(opt.price).toFixed(0)}</span>
              {opt.allowed && (
                <button
                  onClick={() => request(opt)}
                  disabled={!!sendingId || sentId === opt.packageId}
                  className="btn-primary text-xs px-3 py-1.5 disabled:opacity-50"
                >
                  {sendingId === opt.packageId ? <Loader2 className="w-4 h-4 animate-spin" />
                    : sentId === opt.packageId ? 'Sent' : 'Request'}
                </button>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
