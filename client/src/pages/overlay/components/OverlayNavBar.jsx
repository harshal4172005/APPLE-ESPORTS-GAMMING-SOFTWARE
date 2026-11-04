import React from 'react';
import { NavLink } from 'react-router-dom';
import { Home, Coffee, Clock, PhoneCall, Receipt } from 'lucide-react';
import { useOverlaySocket } from '../../../contexts/OverlaySocketContext';

export default function OverlayNavBar() {
  const { emitActivity, pcId, sessionData } = useOverlaySocket();

  const handleNavClick = (tabName) => {
    emitActivity(`nav_clicked_${tabName}`);
  };

  // Extend only makes sense for a fixed-duration session with a slot to extend —
  // Pay-As-You-Go already bills continuously for real time with no cap.
  const isPayAsYouGo = !sessionData?.plannedDurationMin;

  const navItems = [
    { path: '', label: 'Home', icon: <Home className="w-5 h-5" />, name: 'home' },
    { path: 'food', label: 'Food', icon: <Coffee className="w-5 h-5" />, name: 'food' },
    ...(isPayAsYouGo ? [] : [{ path: 'extend', label: 'Time', icon: <Clock className="w-5 h-5" />, name: 'extend' }]),
    { path: 'call', label: 'Call', icon: <PhoneCall className="w-5 h-5" />, name: 'call' },
    { path: 'bill', label: 'Bill', icon: <Receipt className="w-5 h-5" />, name: 'bill' },
  ];

  return (
    <div className="flex items-center justify-around h-full px-2">
      {navItems.map((item) => {
        // Construct the absolute path to prevent recursive appending
        const absolutePath = item.path ? `/pc-overlay/${pcId}/${item.path}` : `/pc-overlay/${pcId}`;
        
        return (
          <NavLink
            key={item.name}
            to={absolutePath}
            end={item.path === ''}
            onClick={() => handleNavClick(item.name)}
            className={({ isActive }) => `
              flex flex-col items-center justify-center w-16 h-14 rounded-lg transition-all duration-200
              ${isActive 
                ? 'text-accent bg-accent/10 shadow-[inset_0_-2px_0_rgba(220,38,38,1)]' 
                : 'text-text-3 hover:text-text-2 hover:bg-white/5'}
            `}
          >
            {item.icon}
            <span className="text-[10px] font-heading tracking-wider uppercase mt-1 font-bold">
              {item.label}
            </span>
          </NavLink>
        );
      })}
    </div>
  );
}
