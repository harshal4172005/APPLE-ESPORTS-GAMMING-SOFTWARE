import React, { useState, useEffect } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Clock, Wallet, CheckCircle, Loader2, ArrowLeft } from 'lucide-react';
import api from '../../../config/api';
import { useToast } from '../../../components/ui/Toast';

export default function MemberTimeSelectionScreen() {
  const { pcId } = useParams();
  const navigate = useNavigate();
  const toast = useToast();

  const [profile, setProfile] = useState(null);
  const [plans, setPlans] = useState([]);
  const [selectedPlan, setSelectedPlan] = useState(null);
  const [isStarting, setIsStarting] = useState(false);
  const [loadingPlans, setLoadingPlans] = useState(true);

  useEffect(() => {
    const storedProfile = JSON.parse(localStorage.getItem('memberProfile') || 'null');
    if (!storedProfile) {
      navigate(`/pc-overlay/${pcId}/login`);
      return;
    }
    setProfile(storedProfile);

    const fetchPlans = async () => {
      try {
        setLoadingPlans(true);
        const res = await api.get(`/public/pcs/${pcId}/plans`);
        if (res.data.success) {
          // Filter out postpaid for members if your business logic dictates, or keep them.
          // Let's keep all plans for now.
          setPlans(res.data.data);
          if (res.data.data.length > 0) {
            setSelectedPlan(res.data.data[0]);
          }
        }
      } catch (err) {
        console.error('Failed to fetch PC plans', err);
      } finally {
        setLoadingPlans(false);
      }
    };
    fetchPlans();
  }, [navigate, pcId]);

  // Logged in but not playing yet: the balance shown came from login. Re-read it every 15 s so a
  // top-up or admin edit at the counter shows here without logging out and back in.
  useEffect(() => {
    const token = localStorage.getItem('memberToken');
    if (!token) return;
    const refresh = async () => {
      if (document.visibilityState === 'hidden') return;
      try {
        // Plain fetch, not `api`: its 401 handling sends the page to the staff login, and an
        // expired member token on a background refresh must not take the gaming PC screen with it.
        const res = await fetch('/api/public/members/me', { headers: { Authorization: `Bearer ${token}` } });
        if (!res.ok) return;
        const fresh = (await res.json())?.data;
        if (!fresh) return;
        setProfile(p => {
          const next = { ...(p || {}), gamingBalance: fresh.gamingBalance, foodBalance: fresh.foodBalance };
          localStorage.setItem('memberProfile', JSON.stringify(next));
          return next;
        });
      } catch { /* keep what is shown; the start itself is checked by the server */ }
    };
    refresh();
    const timer = setInterval(refresh, 15000);
    return () => clearInterval(timer);
  }, []);

  const handleStartSession = async () => {
    if (!profile || !selectedPlan) return;
    
    if (profile.gamingBalance < selectedPlan.price) {
      toast.error('Insufficient gaming balance');
      return;
    }

    setIsStarting(true);
    const memberToken = localStorage.getItem('memberToken');

    try {
      const res = await api.post(
        '/sessions/start',
        {
          pcId: pcId,
          memberId: profile.memberId,
          customerName: profile.fullName,
          durationMinutes: selectedPlan.duration > 0 ? selectedPlan.duration : 0,
          packageName: selectedPlan.name,
          expectedAmount: selectedPlan.price,
        },
        {
          headers: {
            Authorization: `Bearer ${memberToken}`,
            // In a real scenario, we might need X-Branch-Id if the backend requires it. 
            // For now, the backend will resolve the branch from the pcId if possible.
          },
        }
      );

      if (res.data.success) {
        toast.success('Session started successfully!');
        navigate(`/pc-overlay/${pcId}/`);
      } else {
        toast.error(res.data.error || 'Failed to start session');
      }
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to start session');
    } finally {
      setIsStarting(false);
    }
  };

  if (!profile) return null;

  return (
    <div className="h-full flex flex-col p-6 overflow-y-auto">
      <div className="flex items-center gap-4 mb-6">
        <button 
          onClick={() => navigate(`/pc-overlay/${pcId}/`)}
          className="text-text-3 hover:text-text transition-colors"
        >
          <ArrowLeft className="w-5 h-5" />
        </button>
        <h2 className="font-heading text-xl font-bold text-text uppercase tracking-wider">Start Session</h2>
      </div>

      <div className="bg-bg-3 border border-border p-4 rounded-xl mb-6">
        <div className="flex items-center justify-between mb-2">
          <span className="text-text-3 font-heading uppercase tracking-widest text-xs font-bold">Member</span>
          <Wallet className="w-4 h-4 text-accent" />
        </div>
        <p className="font-bold text-text text-lg">{profile.fullName}</p>
        <p className="text-text-2 font-mono text-xs mb-4">{profile.memberNumber}</p>
        
        <div className="flex justify-between items-center bg-bg/50 p-3 rounded-lg border border-border/50">
          <span className="text-text-2 text-sm font-body">Gaming Balance</span>
          <span className="font-mono font-bold text-text text-lg">₹{profile.gamingBalance.toFixed(2)}</span>
        </div>
      </div>

      <div className="flex-1 flex flex-col">
        <h3 className="font-heading text-sm font-bold text-text-2 uppercase tracking-widest mb-4">Select Plan</h3>
        
        {loadingPlans ? (
          <div className="flex-1 flex items-center justify-center">
            <Loader2 className="w-8 h-8 animate-spin text-accent" />
          </div>
        ) : (
          <div className="grid grid-cols-2 gap-3 mb-6">
            {plans.map((plan) => (
              <button
                key={plan.id}
                onClick={() => setSelectedPlan(plan)}
                className={`p-3 rounded-lg border transition-all flex flex-col items-center justify-center gap-1 text-center ${
                  selectedPlan?.id === plan.id 
                    ? 'bg-accent/20 border-accent shadow-[0_0_10px_rgba(220,38,38,0.2)]' 
                    : 'bg-bg-3 border-border hover:border-text-3'
                }`}
              >
                <Clock className={`w-5 h-5 ${selectedPlan?.id === plan.id ? 'text-accent' : 'text-text-3'}`} />
                <span className={`font-mono text-sm font-bold ${selectedPlan?.id === plan.id ? 'text-text' : 'text-text-2'}`}>
                  {plan.name}
                </span>
                <span className="text-accent font-bold text-xs">
                  {plan.isPostpaid ? 'Pay as you go' : `₹${plan.price}`}
                </span>
              </button>
            ))}
          </div>
        )}

        <div className="mt-auto bg-bg-3 p-4 rounded-xl border border-border">
          <div className="flex justify-between items-center mb-4">
            <span className="text-text-2 text-sm font-body">Estimated Cost</span>
            <span className="font-mono font-bold text-text text-xl">
              {selectedPlan?.isPostpaid ? 'Pay at end' : `₹${selectedPlan?.price.toFixed(2)}`}
            </span>
          </div>

          <button
            onClick={handleStartSession}
            disabled={isStarting || !selectedPlan || (!selectedPlan.isPostpaid && profile.gamingBalance < selectedPlan.price)}
            className="w-full bg-accent hover:bg-accent-dark text-white font-semibold py-3 px-4 rounded-sm transition-all duration-200 flex items-center justify-center shadow-[0_0_15px_rgba(220,38,38,0.3)] disabled:opacity-50 disabled:cursor-not-allowed border border-accent/50 gap-2"
          >
            {isStarting ? (
              <Loader2 className="w-5 h-5 animate-spin" />
            ) : (
              <>
                <CheckCircle className="w-5 h-5" />
                <span className="font-heading text-sm tracking-wider uppercase font-bold">Start Now</span>
              </>
            )}
          </button>
          
          {selectedPlan && !selectedPlan.isPostpaid && profile.gamingBalance < selectedPlan.price && (
            <p className="text-neon-orange text-xs text-center mt-3 font-body">Insufficient balance for this plan.</p>
          )}
        </div>
      </div>
    </div>
  );
}
