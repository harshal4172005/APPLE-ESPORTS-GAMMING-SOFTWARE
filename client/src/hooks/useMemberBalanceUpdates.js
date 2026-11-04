import { useEffect, useRef } from 'react';

// Fired by SocketContext whenever the branch says a member's balance changed (top-up, bonus,
// admin edit, wallet payment). detail = { memberId, member } where member is the fresh profile.
export const MEMBER_BALANCE_EVENT = 'member-balance-updated';

export default function useMemberBalanceUpdates(handler) {
  const ref = useRef(handler);
  ref.current = handler;
  useEffect(() => {
    const listener = (e) => ref.current?.(e.detail || {});
    window.addEventListener(MEMBER_BALANCE_EVENT, listener);
    return () => window.removeEventListener(MEMBER_BALANCE_EVENT, listener);
  }, []);
}

// Copies a fresh member's balances onto a held member object, if it is the same member.
export const withFreshBalance = (held, { memberId, member }) =>
  held && member && (held.id === memberId || held.memberId === memberId)
    ? { ...held, gamingBalance: member.gamingBalance, foodBalance: member.foodBalance, walletBalance: member.walletBalance ?? held.walletBalance }
    : held;
