/* ============================================================================
   VFX inspector atoms — Unity Editor styled controls + icons.
   Pure presentational; state lives in the inspector components.
   Exported to window for the other Babel scripts.
   ============================================================================ */
const { useState: _useState } = React;

/* ---- 16px monochrome glyphs in Unity's flat style ---- */
const VIcon = ({ name, size = 16, color = 'currentColor' }) => {
  const p = { width: size, height: size, viewBox: '0 0 24 24', fill: 'none',
              stroke: color, strokeWidth: 1.7, strokeLinecap: 'round', strokeLinejoin: 'round' };
  const f = { width: size, height: size, viewBox: '0 0 24 24', fill: color };
  switch (name) {
    case 'search':   return <svg {...p}><circle cx="11" cy="11" r="6"/><path d="M20 20l-4-4"/></svg>;
    case 'star':     return <svg {...p}><path d="M12 3.5l2.6 5.3 5.9.8-4.3 4.1 1 5.8L12 16.8 6.8 19.5l1-5.8L3.5 9.6l5.9-.8z"/></svg>;
    case 'star-fill':return <svg {...f}><path d="M12 3.5l2.6 5.3 5.9.8-4.3 4.1 1 5.8L12 16.8 6.8 19.5l1-5.8L3.5 9.6l5.9-.8z"/></svg>;
    case 'reset':    return <svg {...p}><path d="M4 12a8 8 0 1 1 2.5 5.8"/><path d="M4 19v-4h4"/></svg>;
    case 'play':     return <svg {...f}><path d="M8 5v14l11-7z"/></svg>;
    case 'pause':    return <svg {...f}><rect x="6" y="5" width="4" height="14" rx="1"/><rect x="14" y="5" width="4" height="14" rx="1"/></svg>;
    case 'step':     return <svg {...f}><path d="M6 5v14l9-7z"/><rect x="16" y="5" width="3" height="14" rx="1"/></svg>;
    case 'step-back':return <svg {...f}><path d="M18 5v14l-9-7z"/><rect x="5" y="5" width="3" height="14" rx="1"/></svg>;
    case 'restart':  return <svg {...p}><path d="M4 12a8 8 0 1 1 2.5 5.8"/><path d="M4 19v-4h4"/></svg>;
    case 'stop':     return <svg {...f}><rect x="6" y="6" width="12" height="12" rx="1.5"/></svg>;
    case 'loop':     return <svg {...p}><path d="M4 9a5 5 0 0 1 5-5h7l-2-2m2 2-2 2"/><path d="M20 15a5 5 0 0 1-5 5H8l2 2m-2-2 2-2"/></svg>;
    case 'gear':     return <svg {...p}><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M5 5l2 2M17 17l2 2M19 5l-2 2M7 17l-2 2"/></svg>;
    case 'more':     return <svg {...f}><circle cx="5" cy="12" r="1.7"/><circle cx="12" cy="12" r="1.7"/><circle cx="19" cy="12" r="1.7"/></svg>;
    case 'chev-down':return <svg {...p}><path d="M6 9l6 6 6-6"/></svg>;
    case 'chev-right':return <svg {...p}><path d="M9 6l6 6-6 6"/></svg>;
    case 'lock':     return <svg {...p}><rect x="5" y="11" width="14" height="9" rx="1.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>;
    case 'bolt':     return <svg {...f}><path d="M13 2L4 14h6l-1 8 9-12h-6z"/></svg>;
    case 'eye':      return <svg {...p}><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="2.6"/></svg>;
    case 'grid':     return <svg {...p}><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/></svg>;
    case 'info':     return <svg {...f}><circle cx="12" cy="12" r="9"/><path d="M12 11v6" stroke="#383838" strokeWidth="2.2" strokeLinecap="round"/><circle cx="12" cy="7.5" r="1.2" fill="#383838"/></svg>;
    case 'warn':     return <svg {...f}><path d="M12 3l9.5 17H2.5z"/><path d="M12 9v5" stroke="#383838" strokeWidth="2" strokeLinecap="round"/><circle cx="12" cy="17" r="1.1" fill="#383838"/></svg>;
    case 'filter':   return <svg {...p}><path d="M3 5h18l-7 8v6l-4 2v-8z"/></svg>;
    case 'x':        return <svg {...p}><path d="M6 6l12 12M18 6L6 18"/></svg>;
    case 'cube':     return <svg {...p}><path d="M12 2l8 4.5v9L12 20 4 15.5v-9z"/><path d="M12 2v18M4 6.5l8 4.5 8-4.5"/></svg>;
    case 'material': return <svg {...p}><circle cx="12" cy="12" r="8"/><path d="M12 4a8 8 0 0 0 0 16"/></svg>;
    case 'sparkle':  return <svg {...f}><path d="M12 2l1.8 6.2L20 10l-6.2 1.8L12 18l-1.8-6.2L4 10l6.2-1.8z"/><circle cx="18.5" cy="4.5" r="1.4"/><circle cx="5" cy="17" r="1.2"/></svg>;
    case 'chart':    return <svg {...p}><path d="M4 20V6M4 20h16M8 20v-6M12 20v-9M16 20v-4M20 20V9"/></svg>;
    case 'list':     return <svg {...p}><path d="M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01"/></svg>;
    case 'pin':      return <svg {...p}><path d="M9 3h6l-1 6 3 3v2h-5v5l-1 2-1-2v-5H5v-2l3-3z"/></svg>;
    default: return null;
  }
};

/* ---- icon button (toolbar / row affordances) ---- */
const IconBtn = ({ name, size = 13, title, active, on, onClick, className = '' }) => (
  <button type="button" title={title}
    className={'vfx-iconbtn' + (active ? ' is-active' : '') + (on ? ' is-on' : '') + (className ? ' ' + className : '')}
    onClick={onClick}>
    <VIcon name={name} size={size} />
  </button>
);

/* ---- search input ---- */
const VSearch = ({ value, onChange, placeholder = 'Search properties…' }) => (
  <div className="vfx-search">
    <VIcon name="search" size={12} />
    <input value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} spellCheck={false} />
    {value ? <button type="button" className="vfx-search-x" onClick={() => onChange('')}><VIcon name="x" size={11} /></button> : null}
  </div>
);

/* ---- the value control for a property, by type ---- */
const PropControl = ({ p, value, onChange }) => {
  switch (p.type) {
    case 'float':
    case 'int': {
      const min = p.min ?? 0, max = p.max ?? 1;
      const pct = Math.max(0, Math.min(100, ((value - min) / (max - min)) * 100));
      const fmt = p.type === 'int' ? Math.round(value) : (Math.round(value * 100) / 100);
      return (
        <div className="vfx-slider">
          <div className="vfx-slider-track" onMouseDown={(e) => {
            const r = e.currentTarget.getBoundingClientRect();
            const set = (cx) => { const t = Math.max(0, Math.min(1, (cx - r.left) / r.width)); let v = min + t * (max - min); if (p.type === 'int') v = Math.round(v); onChange(Math.round(v * 1000) / 1000); };
            set(e.clientX);
            const mv = (ev) => set(ev.clientX); const up = () => { window.removeEventListener('mousemove', mv); window.removeEventListener('mouseup', up); };
            window.addEventListener('mousemove', mv); window.addEventListener('mouseup', up);
          }}>
            <span className="vfx-slider-fill" style={{ width: pct + '%' }} />
            <span className="vfx-slider-thumb" style={{ left: pct + '%' }} />
          </div>
          <div className="vfx-numfield">{fmt}{p.unit ? <span className="vfx-unit">{p.unit}</span> : null}</div>
        </div>
      );
    }
    case 'bool':
      return <button type="button" className={'vfx-check' + (value ? ' on' : '')} onClick={() => onChange(!value)}>{value ? <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3.4" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12l5 5L20 6"/></svg> : null}</button>;
    case 'color':
      return <div className="vfx-colorfield"><span className="vfx-swatch" style={{ background: value }} /><span className="vfx-hex">{value.toUpperCase()}</span><span className="vfx-chev">▾</span></div>;
    case 'gradient':
      return <div className="vfx-gradient" style={{ background: `linear-gradient(90deg, ${value.join(',')})` }}><span className="vfx-chev light">▾</span></div>;
    case 'vector3':
      return (
        <div className="vfx-vec">
          {['X','Y','Z'].map((ax, i) => (
            <label key={ax} className="vfx-axis"><span>{ax}</span><span className="vfx-numfield mini">{value[i]}</span></label>
          ))}
        </div>
      );
    case 'curve':
      return (
        <div className="vfx-curve">
          <svg viewBox="0 0 80 24" preserveAspectRatio="none"><path d="M2 22 C 24 22, 40 4, 78 3" fill="none" stroke="#7baefa" strokeWidth="1.6"/></svg>
        </div>
      );
    case 'object':
      return <div className="vfx-objfield"><span className="vfx-obj-ico"><VIcon name={p.icon || 'material'} size={13} /></span><span className="vfx-obj-name">{value}</span><span className="vfx-obj-btn"><VIcon name="more" size={12} /></span></div>;
    case 'enum':
      return <div className="vfx-dropdown"><span>{value}</span><span className="vfx-chev">▾</span></div>;
    default:
      return <div className="vfx-numfield">{String(value)}</div>;
  }
};

Object.assign(window, { VIcon, IconBtn, VSearch, PropControl });
