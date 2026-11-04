// ═══════════════════════════════════════════════════════════
// Gaming Café ERP — Socket Context (SignalR)
// SOP §20: Real-Time Synchronization Engine
// Manages SignalR Hub connections and lifecycle
// ═══════════════════════════════════════════════════════════

import { createContext, useContext, useEffect, useRef, useState, useCallback } from 'react';
import * as signalR from '@microsoft/signalr';
import { useAuth } from './AuthContext';

const SocketContext = createContext(null);

// SignalR Hub endpoints mapped from .NET 8 backend
export const SIGNALR_HUBS = {
  NOTIFICATIONS: '/hubs/notifications',
  PC_STATUS: '/hubs/pc-status',
  SESSIONS: '/hubs/sessions',
  RESERVATIONS: '/hubs/reservations',
  FOOD_ORDERS: '/hubs/food-orders',
  BILLING: '/hubs/billing',
  CASH: '/hubs/cash',
  DASHBOARD: '/hubs/dashboard'
};

const HUB_BASE_URL = import.meta.env.VITE_API_URL
  ? import.meta.env.VITE_API_URL.replace('/api', '')
  : '';

// ── Reconnect for a machine that is expected to stay up for days ────────────
//
// This replaces `.withAutomaticReconnect([0, 2000, 5000, 10000, 30000])`.
//
// Passing an ARRAY to withAutomaticReconnect does not mean "back off like
// this, forever". It means "make exactly these attempts, then give up and
// never reconnect again". That is five tries over 47 seconds. Any outage
// longer than 47 seconds - a router reboot, the branch's line dropping, the
// server being redeployed - permanently killed every hub for the life of the
// page, with no error shown and no way back except somebody noticing and
// pressing F5.
//
// A counter PC is left running all day and a Head Office screen is left open
// all night. Neither has anybody watching for a dead socket. So: never stop
// trying. Quick retries first so a blip recovers instantly, then settle to
// every 30s so a branch that is off overnight is not hammering a dead line.
const foreverRetryPolicy = {
  nextRetryDelayInMilliseconds: (ctx) => {
    const ladder = [0, 2000, 5000, 10000, 30000];
    return ladder[Math.min(ctx.previousRetryCount, ladder.length - 1)];
  },
};

// Hubs that carry the live figures on the operational screens. The notifications
// hub alone used to decide whether the UI showed "LIVE", so a dead pc-status hub
// left a frozen PC grid sitting under a confident green LIVE badge - which is
// exactly what the Head Office screenshots showed.
const CRITICAL_HUBS = [
  SIGNALR_HUBS.NOTIFICATIONS,
  SIGNALR_HUBS.PC_STATUS,
  SIGNALR_HUBS.SESSIONS,
  SIGNALR_HUBS.BILLING,
];

export function SocketProvider({ children }) {
  const { isAuthenticated, logout, fetchCurrentUser } = useAuth();
  const [connected, setConnected] = useState(false);
  // Per-hub health, for a component that only cares about ONE hub rather than the
  // all-four "is everything on this screen live" badge. Without this, subscribing to
  // pc-status meant subscribing to the health of notifications, sessions and billing too -
  // a blip on any one of the other three silently tore down the pc-status listener along
  // with it, and it stayed torn down until every hub recovered together. A PC that was
  // shut down or started during that window never got its live push at all, not even late.
  const [hubStatus, setHubStatus] = useState({});
  const hubsRef = useRef({});

  // ── Connect to all hubs when authenticated ──
  useEffect(() => {
    if (!isAuthenticated) {
      // Disconnect all on logout
      Object.values(hubsRef.current).forEach(hub => hub?.stop());
      hubsRef.current = {};
      setConnected(false);
      setHubStatus({});
      return;
    }

    let isSubscribed = true;

    // Which hubs are currently up. `connected` is derived from this rather than
    // from the notifications hub alone, so the LIVE badge cannot stay green while
    // the hub feeding the screen you are looking at is dead.
    const hubHealth = {};
    const publishHealth = () => {
      if (!isSubscribed) return;
      setConnected(CRITICAL_HUBS.every((h) => hubHealth[h] === true));
      setHubStatus({ ...hubHealth });
    };

    const connectHubs = async () => {
      try {
        const hubs = Object.values(SIGNALR_HUBS);

        for (const hubPath of hubs) {
          if (!hubsRef.current[hubPath]) {
            const connection = new signalR.HubConnectionBuilder()
              .withUrl(`${HUB_BASE_URL}${hubPath}`, {
                // Read fresh on every connect AND every reconnect. This used to
                // close over a token captured once at mount, so after a refresh
                // every reconnect re-presented the expired one, failed auth, and
                // burned through the (then finite) retry budget - turning a
                // recoverable blip into a permanently dead hub.
                accessTokenFactory: () => localStorage.getItem('accessToken'),
              })
              .withAutomaticReconnect(foreverRetryPolicy)
              .configureLogging(signalR.LogLevel.Warning)
              .build();

            // Store connection
            hubsRef.current[hubPath] = connection;

            // Handle lifecycle events
            connection.onreconnecting(() => {
              hubHealth[hubPath] = false;
              publishHealth();
            });

            connection.onreconnected(() => {
              hubHealth[hubPath] = true;
              publishHealth();
            });

            connection.onclose(() => {
              hubHealth[hubPath] = false;
              publishHealth();
            });

            // Store the start promise so we can await it during cleanup
            connection.startPromise = connection.start().then(() => {
              hubHealth[hubPath] = true;
              publishHealth();
            }).catch(err => {
              hubHealth[hubPath] = false;
              publishHealth();
              if (err.name !== 'AbortError') console.error('SignalR start error:', err);

              // withAutomaticReconnect only covers a connection that was once
              // established; it does nothing for a start() that never succeeded.
              // Without this a hub that was down at page load stayed down forever.
              if (isSubscribed && err.name !== 'AbortError') {
                setTimeout(function retryStart() {
                  const conn = hubsRef.current[hubPath];
                  if (!isSubscribed || !conn) return;
                  if (conn.state !== signalR.HubConnectionState.Disconnected) return;
                  conn.start()
                    .then(() => { hubHealth[hubPath] = true; publishHealth(); })
                    .catch(() => { if (isSubscribed) setTimeout(retryStart, 15000); });
                }, 5000);
              }
            });
          }
        }

        // Handle forced logout (SOP §11: Live Access Revocation)
        const notifHub = hubsRef.current[SIGNALR_HUBS.NOTIFICATIONS];
        if (notifHub) {
          notifHub.on('ForceLogout', (reason) => {
            console.warn('Forced logout received:', reason);
            logout();
            window.location.href = '/?reason=forced_logout';
          });
          // Any screen holding a member re-reads its balance - see useMemberBalanceUpdates.
          notifHub.on('MemberBalanceUpdated', (envelope) => {
            const detail = envelope?.payload ?? envelope;
            window.dispatchEvent(new CustomEvent('member-balance-updated', { detail }));
          });
          notifHub.on('PermissionsUpdated', () => {
            console.log('Permissions updated by admin. Refreshing user profile...');
            fetchCurrentUser();
          });
        }
      } catch (err) {
        console.error('SignalR Connection Error:', err);
      }
    };

    connectHubs();

    return () => {
      isSubscribed = false;
      Object.values(hubsRef.current).forEach(hub => {
        if (hub.startPromise) {
          hub.startPromise.then(() => hub.stop().catch(console.error));
        } else {
          hub.stop().catch(console.error);
        }
      });
      hubsRef.current = {};
      setConnected(false);
    };
  }, [isAuthenticated, logout, fetchCurrentUser]);

  // ── Get a specific hub connection ──
  const getHub = useCallback((hubPath) => {
    return hubsRef.current[hubPath] || null;
  }, []);

  // ── Subscribe to a hub event ──
  const subscribe = useCallback((hubPath, eventName, handler) => {
    const hub = hubsRef.current[hubPath];
    if (hub) {
      hub.on(eventName, handler);
      return () => hub.off(eventName, handler);
    }
    // If not connected yet, we could queue it, but usually component mounts after connection
    return () => {};
  }, []);

  // ── Emit event to a hub ──
  const emit = useCallback(async (hubPath, methodName, ...args) => {
    const hub = hubsRef.current[hubPath];
    if (hub && hub.state === signalR.HubConnectionState.Connected) {
      return await hub.invoke(methodName, ...args);
    }
    throw new Error('Hub is not connected');
  }, []);

  // Whether one specific hub is up right now, independent of the other three. Use this to
  // gate a subscription that only needs its own hub healthy - `connected` is for the
  // all-four "everything on this screen is live" badge, not for deciding whether it is safe
  // to listen for one particular event.
  const isHubUp = useCallback((hubPath) => hubStatus[hubPath] === true, [hubStatus]);

  const value = {
    connected,
    hubStatus,
    isHubUp,
    getHub,
    subscribe,
    emit,
    SIGNALR_HUBS,
  };

  return (
    <SocketContext.Provider value={value}>
      {children}
    </SocketContext.Provider>
  );
}

export function useSocket() {
  const context = useContext(SocketContext);
  if (!context) {
    throw new Error('useSocket must be used within a SocketProvider');
  }
  return context;
}

export default SocketContext;
