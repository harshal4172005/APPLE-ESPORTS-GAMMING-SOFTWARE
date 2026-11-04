import React, { useState } from 'react';
import { useOverlaySocket } from '../../../contexts/OverlaySocketContext';
import { Clock, Loader2, CheckCircle2 } from 'lucide-react';
import ChangePackageSection from '../components/ChangePackageSection';

export default function TimeExtensionScreen() {
  const { requestExtension, sessionData } = useOverlaySocket();
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [success, setSuccess] = useState(false);
  const [error, setError] = useState(null);

  const durationOptions = [
    { label: '30 Min', value: 30 },
    { label: '1 Hour', value: 60 },
    { label: '2 Hours', value: 120 },
    { label: '3 Hours', value: 180 },
  ];

  const handleRequest = async (minutes) => {
    setIsSubmitting(true);
    setError(null);
    try {
      const res = await requestExtension(minutes);
      if (res?.success) {
        setSuccess(true);
        setTimeout(() => setSuccess(false), 3000);
      } else {
        setError(res?.error || 'The request could not be sent. Please call the operator.');
      }
    } catch (err) {
      console.error(err);
    } finally {
      setIsSubmitting(false);
    }
  };

  if (!sessionData || sessionData.sessionStatus !== 'active') {
    return (
      <div className="p-6 text-center h-full flex flex-col items-center justify-center">
        <Clock className="w-16 h-16 text-text-3 mb-4 opacity-50" />
        <h2 className="font-heading text-xl font-bold text-text-2 tracking-wide uppercase">Not Available</h2>
        <p className="text-text-3 font-body text-sm mt-2">Time extension is only available during active sessions.</p>
      </div>
    );
  }

  // Pay-As-You-Go already bills continuously for real time with no cap — nothing to extend.
  if (!sessionData.plannedDurationMin) {
    return (
      <div className="p-6 text-center h-full flex flex-col items-center justify-center">
        <Clock className="w-16 h-16 text-text-3 mb-4 opacity-50" />
        <h2 className="font-heading text-xl font-bold text-text-2 tracking-wide uppercase">Not Needed</h2>
        <p className="text-text-3 font-body text-sm mt-2">Your session is Pay-As-You-Go — it already runs for as long as you play, with no fixed time to extend.</p>
      </div>
    );
  }

  if (success) {
    return (
      <div className="p-6 text-center h-full flex flex-col items-center justify-center">
        <div className="w-20 h-20 bg-neon-green/20 rounded-full flex items-center justify-center mb-6 shadow-[0_0_30px_rgba(34,211,166,0.3)]">
          <CheckCircle2 className="w-10 h-10 text-neon-green" />
        </div>
        <h2 className="font-heading text-2xl font-bold text-text tracking-wide uppercase">Request Sent</h2>
        <p className="text-text-2 font-body mt-2">The operator will approve your request shortly.</p>
      </div>
    );
  }

  return (
    <div className="p-6 h-full flex flex-col overflow-y-auto">
      <div className="mb-8">
        <h2 className="font-heading text-2xl font-bold text-text tracking-wider uppercase flex items-center gap-2">
          <Clock className="w-6 h-6 text-accent" />
          Extend Time
        </h2>
        <p className="text-text-2 font-body text-sm mt-2">Request more gaming time. Charges will be added to your current bill upon approval.</p>
      </div>

      {error && (
        <p className="mb-4 text-sm text-neon-red font-body">{error}</p>
      )}

      <div className="grid grid-cols-2 gap-4">
        {durationOptions.map((opt) => (
          <button
            key={opt.value}
            onClick={() => handleRequest(opt.value)}
            disabled={isSubmitting}
            className="card bg-bg-3 border-border hover:border-accent flex flex-col items-center justify-center p-6 gap-2 transition-all hover:scale-105 active:scale-95 disabled:opacity-50 disabled:pointer-events-none"
          >
            <span className="font-mono text-3xl font-bold text-text">{opt.value >= 60 ? opt.value / 60 : opt.value}</span>
            <span className="font-heading uppercase tracking-wider text-sm font-bold text-text-2">
              {opt.value >= 60 ? (opt.value === 60 ? 'Hour' : 'Hours') : 'Minutes'}
            </span>
          </button>
        ))}
      </div>

      <ChangePackageSection />

      {isSubmitting && (
        <div className="absolute inset-0 bg-bg/80 backdrop-blur-sm flex flex-col items-center justify-center z-10">
          <Loader2 className="w-12 h-12 text-accent animate-spin mb-4" />
          <p className="font-heading text-lg font-bold tracking-wider uppercase">Sending Request...</p>
        </div>
      )}
    </div>
  );
}
