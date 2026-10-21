import { useEffect, useState } from 'react';
import { WifiOff } from 'lucide-react';
import api from '../../config/api';
import { useSocket } from '../../contexts/SocketContext';

/**
 * A number that goes up every time the branch says stock moved (SharedStockWatcher sends
 * "StockChanged" the moment Head Office's shared fridge count changes, or this counter's own).
 * Put it in a screen's load effect dependencies and the screen reloads its menu instantly.
 */
export function useStockVersion() {
  const { subscribe, SIGNALR_HUBS } = useSocket();
  const [version, setVersion] = useState(0);
  useEffect(() => subscribe(SIGNALR_HUBS.FOOD_ORDERS, 'StockChanged', () => setVersion(v => v + 1)),
    [subscribe, SIGNALR_HUBS]);
  return version;
}

/** Shown only when this branch shares a fridge and Head Office cannot be reached. */
export function SharedStockBanner() {
  const [message, setMessage] = useState(null);

  useEffect(() => {
    let alive = true;
    const check = async () => {
      try {
        const res = await api.get('/shared-stock/status');
        if (alive) setMessage(res.data?.data?.message || null);
      } catch { /* branch API unreachable - other screens already say so */ }
    };
    check();
    const timer = setInterval(check, 5000);
    return () => { alive = false; clearInterval(timer); };
  }, []);

  if (!message) return null;
  return (
    <div className="flex items-center gap-2 px-3 py-2 mb-3 rounded border border-neon-orange/50 bg-neon-orange/10 text-neon-orange text-xs font-bold">
      <WifiOff size={14} className="shrink-0" /> {message}
    </div>
  );
}
