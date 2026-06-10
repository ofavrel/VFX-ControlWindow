/* ============================================================================
   Shared VFX inspector logic — state hook + reusable PropRow / Transport /
   Stats used by all three variations.
   ============================================================================ */
const { useState: useS, useEffect: useE, useRef: useR, useMemo: useM } = React;

/* per-inspector state: prop values, favorites, playback */
function useVfxState() {
  const [vals, setVals] = useS(() => Object.fromEntries(VFX_PROPS.map(p => [p.id, p.value])));
  const [favs, setFavs] = useS(() => new Set(VFX_PROPS.filter(p => p.fav).map(p => p.id)));
  const [playing, setPlaying] = useS(true);
  const [focused, setFocused] = useS(null);
  const [rate, setRate] = useS(1);
  const [t, setT] = useS(0.42); // normalized playhead 0..1
  const [loop, setLoop] = useS(true);

  const duration = 2.4;
  useE(() => {
    if (!playing) return;
    let raf, last = performance.now();
    const tick = (now) => {
      const dt = (now - last) / 1000; last = now;
      setT(prev => {
        let nx = prev + (dt * rate) / duration;
        if (nx > 1) nx = loop ? nx - 1 : 1;
        return nx;
      });
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [playing, rate, loop]);

  const setVal = (id, v) => setVals(s => ({ ...s, [id]: v }));
  const resetVal = (id) => { const p = VFX_PROPS.find(x => x.id === id); setVals(s => ({ ...s, [id]: p.def })); };
  const resetAll = () => setVals(Object.fromEntries(VFX_PROPS.map(p => [p.id, p.def])));
  const toggleFav = (id) => setFavs(s => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });
  const isModified = (id) => { const p = VFX_PROPS.find(x => x.id === id); return JSON.stringify(vals[id]) !== JSON.stringify(p.def); };
  const modifiedCount = VFX_PROPS.filter(p => isModified(p.id)).length;

  return { vals, setVal, resetVal, resetAll, favs, toggleFav, isModified, modifiedCount,
           focused, setFocused,
           playing, setPlaying, rate, setRate, t, setT, loop, setLoop, duration };
}

/* one property row — visual differs only via the .pl-* parent class */
const PropRow = ({ p, st, showFav = true }) => {
  const modified = st.isModified(p.id);
  const fav = st.favs.has(p.id);
  return (
    <div className={'vfx-prop' + (modified ? ' modified' : '') + (fav ? ' fav' : '') + (st.focused === p.id ? ' is-focused' : '')}
      onMouseDown={() => st.setFocused && st.setFocused(p.id)}>
      <span className="vfx-plabel" title={p.label}>
        {p.label}
      </span>
      <span className="vfx-pcontrol">
        <PropControl p={p} value={st.vals[p.id]} onChange={(v) => st.setVal(p.id, v)} />
      </span>
      <span className="vfx-prow-tools">
        <IconBtn name="reset" size={12} title="Reset to graph default"
          onClick={() => st.resetVal(p.id)} className={modified ? '' : 'vfx-dim'} />
        {showFav ? <IconBtn name={fav ? 'star-fill' : 'star'} size={12}
          title={fav ? 'Unpin' : 'Pin to favorites'} on={fav} onClick={() => st.toggleFav(p.id)} /> : null}
      </span>
    </div>
  );
};

/* the asset / component header shared by all variants */
const VfxCompHead = () => (
  <>
    <div className="vfx-comp-head">
      <span className="vfx-tw">▾</span>
      <span className="vfx-ci"><VIcon name="sparkle" size={15} /></span>
      <span className="vfx-title">Visual Effect</span>
      <span className="vfx-head-gear"><VIcon name="more" size={14} /></span>
    </div>
    <div className="vfx-meta">
      <div className="vfx-meta-row">
        <span className="vfx-mlabel">Asset Template</span>
        <div className="vfx-objfield">
          <span className="vfx-obj-ico"><VIcon name="sparkle" size={13} /></span>
          <span className="vfx-obj-name">{VFX_ASSET.file}</span>
          <span className="vfx-obj-btn"><VIcon name="more" size={12} /></span>
        </div>
      </div>
      <div className="vfx-meta-row">
        <span className="vfx-mlabel">Initial Event</span>
        <div className="vfx-dropdown"><span>{VFX_ASSET.initialEvent}</span><span className="vfx-chev">▾</span></div>
      </div>
    </div>
  </>
);

/* full transport block (used in V2 Playback tab) */
const Transport = ({ st }) => {
  const scrub = (e) => {
    const r = e.currentTarget.getBoundingClientRect();
    const set = (cx) => st.setT(Math.max(0, Math.min(1, (cx - r.left) / r.width)));
    set(e.clientX);
    const mv = (ev) => set(ev.clientX), up = () => { window.removeEventListener('mousemove', mv); window.removeEventListener('mouseup', up); };
    window.addEventListener('mousemove', mv); window.addEventListener('mouseup', up);
  };
  const cur = (st.t * st.duration).toFixed(2);
  return (
    <div className="vfx-transport">
      <div className="vfx-timeline" onMouseDown={scrub}>
        <div className="vfx-timeline-ticks">{Array.from({ length: 12 }).map((_, i) => <span key={i} className="vfx-timeline-tick" />)}</div>
        <div className="vfx-timeline-fill" style={{ width: (st.t * 100) + '%' }} />
        <div className="vfx-playhead" style={{ left: (st.t * 100) + '%' }} />
      </div>
      <div className="vfx-time-readout"><span>{cur}s</span><span>{st.duration.toFixed(2)}s</span></div>
      <div className="vfx-transport-row">
        <div className="vfx-transport-btns">
          <button className="vfx-tbtn" title="Restart" onClick={() => st.setT(0)}><VIcon name="restart" size={14} /></button>
          <button className="vfx-tbtn" title="Step back" onClick={() => st.setT(Math.max(0, st.t - 1 / st.duration / 30))}><VIcon name="step-back" size={14} /></button>
          <button className={'vfx-tbtn primary' + (st.playing ? ' is-on' : '')} title={st.playing ? 'Pause' : 'Play'} onClick={() => st.setPlaying(p => !p)}><VIcon name={st.playing ? 'pause' : 'play'} size={14} /></button>
          <button className="vfx-tbtn" title="Step forward" onClick={() => st.setT(Math.min(1, st.t + 1 / st.duration / 30))}><VIcon name="step" size={14} /></button>
          <button className={'vfx-tbtn' + (st.loop ? ' is-on' : '')} title="Loop" onClick={() => st.setLoop(l => !l)}><VIcon name="loop" size={14} /></button>
        </div>
        <div className="vfx-rate">
          <span className="vfx-rate-label">Rate</span>
          <PropControl p={{ type: 'float', min: 0, max: 2, unit: '×' }} value={st.rate} onChange={(v) => st.setRate(v)} />
        </div>
      </div>
    </div>
  );
};

/* live stat grid */
const StatGrid = ({ st }) => {
  const alive = Math.round(VFX_STATS.aliveParticles * (st.playing ? (0.85 + st.t * 0.3) : 0.6));
  return (
    <div className="vfx-stat-grid">
      <div className="vfx-stat"><span className="vfx-stat-k">Alive particles</span><span className="vfx-stat-v">{alive.toLocaleString()}<span className="u">/ {VFX_STATS.capacity.toLocaleString()}</span></span></div>
      <div className="vfx-stat"><span className="vfx-stat-k">Spawn rate</span><span className="vfx-stat-v">{Math.round(st.vals.spawnRate)}<span className="u">/s</span></span></div>
      <div className="vfx-stat"><span className="vfx-stat-k">GPU time</span><span className="vfx-stat-v">{VFX_STATS.gpuTimeMs.toFixed(2)}<span className="u">ms</span></span></div>
      <div className="vfx-stat"><span className="vfx-stat-k">Draw calls</span><span className="vfx-stat-v">{VFX_STATS.drawCalls}</span></div>
      <div className="vfx-stat"><span className="vfx-stat-k">Texture mem</span><span className="vfx-stat-v">{VFX_STATS.textureMem}</span></div>
      <div className="vfx-stat"><span className="vfx-stat-k">Bounds</span><span className="vfx-stat-v" style={{ fontSize: 11 }}>{VFX_STATS.bounds.join(' × ')}</span></div>
    </div>
  );
};

Object.assign(window, { useVfxState, PropRow, VfxCompHead, Transport, StatGrid });
