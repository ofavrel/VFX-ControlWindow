/* ============================================================================
   V3 — BOLD : rethinks the experience. A persistent mini-transport pinned to
   the top (playback is never buried), a left category rail for instant jumps,
   filter chips (All / Favorites / Modified), and a pinned favorites tray of
   quick-access cards. Highest novelty while staying Unity-flat.
   ============================================================================ */
const MiniTransport = ({ st }) => {
  const scrub = (e) => {
    const r = e.currentTarget.getBoundingClientRect();
    const set = (cx) => st.setT(Math.max(0, Math.min(1, (cx - r.left) / r.width)));
    set(e.clientX);
    const mv = (ev) => set(ev.clientX), up = () => { window.removeEventListener('mousemove', mv); window.removeEventListener('mouseup', up); };
    window.addEventListener('mousemove', mv); window.addEventListener('mouseup', up);
  };
  const alive = Math.round(VFX_STATS.aliveParticles * (st.playing ? (0.85 + st.t * 0.3) : 0.6));
  return (
    <div className="vfx-sticky-transport">
      <button className={'vfx-tbtn primary' + (st.playing ? ' is-on' : '')} style={{ width: 26, height: 22, borderRadius: 3 }} onClick={() => st.setPlaying(p => !p)}><VIcon name={st.playing ? 'pause' : 'play'} size={13} /></button>
      <button className="vfx-tbtn" style={{ width: 24, height: 22, borderRadius: 3 }} title="Restart" onClick={() => st.setT(0)}><VIcon name="restart" size={12} /></button>
      <div className="vfx-mini-scrub" onMouseDown={scrub}>
        <span className="vfx-mini-fill" style={{ width: (st.t * 100) + '%' }} />
        <span className="vfx-mini-thumb" style={{ left: (st.t * 100) + '%' }} />
      </div>
      <span className="vfx-mini-time">{(st.t * st.duration).toFixed(2)}s</span>
      <span className="vfx-mini-time" style={{ color: '#7baefa' }}>{alive.toLocaleString()} live</span>
    </div>
  );
};

/* rich playback tab for version C — full transport + playback options + live info */
const CPlaybackTab = ({ st }) => {
  const [opts, setOpts] = useS({ playOnAwake: true, reset: true, fixedDelta: false, culling: 'always' });
  const [flash, setFlash] = useS(null);
  const set = (k, v) => setOpts(s => ({ ...s, [k]: v }));
  const fire = (e) => { setFlash(e); setTimeout(() => setFlash(null), 200); };
  return (
    <div className="vfx-rail-content">
      <Transport st={st} />

      <div className="vfx-section-h">Playback options</div>
      <div className="vfx-fold-body" style={{ paddingTop: 4 }}>
        <div className="vfx-prop"><span className="vfx-plabel">Loop</span><span className="vfx-pcontrol"><button type="button" className={'vfx-check' + (st.loop ? ' on' : '')} onClick={() => st.setLoop(l => !l)}>{st.loop ? <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3.4" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12l5 5L20 6"/></svg> : null}</button></span></div>
        <div className="vfx-prop"><span className="vfx-plabel">Play On Awake</span><span className="vfx-pcontrol"><button type="button" className={'vfx-check' + (opts.playOnAwake ? ' on' : '')} onClick={() => set('playOnAwake', !opts.playOnAwake)}>{opts.playOnAwake ? <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3.4" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12l5 5L20 6"/></svg> : null}</button></span></div>
        <div className="vfx-prop"><span className="vfx-plabel">Reset On Play</span><span className="vfx-pcontrol"><button type="button" className={'vfx-check' + (opts.reset ? ' on' : '')} onClick={() => set('reset', !opts.reset)}>{opts.reset ? <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3.4" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12l5 5L20 6"/></svg> : null}</button></span></div>
        <div className="vfx-prop"><span className="vfx-plabel">Playback Rate</span><span className="vfx-pcontrol"><PropControl p={{ type: 'float', min: 0, max: 2, unit: '×' }} value={st.rate} onChange={(v) => st.setRate(v)} /></span></div>
        <div className="vfx-prop"><span className="vfx-plabel">Fixed Delta Time</span><span className="vfx-pcontrol"><button type="button" className={'vfx-check' + (opts.fixedDelta ? ' on' : '')} onClick={() => set('fixedDelta', !opts.fixedDelta)}>{opts.fixedDelta ? <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3.4" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12l5 5L20 6"/></svg> : null}</button></span></div>
        <div className="vfx-prop"><span className="vfx-plabel">Random Seed</span><span className="vfx-pcontrol"><div className="vfx-numfield" style={{ minWidth: 70 }}>{VFX_ASSET.seed}</div><button className="vfx-btn ghost" style={{ height: 18, padding: '0 8px' }}>Reseed</button></span></div>
      </div>

      <div className="vfx-section-h">Live info</div>
      <StatGrid st={st} />

      <div className="vfx-section-h">Systems</div>
      <div className="vfx-syslist">
        {VFX_ASSET.systems.map(s => (
          <div key={s.name} className="vfx-sys">
            <div className="vfx-sys-top"><span className="vfx-sys-name">{s.name}</span><span className="vfx-sys-num">{s.alive.toLocaleString()} / {s.cap.toLocaleString()}</span></div>
            <div className="vfx-sys-bar"><span style={{ width: (s.alive / s.cap * 100) + '%' }} /></div>
          </div>
        ))}
      </div>

      <div className="vfx-section-h">Send event</div>
      <div className="vfx-events">
        <div className="vfx-event-chips">
          {VFX_EVENTS.map(ev => (
            <button key={ev} className={'vfx-chip' + (flash === ev ? ' flash' : '')} onClick={() => fire(ev)}>
              <span className="vfx-chip-ico"><VIcon name="bolt" size={11} /></span>{ev}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
};

/* collapsible pinned favorites tray */
const PinnedTray = ({ favProps, st }) => {
  const [open, setOpen] = useS(true);
  return (
    <div className="vfx-pinned">
      <div className="vfx-fav-head" style={{ padding: 0, cursor: 'default', userSelect: 'none' }} onClick={() => setOpen(o => !o)}>
        <span className="vfx-tw" style={{ width: 9, fontSize: 9, opacity: 0.75 }}>{open ? '▾' : '▸'}</span>
        <VIcon name="star-fill" size={12} /> Pinned
        <span className="vfx-pin-n">{favProps.length}</span>
        <span className="vfx-fav-line" />
      </div>
      {open ? (
        <div className="vfx-pinned-grid">
          {favProps.map(p => {
            const c = VFX_CATEGORIES.find(x => x.id === p.cat);
            return (
              <div key={p.id} className="vfx-pincard">
                <div className="vfx-pincard-top">
                  <span className="vfx-pincard-dot" style={{ background: c.dot }} />
                  <span className="vfx-pincard-label" title={p.label}>{p.label}</span>
                  <span className="vfx-pincard-unpin" title="Unpin" onClick={(e) => { e.stopPropagation(); st.toggleFav(p.id); }}><VIcon name="star-fill" size={11} /></span>
                </div>
                <PropControl p={p} value={st.vals[p.id]} onChange={(v) => st.setVal(p.id, v)} />
              </div>
            );
          })}
        </div>
      ) : null}
    </div>
  );
};

/* collapsible category group */
const BoldGroup = ({ g, st, forceOpen }) => {
  const [open, setOpen] = useS(true);
  const isOpen = forceOpen || open;
  return (
    <div className="vfx-bold-group">
      <div className="vfx-bold-group-h" onClick={() => setOpen(o => !o)}>
        <span className="vfx-tw">{isOpen ? '▾' : '▸'}</span>
        <span className="vfx-dot" style={{ background: g.cat.dot }} />
        <span className="vfx-bold-group-title">{g.cat.label}</span>
        <span className="vfx-bold-group-count">{g.props.length}</span>
      </div>
      {isOpen ? <div className="vfx-bold-rows">{g.props.map(p => <PropRow key={p.id} p={p} st={st} />)}</div> : null}
    </div>
  );
};

function InspectorBold({ propStyle = 'rows', density = 'comfortable' }) {
  const st = useVfxState();
  const [q, setQ] = useS('');
  const [cat, setCat] = useS('all');
  const [chip, setChip] = useS('all');
  const [tab, setTab] = useS('props');
  const ql = q.trim().toLowerCase();

  // wheel-to-horizontal-scroll for the category rail
  const railRef = useR(null);
  useE(() => {
    const el = railRef.current;
    if (!el) return;
    const onWheel = (e) => {
      if (el.scrollWidth <= el.clientWidth) return;
      const d = Math.abs(e.deltaX) > Math.abs(e.deltaY) ? e.deltaX : e.deltaY;
      if (!d) return;
      el.scrollLeft += d;
      e.preventDefault();
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [tab]);

  const favCount = VFX_PROPS.filter(p => st.favs.has(p.id)).length;
  const visible = (p) =>
    (!ql || p.label.toLowerCase().includes(ql)) &&
    (cat === 'all' || p.cat === cat) &&
    (chip === 'all' || (chip === 'fav' ? st.favs.has(p.id) : st.isModified(p.id)));

  const shown = VFX_PROPS.filter(visible);
  const groups = VFX_CATEGORIES.map(c => ({ cat: c, props: shown.filter(p => p.cat === c.id) })).filter(g => g.props.length);
  const showTray = cat === 'all' && chip === 'all' && !ql && favCount > 0;
  const favProps = VFX_PROPS.filter(p => st.favs.has(p.id));

  return (
    <div className={'vfx-inspector pl-' + propStyle} data-density={density}>
      <div style={{ flex: 'none' }}><VfxCompHead /></div>
      <MiniTransport st={st} />

      {/* recessed gap separating the asset/playback section from the tabs section */}
      <div className="vfx-section-gap" />

      {/* tabs begin the second section */}
      <div className="vfx-tabs">
        <button className={'vfx-tab' + (tab === 'props' ? ' active' : '')} onClick={() => setTab('props')}>Properties <span className="vfx-tabcount">{VFX_PROPS.length}</span></button>
        <button className={'vfx-tab' + (tab === 'play' ? ' active' : '')} onClick={() => setTab('play')}>Playback</button>
        <button className={'vfx-tab' + (tab === 'debug' ? ' active' : '')} onClick={() => setTab('debug')}>Debug</button>
      </div>

      {tab === 'props' ? (
        <React.Fragment>
          <div className="vfx-subbar" style={{ flexWrap: 'wrap', gap: 7 }}>
            <VSearch value={q} onChange={setQ} />
            <div className="vfx-filterchips">
              <button className={'vfx-fchip' + (chip === 'all' ? ' active' : '')} onClick={() => setChip('all')}>All <span className="vfx-fchip-n">{VFX_PROPS.length}</span></button>
              <button className={'vfx-fchip' + (chip === 'fav' ? ' active' : '')} onClick={() => setChip('fav')}><VIcon name="star-fill" size={10} /> {favCount}</button>
              <button className={'vfx-fchip' + (chip === 'mod' ? ' active' : '')} onClick={() => setChip('mod')}>Modified <span className="vfx-fchip-n">{st.modifiedCount}</span></button>
            </div>
          </div>

          {/* horizontal category quick-nav rail — vertical wheel scrolls it horizontally */}
          <div className="vfx-hrail" ref={railRef}>
            <button className={'vfx-hrail-btn' + (cat === 'all' ? ' active' : '')} onClick={() => setCat('all')}>
              <VIcon name="list" size={13} /> All
            </button>
            {VFX_CATEGORIES.map(c => {
              const n = VFX_PROPS.filter(p => p.cat === c.id).length;
              return (
                <button key={c.id} className={'vfx-hrail-btn' + (cat === c.id ? ' active' : '')} title={c.label} onClick={() => setCat(cat === c.id ? 'all' : c.id)}>
                  <span className="vfx-rail-dot" style={{ background: c.dot }} />{c.label}
                </button>
              );
            })}
          </div>

          <div className="vfx-bold-main">
            <div className="vfx-rail-content">
              {/* pinned favorites tray */}
              {showTray ? <PinnedTray favProps={favProps} st={st} /> : null}

              {/* grouped property rows */}
              {groups.map(g => (
                <BoldGroup key={g.cat.id} g={g} st={st} forceOpen={!!ql} />
              ))}

              {!groups.length ? (
                <div className="vfx-empty">{chip === 'mod' ? 'Nothing edited yet — all properties match the graph defaults.' : chip === 'fav' ? 'No pinned properties. Hover a row and tap ★ to pin it here.' : `No properties match “${q}”.`}</div>
              ) : null}
            </div>
          </div>
        </React.Fragment>
      ) : null}

      {tab === 'play' ? <div className="vfx-bold-main"><CPlaybackTab st={st} /></div> : null}
      {tab === 'debug' ? <div className="vfx-bold-main"><div className="vfx-rail-content"><DebugTab st={st} /></div></div> : null}

      <div className="vfx-footer">
        <span className="vfx-foot-note">{st.modifiedCount ? st.modifiedCount + ' edited' : 'No overrides'} · seed {VFX_ASSET.seed}</span>
        <button className="vfx-btn ghost" disabled={!st.modifiedCount} onClick={st.resetAll}>Reset all</button>
        <button className="vfx-btn">Save preset</button>
      </div>
    </div>
  );
}

Object.assign(window, { InspectorBold });
