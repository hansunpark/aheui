// 아희 IDE 앱: 코드 공간(메인 + 떠 있는 보조 창), 실행, 팔레트, 클립보드, 버전 관리를 묶는다.
import { CodeDoc, cellKey, parseKey } from './doc.js';
import { CodeView } from './view.js';
import { host, pickFileInBrowser, downloadInBrowser } from './host.js';
import { SAMPLES } from './samples.js';

const A = globalThis.Aheui;
const $ = id => document.getElementById(id);
const SAVE_KEY = 'aheui-ide-workspace-v2';
const PEN_COLORS = [
  'rgba(255, 213, 0, 0.42)', 'rgba(0, 200, 83, 0.32)', 'rgba(41, 121, 255, 0.30)',
  'rgba(255, 64, 129, 0.32)', 'rgba(170, 0, 255, 0.28)', 'rgba(255, 109, 0, 0.36)',
];
const TOOLS = ['cursor', 'pen', 'bp', 'lasso', 'brush'];
const TX_LABELS = {
  type: '입력', delete: '지우기', pen: '형광펜', highlight: '형광펜', paste: '붙여넣기', palette: '팔레트 배치',
  breakpoint: '중단점', restore: '버전 복원', replace: '전체 바꾸기', edit: '편집', open: '파일 열기', new: '새로',
};

function el(tag, props = {}, ...children) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === 'class') e.className = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else if (k in e) e[k] = v;
    else e.setAttribute(k, v);
  }
  e.append(...children);
  return e;
}

/** 실행 기록을 가진 코드 공간 하나 */
class Session {
  constructor(app, { id, name, kind, doc, input = '', filePath = null, fileName = null }) {
    Object.assign(this, { app, id, name, kind, doc, input, filePath, fileName });
    this.machine = null;
    this.trace = new Map();
    this.native = null;
    this.view = new CodeView(app, this);
    this.frame = null;
    this.minimized = false;
    doc.onChange(() => app.docChanged(this));
  }

  ensureMachine() {
    if (!this.machine) {
      this.trace.clear();
      this.native = null;
      this.machine = new A.Machine(this.doc, this.input, {
        history: true,
        onMove: (fx, fy, tx, ty, dir, delta, steps) => this.recordMove(fx, fy, tx, ty, dir, delta, steps),
      });
    }
    return this.machine;
  }

  recordMove(fx, fy, tx, ty, dir, delta, steps) {
    const k = `${fx},${fy},${tx},${ty},${dir}`;
    let e = this.trace.get(k);
    if (!e) {
      if (delta < 0) return;
      e = { fx, fy, tx, ty, dir, count: 0, last: 0 };
      this.trace.set(k, e);
    }
    e.count += delta;
    if (delta > 0) e.last = steps;
    if (e.count <= 0) this.trace.delete(k);
  }

  resetMachine() {
    this.machine = null;
    this.trace.clear();
    this.view.invalidate();
  }

  get title() { return this.kind === 'main' ? '메인' : this.name; }
}

class App {
  constructor() {
    this.sessions = [];
    this.active = null;
    this.tool = 'cursor';
    this.penColor = PEN_COLORS[0];
    this.clips = [];
    this.palette = [];
    this.runner = null;
    this.z = 10;
    this.floatSeq = 1;
    this.clipSeq = 1;
    this.readTheme();
    matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
      this.readTheme();
      this.allViews().forEach(v => v.invalidate());
      this.renderClips();
    });
  }

  readTheme() {
    const cs = getComputedStyle(document.documentElement);
    const v = n => cs.getPropertyValue(n).trim();
    this.theme = {
      bg: v('--code-bg'), grid: v('--grid'), text: v('--text'), accent: v('--accent'),
      sel: v('--sel'), selLine: v('--sel-line'), exec: v('--exec'), halted: v('--halted'),
      arrow: v('--arrow'), arrowHalo: v('--arrow-halo'), bp: v('--bp'), diff: v('--diff'),
      placeBg: v('--place-bg'), conflictBg: v('--conflict-bg'), other: v('--c-other'), font: v('--code-font'),
      cat: {
        arith: v('--c-arith'), stack: v('--c-stack'), storage: v('--c-storage'),
        branch: v('--c-branch'), halt: v('--c-halt'), nop: v('--c-nop'),
      },
    };
  }

  allViews() { return [...this.sessions.map(s => s.view), ...this.palette.map(p => p.view)]; }

  // ───── 시작 ─────
  async init() {
    this.buildToolbar();
    this.bindPanels();
    if (!this.loadWorkspace()) {
      this.createMain(new CodeDoc(SAMPLES[0].code));
      this.addPaletteLine('방방다망희');
      this.addPaletteLine('');
    }
    this.setTool(this.tool);
    this.setActive(this.main);
    this.main.view.el.focus();
    document.addEventListener('keydown', e => this.onGlobalKey(e));
    window.addEventListener('beforeunload', () => this.saveWorkspace());
    // 다른 프로그램에서 복사한 한글 코드를 IDE 클립보드로 가져온다 (창으로 돌아올 때마다 확인)
    window.addEventListener('focus', () => this.syncSystemClipboard());
    document.addEventListener('visibilitychange', () => { if (!document.hidden) this.syncSystemClipboard(); });
    this.syncSystemClipboard();
    if (host.available) {
      try {
        const f = await host.call('startupFile');
        if (f) this.loadFileInto(this.main, f);
      } catch { /* 무시 */ }
    } else {
      $('st-state').title = '브라우저에서 열었습니다: 네이티브 실행과 파일 대화상자는 데스크톱 앱(aheui-ide.exe)에서만 됩니다';
    }
    this.updateTitle();
  }

  createMain(doc) {
    const s = new Session(this, { id: 'main', name: '메인', kind: 'main', doc });
    this.sessions.push(s);
    this.main = s;
    s.frame = $('main-space');
    s.frame.append(s.view.el);
    s.frame.addEventListener('pointerdown', () => this.setActive(s), true);
    return s;
  }

  // ───── 도구 막대 ─────
  buildToolbar() {
    document.querySelectorAll('[data-act]').forEach(b => b.addEventListener('click', () => this.act(b.dataset.act)));
    document.querySelectorAll('[data-tool]').forEach(b => b.addEventListener('click', () => this.setTool(b.dataset.tool)));
    const sw = $('pen-colors');
    PEN_COLORS.forEach(c => {
      const b = el('button', { class: 'swatch', title: '형광펜 색', onclick: () => { this.penColor = c; this.setTool('pen'); this.renderSwatches(); } });
      b.style.background = c;
      b.dataset.color = c;
      sw.append(b);
    });
    this.renderSwatches();
    const sel = $('sel-sample');
    SAMPLES.forEach((s, i) => sel.add(new Option(s.name, i)));
    sel.onchange = async () => {
      const sample = SAMPLES[sel.value];
      sel.value = '';
      if (!sample) return;
      const s = this.active;
      if (!s.doc.isEmpty() && !await this.confirmDiscard(s, '예제를 불러오면 지금 코드 공간 내용이 바뀝니다 (되돌리기 가능).')) return;
      s.doc.replaceAll({ text: sample.code }, 'replace');
      s.input = sample.input;
      s.filePath = null;
      s.fileName = null;
      s.resetMachine();
      this.renderRunPanel();
      this.updateTitle();
      s.view.el.focus();
    };
  }

  renderSwatches() {
    document.querySelectorAll('.swatch').forEach(b => b.classList.toggle('on', b.dataset.color === this.penColor));
  }

  setTool(t) {
    this.tool = t;
    document.body.dataset.tool = t;
    document.querySelectorAll('[data-tool]').forEach(b => b.classList.toggle('on', b.dataset.tool === t));
    this.scheduleSave();
  }

  act(name) {
    const s = this.active;
    switch (name) {
      case 'new': return this.newFile();
      case 'open': return this.openFile();
      case 'save': return this.saveFile(false);
      case 'saveAs': return this.saveFile(true);
      case 'copyText': return this.copyText();
      case 'openProject': return this.openProject();
      case 'saveProject': return this.saveProject();
      case 'run': return this.run(s);
      case 'pause': return this.pause();
      case 'step': return this.step(s);
      case 'stepBack': return this.stepBack(s);
      case 'reset': return this.reset(s);
      case 'native': return this.runNative(s);
      case 'newSpace': return this.newSubSpace();
      case 'undo': s.view.commitInPlace(); s.doc.undo(); return;
      case 'redo': s.view.commitInPlace(); s.doc.redo(); return;
      case 'commit': return this.commit();
    }
  }

  onGlobalKey(e) {
    const t = e.target;
    const inText = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT');
    const ctrl = e.ctrlKey || e.metaKey;
    let handled = true;
    if (e.key === 'F5' && e.shiftKey) this.reset(this.active);
    else if (e.key === 'F5') this.run(this.active);
    else if (e.key === 'F6') this.pause();
    else if (e.key === 'F10' && e.shiftKey) this.stepBack(this.active);
    else if (e.key === 'F10') this.step(this.active);
    else if (ctrl && e.key === 'Enter') this.runNative(this.active);
    else if (ctrl && e.shiftKey && e.code === 'KeyC' && !inText) this.copyText();
    else if (ctrl && e.code === 'KeyS') this.saveFile(e.shiftKey);
    else if (ctrl && e.code === 'KeyO') this.openFile();
    else if (e.altKey && !ctrl && /^Digit[1-5]$/.test(e.code) && !inText) this.setTool(TOOLS[+e.code.slice(5) - 1]);
    else if (ctrl && e.code === 'Backquote') this.togglePalettePanel();
    else if (e.key === 'Escape' && this.armedPalette) { this.disarmPalette(); this.status('팔레트 배치를 취소했습니다'); }
    else if (e.key === 'Escape' && this.modalOpen) handled = false;
    else handled = false;
    if (handled) e.preventDefault();
  }

  // ───── 활성 코드 공간 ─────
  setActiveView(view) {
    const s = this.sessions.find(x => x.view === view);
    if (s) this.setActive(s);
  }

  setActive(s) {
    if (!s) return;
    if (this.active !== s) {
      this.active = s;
      for (const x of this.sessions) if (x.frame) x.frame.classList.toggle('active', x === s);
      if (s.frame && s.kind === 'sub') s.frame.style.zIndex = ++this.z;
      this.renderRunPanel();
      this.renderVersions();
    }
  }

  docChanged(s) {
    if (s === this.active) this.renderVersions();
    if (s.machine && !this.runner) this.renderRunPanel();
    this.scheduleSave();
  }

  // ───── 보조 코드 공간 (떠 있는 창) ─────
  newSubSpace(opts = {}) {
    const n = this.floatSeq++;
    const s = new Session(this, {
      id: opts.id || `sub${Date.now().toString(36)}${n}`,
      name: opts.name || `보조 ${n}`,
      kind: 'sub',
      doc: opts.doc || new CodeDoc(),
      input: opts.input || '',
    });
    this.sessions.push(s);
    const ws = $('workspace').getBoundingClientRect();
    const g = opts.float || {
      x: 60 + (n % 6) * 34, y: 50 + (n % 6) * 30,
      w: Math.min(520, ws.width - 120), h: Math.min(340, ws.height - 120), min: false,
    };
    const nameInput = el('input', { class: 'name', value: s.name, title: '이름 바꾸기', spellcheck: false });
    nameInput.addEventListener('change', () => { s.name = nameInput.value || s.name; this.renderDock(); this.renderRunPanel(); this.scheduleSave(); });
    const frame = el('div', { class: 'float space-frame' },
      el('div', { class: 'space-title' },
        nameInput,
        el('span', { class: 'spacer' }),
        el('button', { title: '최소화', onclick: () => this.minimize(s, true) }, '▁'),
        el('button', { title: '닫기', onclick: () => this.closeSub(s) }, '✕')),
      s.view.el);
    Object.assign(frame.style, { left: g.x + 'px', top: g.y + 'px', width: g.w + 'px', height: g.h + 'px', zIndex: ++this.z });
    s.frame = frame;
    $('float-layer').append(frame);
    frame.addEventListener('pointerdown', () => { this.setActive(s); frame.style.zIndex = ++this.z; }, true);
    this.makeMovable(frame, frame.querySelector('.space-title'));
    new ResizeObserver(() => this.scheduleSave()).observe(frame);
    if (g.min) this.minimize(s, true);
    if (!opts.silent) { this.setActive(s); s.view.el.focus(); }
    return s;
  }

  makeMovable(frame, handle) {
    handle.addEventListener('pointerdown', e => {
      if (e.target.closest('button, input')) return;
      e.preventDefault();
      const start = { x: e.clientX, y: e.clientY, l: frame.offsetLeft, t: frame.offsetTop };
      const move = ev => {
        const layer = frame.parentElement.getBoundingClientRect();
        const l = Math.min(Math.max(start.l + ev.clientX - start.x, -frame.offsetWidth + 80), layer.width - 80);
        const t = Math.min(Math.max(start.t + ev.clientY - start.y, 0), layer.height - 30);
        frame.style.left = l + 'px';
        frame.style.top = t + 'px';
      };
      const up = () => { window.removeEventListener('pointermove', move); this.scheduleSave(); };
      window.addEventListener('pointermove', move);
      window.addEventListener('pointerup', up, { once: true });
    });
  }

  minimize(s, min) {
    s.minimized = min;
    s.frame.hidden = min;
    if (min && this.active === s) { this.setActive(this.main); this.main.view.el.focus(); }
    if (!min) { this.setActive(s); s.frame.style.zIndex = ++this.z; s.view.el.focus(); s.view.invalidate(); }
    this.renderDock();
    this.scheduleSave();
  }

  renderDock() {
    const dock = $('dock');
    dock.replaceChildren(...this.sessions.filter(s => s.kind === 'sub' && s.minimized).map(s =>
      el('button', { title: '다시 띄우기', onclick: () => this.minimize(s, false) }, '▣ ' + s.name)));
  }

  async closeSub(s) {
    if (!s.doc.isEmpty()) {
      const ok = await this.confirm({
        title: `'${s.name}' 닫기`,
        text: '이 코드 공간의 내용과 버전 기록이 사라집니다.\n필요한 부분은 먼저 클립보드에 복사해 두세요.',
        buttons: [{ label: '닫기', value: true, primary: true }, { label: '취소', value: false }],
      });
      if (!ok) return;
    }
    if (this.runner && this.runner.session === s) this.pause();
    s.frame.remove();
    this.sessions = this.sessions.filter(x => x !== s);
    if (this.active === s) this.setActive(this.main);
    this.renderDock();
    this.scheduleSave();
  }

  // ───── 실행 ─────
  speedSettings() {
    const speed = +$('speed').value;
    const DELAYS = [600, 250, 100, 40, 12];
    const BATCH = [20, 300, 5000, 100000, Infinity];
    return speed <= 5 ? { batch: 1, delay: DELAYS[speed - 1] } : { batch: BATCH[speed - 6], delay: 0 };
  }

  run(s) {
    if (this.runner) { if (this.runner.session === s) return; this.pause(); }
    s.view.commitInPlace();
    const m = s.ensureMachine();
    if (m.halted) { this.status('프로그램이 끝났습니다. ⏹ 초기화(Shift+F5) 후 다시 실행하세요'); return; }
    const runner = { session: s, timer: 0, first: true };
    this.runner = runner;
    const tick = () => {
      if (this.runner !== runner) return;
      const { batch, delay } = this.speedSettings();
      const deadline = performance.now() + 25;
      let stop = false;
      for (let i = 0; i < batch; i++) {
        if (!runner.first && s.doc.hasBreakpoint(m.x, m.y)) { stop = true; this.status(`중단점 (${m.x}, ${m.y})`); break; }
        runner.first = false;
        if (!m.step()) { stop = true; break; }
        if ((i & 1023) === 1023 && performance.now() > deadline) break;
      }
      if (delay || stop) s.view.ensureVisible(m.x, m.y);
      s.view.invalidate();
      if (stop) this.pause();
      else runner.timer = setTimeout(tick, delay);
      this.renderRunPanel();
    };
    this.renderRunPanel();
    tick();
  }

  pause() {
    if (!this.runner) return;
    clearTimeout(this.runner.timer);
    this.runner = null;
    this.renderRunPanel();
  }

  step(s) {
    if (this.runner) this.pause();
    s.view.commitInPlace();
    const m = s.ensureMachine();
    m.step();
    s.view.ensureVisible(m.x, m.y);
    s.view.invalidate();
    this.renderRunPanel();
  }

  stepBack(s) {
    if (this.runner) this.pause();
    const m = s.machine;
    if (!m || !m.stepBack()) { this.status('되돌릴 실행 단계가 없습니다'); return; }
    s.view.ensureVisible(m.x, m.y);
    s.view.invalidate();
    this.renderRunPanel();
  }

  reset(s) {
    if (this.runner && this.runner.session === s) this.pause();
    s.native = null;
    s.resetMachine();
    this.renderRunPanel();
  }

  async runNative(s) {
    if (!host.available) { this.status('네이티브 실행은 데스크톱 앱(aheui-ide.exe)에서만 됩니다'); return; }
    if (this.runner) this.pause();
    s.view.commitInPlace();
    s.resetMachine();
    s.native = { running: true };
    this.renderRunPanel();
    try {
      s.native = await host.call('runNative', { code: s.doc.text(), input: s.input, timeoutMs: 30000 });
    } catch (e) {
      s.native = { error: e.message };
    }
    this.renderRunPanel();
  }

  storeName(i) {
    if (i === 0) return '(없음)';
    if (i === A.QUEUE) return 'ㅇ 큐';
    if (i === A.PIPE) return 'ㅎ 통로';
    return A.JONG[i];
  }

  renderRunPanel() {
    const s = this.active;
    if (!s) return;
    $('active-name').textContent = s.title;
    const m = s.machine;
    const st = $('st-state');
    st.className = '';
    const running = this.runner && this.runner.session === s;
    if (s.native) {
      const n = s.native;
      if (n.running) st.textContent = '네이티브 실행 중…';
      else if (n.error) { st.textContent = '네이티브 실행 실패: ' + n.error; st.className = 'error'; }
      else if (n.timedOut) { st.textContent = '네이티브: 30초 시간 초과'; st.className = 'error'; }
      else {
        const err = (n.stderr || '').split('\n').filter(l => l && !l.startsWith('[aheui]')).join(' ');
        st.textContent = `네이티브: 종료 코드 ${n.exitCode}, ${n.elapsedMs.toFixed(0)} ms` + (err ? ` — ${err}` : '');
        if (err) st.className = 'error';
      }
    } else if (!m) st.textContent = '준비';
    else if (m.error) { st.textContent = '오류: ' + m.error; st.className = 'error'; }
    else if (m.halted) st.textContent = `종료 (종료 코드 ${m.exitCode})`;
    else st.textContent = running ? '실행 중' : m.steps ? '일시정지' : '준비';
    $('st-steps').textContent = m ? m.steps.toLocaleString() : '0';
    $('st-pos').textContent = m ? `(${m.x}, ${m.y})` : '(0, 0)';
    $('st-dir').textContent = A.DIR_NAMES[m ? m.dir : A.DOWN];
    $('st-storage').textContent = this.storeName(m ? m.storage : 0);
    const frag = document.createDocumentFragment();
    if (m) {
      for (let i = 0; i < 28; i++) {
        const items = m.items(i);
        if (!items.length && i !== m.storage) continue;
        const shown = items.length > 200 ? ['…', ...items.slice(-200)] : items;
        frag.append(el('div', { class: 'store' + (i === m.storage ? ' current' : '') + (items.length ? '' : ' empty') },
          el('span', { class: 'name' }, this.storeName(i)),
          el('span', { class: 'vals' }, items.length ? shown.join(' ') + (i === A.QUEUE ? '  ← 뒤' : '  ← 위') : '비어 있음')));
      }
    }
    $('stores').replaceChildren(frag);
    if (document.activeElement !== $('input')) $('input').value = s.input;
    const out = $('output');
    out.textContent = s.native && !s.native.running ? (s.native.stdout || '') : m ? m.output : '';
    out.scrollTop = out.scrollHeight;
    const busy = !!this.runner;
    document.querySelector('[data-act=run]').disabled = running || !!(m && m.halted);
    document.querySelector('[data-act=run]').textContent = m && m.steps && !m.halted ? '▶ 계속' : '▶ 실행';
    document.querySelector('[data-act=pause]').disabled = !busy;
    document.querySelector('[data-act=stepBack]').disabled = running || !(m && m.canStepBack);
    document.querySelector('[data-act=native]').disabled = !host.available;
  }

  // ───── 상태 표시줄 ─────
  status(msg, sticky = false) {
    const e = $('status-msg');
    e.textContent = msg;
    e.classList.toggle('sticky', sticky);
    clearTimeout(this._statusTimer);
    if (msg && !sticky) this._statusTimer = setTimeout(() => { if (e.textContent === msg) e.textContent = ''; }, 6000);
  }

  showCellInfo(view, cell) {
    if (!view || !cell) { this.showCursor(this.active && this.active.view); return; }
    const cp = view.doc.get(cell.x, cell.y);
    $('status-cell').textContent = `(${cell.x}, ${cell.y}) ${cp ? String.fromCodePoint(cp) + ' ' : ''}${A.describe(cp)}`;
  }

  showCursor(view) {
    if (!view) return;
    $('status-cell').textContent = `커서 (${view.cursor.x}, ${view.cursor.y})`;
  }

  // ───── 클립보드 (프로그램 내부 전용) ─────
  /** 시스템 클립보드 글자 읽기 (데스크톱 앱이면 창 쪽에서, 아니면 브라우저 API로) */
  async readSystemClipboard() {
    if (host.available) return (await host.call('readClipboard')) || '';
    return navigator.clipboard && navigator.clipboard.readText ? navigator.clipboard.readText() : '';
  }

  /**
   * 시스템 클립보드에 한글 음절이 있으면 모양 그대로 IDE 클립보드 맨 위에 넣는다.
   * 한글이 아닌 글자는 빈칸으로 둔다. 같은 내용은 다시 넣지 않는다.
   */
  async syncSystemClipboard() {
    if (this._syncing) return;
    this._syncing = true;
    try {
      let text;
      try { text = await this.readSystemClipboard(); } catch { return; }
      if (!text || text === this.lastSystemClip || text.length > 50000) return;
      this.lastSystemClip = text;
      const clip = this.clipFromText(text);
      if (!clip) return;
      const sig = JSON.stringify(clip.cells);
      if (this.clips.some(c => JSON.stringify(c.cells) === sig)) return;
      clip.source = 'system';
      this.addClip(clip);
      this.status(`시스템 클립보드의 한글 ${clip.cells.length}글자를 IDE 클립보드에 넣었습니다`);
    } finally {
      this._syncing = false;
    }
  }

  clipFromText(text) {
    const cells = [];
    text.replace(/\r\n?/g, '\n').split('\n').forEach((line, y) => {
      let x = 0;
      for (const ch of line) {
        const cp = ch.codePointAt(0);
        if (cp >= 0xAC00 && cp <= 0xD7A3) cells.push([x, y, cp, null]);
        x++;
      }
    });
    if (!cells.length) return null;
    const minX = Math.min(...cells.map(c => c[0])), minY = Math.min(...cells.map(c => c[1]));
    const norm = cells.map(([x, y, cp, hl]) => [x - minX, y - minY, cp, hl]);
    return {
      w: Math.max(...norm.map(c => c[0])) + 1,
      h: Math.max(...norm.map(c => c[1])) + 1,
      cells: norm,
    };
  }

  addClip(clip) {
    clip.id = this.clipSeq++;
    clip.time = Date.now();
    this.clips.unshift(clip);
    if (this.clips.length > 40) this.clips.pop();
    this.renderClips();
    this.scheduleSave();
  }

  clipCanvas(clip, maxW = 220) {
    const cs = Math.max(8, Math.min(20, Math.floor(maxW / clip.w)));
    const c = document.createElement('canvas');
    const dpr = window.devicePixelRatio || 1;
    c.width = clip.w * cs * dpr;
    c.height = clip.h * cs * dpr;
    c.style.width = clip.w * cs + 'px';
    c.style.height = clip.h * cs + 'px';
    const ctx = c.getContext('2d');
    ctx.scale(dpr, dpr);
    ctx.font = `${Math.round(cs * 0.66)}px ${this.theme.font}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (const [x, y, cp, hl] of clip.cells) {
      if (hl) { ctx.fillStyle = hl; ctx.fillRect(x * cs, y * cs, cs, cs); }
      const d = A.decompose(cp);
      ctx.fillStyle = d ? this.theme.cat[A.category(d.cho)] : this.theme.other;
      ctx.fillText(String.fromCodePoint(cp), x * cs + cs / 2, y * cs + cs / 2 + 1);
    }
    c.__cs = cs;
    return c;
  }

  renderClips() {
    const box = $('clips');
    if (!this.clips.length) {
      box.replaceChildren(el('div', { class: 'empty-note' }, '비어 있음'));
      return;
    }
    box.replaceChildren(...this.clips.map(clip => {
      const canvas = this.clipCanvas(clip);
      const item = el('div', { class: 'clip', title: '끌어서 코드 공간에 놓기' },
        canvas,
        el('div', { class: 'meta' }, `${clip.w}×${clip.h} · ${clip.cells.length}글자${clip.source === 'system' ? ' · 시스템 클립보드' : ''}`),
        el('button', {
          class: 'small del', title: '지우기',
          onclick: e => { e.stopPropagation(); this.clips = this.clips.filter(c => c !== clip); this.renderClips(); this.scheduleSave(); },
        }, '✕'));
      item.addEventListener('pointerdown', e => {
        if (e.button !== 0 || e.target.closest('button')) return;
        const r = canvas.getBoundingClientRect();
        const cs = canvas.__cs;
        const grab = {
          x: Math.max(0, Math.min(clip.w - 1, Math.floor((e.clientX - r.left) / cs))),
          y: Math.max(0, Math.min(clip.h - 1, Math.floor((e.clientY - r.top) / cs))),
        };
        const ghost = this.clipCanvas(clip, 160);
        this.startDrag({ kind: 'clip', clip, grab }, e, el('div', {}, ghost));
      });
      return item;
    }));
  }

  pasteConflicts(doc, clip, x, y) {
    const out = new Set();
    for (const [dx, dy, cp] of clip.cells) {
      const cur = doc.get(x + dx, y + dy);
      if (cur && cur !== cp) out.add(cellKey(x + dx, y + dy));
    }
    return out;
  }

  async pasteClip(view, clip, x, y) {
    const doc = view.doc;
    const conflicts = this.pasteConflicts(doc, clip, x, y);
    let skip = false;
    if (conflicts.size) {
      const choice = await this.confirm({
        title: '덮어쓰기 확인',
        text: `붙여넣을 자리 중 ${conflicts.size}칸에 이미 다른 글자가 있습니다.`,
        buttons: [
          { label: '덮어쓰기', value: 'over', primary: true },
          { label: '빈 칸에만 붙여넣기', value: 'skip' },
          { label: '취소', value: null },
        ],
      });
      if (!choice) { view.invalidate(); return; }
      skip = choice === 'skip';
    }
    const pasted = new Set();
    doc.transact('paste', () => {
      for (const [dx, dy, cp, hl] of clip.cells) {
        const k = cellKey(x + dx, y + dy);
        if (skip && conflicts.has(k)) continue;
        doc.set(x + dx, y + dy, cp);
        if (hl) doc.setHighlight(x + dx, y + dy, hl);
        pasted.add(k);
      }
    });
    view.selection = pasted;
    view.selectionChanged();
    view.el.focus();
    this.status(`${pasted.size}글자를 붙여넣었습니다 (Ctrl+Z로 되돌리기)`);
  }

  async pasteLatest(view) {
    if (view.singleLine) return;
    await this.syncSystemClipboard();
    const clip = this.clips[0];
    if (!clip) { this.status('클립보드가 비어 있습니다'); return; }
    this.pasteClip(view, clip, view.cursor.x, view.cursor.y);
  }

  // ───── 끌어 놓기 ─────
  startDrag(payload, e, ghost) {
    e.preventDefault();
    ghost.classList.add('drag-ghost');
    document.body.append(ghost);
    let target = null;
    const place = ev => { ghost.style.left = ev.clientX + 14 + 'px'; ghost.style.top = ev.clientY + 14 + 'px'; };
    const findView = ev => {
      const hit = document.elementFromPoint(ev.clientX, ev.clientY);
      const cv = hit && hit.closest('.cv');
      return cv && cv.__view && !cv.__view.singleLine ? cv.__view : null;
    };
    const move = ev => {
      place(ev);
      const v = findView(ev);
      if (v !== target) { if (target) target.dragLeave(payload); target = v; if (v) this.setActiveView(v); }
      if (target) target.dragOver(payload, ev);
    };
    const cleanup = () => {
      ghost.remove();
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('keydown', esc, true);
    };
    const up = ev => {
      cleanup();
      if (target) target.drop(payload, ev);
    };
    const esc = ev => {
      if (ev.key !== 'Escape') return;
      ev.preventDefault();
      cleanup();
      if (target) { target.dragLeave(payload); if (target.placement) target.cancelPlacement(); }
    };
    place(e);
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('keydown', esc, true);
  }

  // ───── 한 줄 팔레트 ─────
  addPaletteLine(text = '') {
    const doc = new CodeDoc(text);
    const pseudo = { doc, machine: null, trace: null };
    const view = new CodeView(this, pseudo, { singleLine: true });
    const item = { doc, view, el: null };
    const arm = el('button', { class: 'arm', title: '배치 켜기: 코드 공간에서 시작할 칸을 누르고 끌기' }, '▶ 배치');
    item.el = el('div', { class: 'pal-line' },
      arm,
      view.el,
      el('button', {
        class: 'del', title: '이 줄 지우기',
        onclick: () => {
          if (this.armedPalette && this.armedPalette.item === item) this.disarmPalette();
          this.palette = this.palette.filter(p => p !== item);
          item.el.remove();
          this.scheduleSave();
        },
      }, '✕'));
    arm.addEventListener('click', () => {
      if (this.armedPalette && this.armedPalette.item === item) { this.disarmPalette(); this.status(''); return; }
      view.commitInPlace();
      const chars = [...doc.text()].filter(c => A.decompose(c.codePointAt(0)));
      if (!chars.length) { this.status('팔레트 줄이 비어 있습니다. 먼저 한 줄로 코드를 쓰세요'); return; }
      this.armPalette({ kind: 'palette', text: chars.join(''), item });
    });
    doc.onChange(() => this.scheduleSave());
    this.palette.push(item);
    $('palette').append(item.el);
    return item;
  }

  /** 팔레트 배치 대기: 다음에 코드 공간을 누른 칸부터 끄는 길을 따라 놓는다 */
  armPalette(payload) {
    this.disarmPalette();
    this.armedPalette = payload;
    payload.item.el.classList.add('armed');
    document.body.classList.add('armed');
    this.status(`'${payload.text}' 배치 준비 · 코드 공간에서 시작할 칸을 누르고 끌어 그리세요 · Shift = 두 칸 · Esc 취소`, true);
  }

  disarmPalette() {
    if (!this.armedPalette) return;
    this.armedPalette.item.el.classList.remove('armed');
    this.armedPalette = null;
    document.body.classList.remove('armed');
  }

  togglePalettePanel(open) {
    const panel = $('palette-panel');
    const isOpen = open ?? panel.classList.contains('closed');
    panel.classList.toggle('closed', !isOpen);
    $('pal-toggle').textContent = (isOpen ? '▾' : '▸') + ' 한 줄 팔레트';
    this.paletteOpen = isOpen;
    if (isOpen) this.palette.forEach(p => p.view.invalidate());
    this.scheduleSave();
  }

  // ───── 버전 (간단한 git) ─────
  commit() {
    const s = this.active;
    s.view.commitInPlace();
    const input = $('commit-msg');
    const msg = input.value.trim() || `버전 ${s.doc.commits.length + 1}`;
    s.doc.commit(msg);
    input.value = '';
    this.status(`'${msg}' 커밋했습니다`);
  }

  async restoreCommit(s, c) {
    const ok = await this.confirm({
      title: '버전 복원',
      text: `'${c.message}' 시점으로 되돌립니다.\n지금 상태가 마지막 커밋과 다르면 먼저 자동으로 커밋해 둡니다.`,
      buttons: [{ label: '복원', value: true, primary: true }, { label: '취소', value: false }],
    });
    if (!ok) return;
    s.view.commitInPlace();
    const last = s.doc.commits[s.doc.commits.length - 1];
    if (!last || s.doc.diff(last.snap).size) s.doc.commit(`복원 전 자동 저장`);
    s.doc.replaceAll(c.snap, 'restore');
    s.view.diffCells = null;
    s.resetMachine();
    this.status(`'${c.message}'(으)로 복원했습니다 (Ctrl+Z로 취소)`);
  }

  renderVersions() {
    const s = this.active;
    if (!s) return;
    $('ver-name').textContent = s.title;
    document.querySelector('[data-act=undo]').disabled = !s.doc.canUndo;
    document.querySelector('[data-act=redo]').disabled = !s.doc.canRedo;
    const list = $('commits');
    if (!s.doc.commits.length) list.replaceChildren(el('div', { class: 'empty-note' }, '아직 커밋이 없습니다. 메시지를 쓰고 커밋하면 지금 상태가 저장됩니다.'));
    else list.replaceChildren(...[...s.doc.commits].reverse().map(c => {
      const diffBtn = el('button', {
        title: '지금과 다른 칸 표시',
        onclick: () => {
          const v = s.view;
          if (v.diffCells && v.diffCommit === c.id) { v.diffCells = null; v.diffCommit = null; }
          else {
            v.diffCells = s.doc.diff(c.snap);
            v.diffCommit = c.id;
            this.status(`'${c.message}'과(와) ${v.diffCells.size}칸 다릅니다 (주황 테두리) · Esc로 끄기`);
          }
          v.invalidate();
        },
      }, '비교');
      return el('div', { class: 'commit' },
        el('div', { class: 'msg' }, c.message),
        el('div', { class: 'time' }, new Date(c.time).toLocaleString('ko-KR')),
        el('div', { class: 'btns' }, diffBtn, el('button', { onclick: () => this.restoreCommit(s, c) }, '복원')));
    }));
    const undo = s.doc.undoStack.slice(-12).reverse();
    $('undo-list').replaceChildren(...(undo.length
      ? undo.map(tx => el('div', {}, `${new Date(tx.time).toLocaleTimeString('ko-KR')} · ${TX_LABELS[tx.label] || tx.label} (${tx.cells.size + tx.hl.size + tx.bp.size}칸)`))
      : [el('div', { class: 'empty-note' }, '없음')]));
  }

  // ───── 파일 ─────
  async confirmDiscard(s, text) {
    return this.confirm({
      title: s.title, text,
      buttons: [{ label: '계속', value: true, primary: true }, { label: '취소', value: false }],
    });
  }

  async newFile() {
    const s = this.active;
    if (!s.doc.isEmpty() && !await this.confirmDiscard(s, '코드 공간을 비웁니다 (되돌리기 가능).')) return;
    s.doc.replaceAll({ text: '' }, 'new');
    s.filePath = null;
    s.fileName = null;
    s.resetMachine();
    this.updateTitle();
  }

  loadFileInto(s, f) {
    s.doc.replaceAll({ text: f.content }, 'open');
    s.filePath = f.path;
    s.fileName = f.name;
    s.resetMachine();
    s.view.setCursor(0, 0);
    this.updateTitle();
    this.status(`${f.name}을(를) 열었습니다`);
  }

  async openFile() {
    const s = this.active;
    try {
      const f = host.available ? await host.call('openFile', { kind: 'aheui' }) : await pickFileInBrowser('.aheui,.txt');
      if (f) this.loadFileInto(s, f);
    } catch (e) { this.status('열기 실패: ' + e.message); }
  }

  async saveFile(as) {
    const s = this.active;
    s.view.commitInPlace();
    const content = s.doc.text() + '\n';
    try {
      const name = s.fileName || (s.kind === 'main' ? 'program.aheui' : `${s.name}.aheui`);
      const r = host.available
        ? await host.call('saveFile', { content, path: as ? null : s.filePath, suggestedName: name, kind: 'aheui' })
        : downloadInBrowser(name, content);
      if (!r) return;
      s.filePath = r.path;
      s.fileName = r.name;
      this.updateTitle();
      this.status(`${r.name}에 저장했습니다`);
    } catch (e) { this.status('저장 실패: ' + e.message); }
  }

  /** 활성 코드 공간을 텍스트로 시스템 클립보드에 복사한다 (저장한 파일과 같은 내용) */
  async copyText() {
    const s = this.active;
    s.view.commitInPlace();
    const text = s.doc.text();
    if (!text) { this.status('복사할 코드가 없습니다'); return; }
    this.lastSystemClip = text; // 방금 내보낸 글은 다시 가져오지 않는다
    let ok = false;
    try {
      await navigator.clipboard.writeText(text);
      ok = true;
    } catch {
      // 대체: 숨긴 textarea로 복사
      const ta = el('textarea', { value: text });
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.append(ta);
      ta.select();
      try { ok = document.execCommand('copy'); } catch { ok = false; }
      ta.remove();
      s.view.el.focus({ preventScroll: true });
    }
    const lines = text.split('\n').length;
    this.status(ok ? `${s.title} 코드 ${lines}줄을 텍스트로 복사했습니다` : '클립보드에 복사하지 못했습니다');
  }

  async saveProject() {
    this.sessions.forEach(s => s.view.commitInPlace());
    const content = JSON.stringify(this.toJSON(), null, 1);
    try {
      const r = host.available
        ? await host.call('saveFile', { content, path: this.projectPath || null, suggestedName: 'project.aheuiproj', kind: 'project' })
        : downloadInBrowser('project.aheuiproj', content);
      if (r) { this.projectPath = r.path; this.status(`프로젝트를 ${r.name}에 저장했습니다`); }
    } catch (e) { this.status('저장 실패: ' + e.message); }
  }

  async openProject() {
    try {
      const f = host.available ? await host.call('openFile', { kind: 'project' }) : await pickFileInBrowser('.aheuiproj,.json');
      if (!f) return;
      const ok = await this.confirm({
        title: '프로젝트 열기', text: '지금 작업 공간(모든 코드 공간, 팔레트, 클립보드)이 바뀝니다.\n필요하면 먼저 프로젝트를 저장하세요.',
        buttons: [{ label: '열기', value: true, primary: true }, { label: '취소', value: false }],
      });
      if (!ok) return;
      this.applyWorkspace(JSON.parse(f.content));
      this.projectPath = f.path;
      this.status(`${f.name}을(를) 열었습니다`);
    } catch (e) { this.status('프로젝트 열기 실패: ' + e.message); }
  }

  updateTitle() {
    document.title = `아희 IDE — ${this.main.fileName || '제목 없음'}`;
    $('main-file').textContent = this.main.filePath || this.main.fileName || '';
  }

  // ───── 작업 공간 저장/복원 ─────
  toJSON() {
    return {
      version: 2,
      tool: this.tool,
      penColor: this.penColor,
      sessions: this.sessions.map(s => ({
        id: s.id, name: s.name, kind: s.kind, input: s.input, filePath: s.filePath, fileName: s.fileName,
        doc: s.doc.toJSON(),
        float: s.kind === 'sub' ? {
          x: s.frame.offsetLeft, y: s.frame.offsetTop, w: s.frame.offsetWidth, h: s.frame.offsetHeight, min: s.minimized,
        } : null,
      })),
      palette: this.palette.map(p => p.doc.text()),
      paletteOpen: this.paletteOpen !== false,
      clips: this.clips,
    };
  }

  applyWorkspace(w) {
    if (this.runner) this.pause();
    for (const s of this.sessions.filter(x => x.kind === 'sub')) s.frame.remove();
    this.sessions = this.sessions.filter(x => x.kind === 'main');
    this.palette.forEach(p => p.el.remove());
    this.palette = [];
    const mainData = w.sessions.find(s => s.kind === 'main');
    const doc = mainData ? CodeDoc.fromJSON(mainData.doc) : new CodeDoc();
    if (!this.main) this.createMain(doc);
    else {
      // 메인은 뷰를 그대로 두고 문서만 바꾼다
      this.main.view.el.remove();
      this.sessions = [];
      this.createMain(doc);
    }
    if (mainData) Object.assign(this.main, { input: mainData.input || '', filePath: mainData.filePath, fileName: mainData.fileName });
    for (const sd of w.sessions.filter(s => s.kind === 'sub')) {
      this.newSubSpace({ id: sd.id, name: sd.name, doc: CodeDoc.fromJSON(sd.doc), input: sd.input, float: sd.float, silent: true });
    }
    (w.palette || []).forEach(t => this.addPaletteLine(t));
    if (!this.palette.length) this.addPaletteLine('');
    this.togglePalettePanel(w.paletteOpen !== false);
    this.clips = w.clips || [];
    this.clipSeq = Math.max(1, ...this.clips.map(c => c.id + 1));
    if (w.penColor) this.penColor = w.penColor;
    if (TOOLS.includes(w.tool)) this.tool = w.tool;
    this.renderSwatches();
    this.renderClips();
    this.renderDock();
    this.active = null;
    this.setActive(this.main);
    this.updateTitle();
  }

  loadWorkspace() {
    try {
      const raw = localStorage.getItem(SAVE_KEY);
      if (!raw) { this.renderClips(); return false; }
      this.applyWorkspace(JSON.parse(raw));
      return true;
    } catch (e) {
      console.warn('작업 공간 복원 실패', e);
      this.renderClips();
      return false;
    }
  }

  scheduleSave() {
    clearTimeout(this._saveTimer);
    this._saveTimer = setTimeout(() => this.saveWorkspace(), 800);
  }

  saveWorkspace() {
    if (!this.main) return;
    try { localStorage.setItem(SAVE_KEY, JSON.stringify(this.toJSON())); } catch { /* 저장소를 못 쓰는 환경 */ }
  }

  // ───── 패널 ─────
  bindPanels() {
    document.querySelectorAll('.tabs button').forEach(b => b.addEventListener('click', () => {
      document.querySelectorAll('.tabs button').forEach(x => x.classList.toggle('on', x === b));
      $('tab-run').hidden = b.dataset.tab !== 'run';
      $('tab-ver').hidden = b.dataset.tab !== 'ver';
    }));
    $('input').addEventListener('input', () => {
      const s = this.active;
      s.input = $('input').value;
      if (s.machine) s.machine.setInput(s.input); // 이미 읽은 위치는 유지
      this.scheduleSave();
    });
    $('out-clear').onclick = () => {
      const s = this.active;
      if (s.machine) s.machine.output = '';
      if (s.native) s.native.stdout = '';
      this.renderRunPanel();
    };
    $('pal-add').onclick = () => { this.togglePalettePanel(true); const p = this.addPaletteLine(''); p.view.el.focus(); this.scheduleSave(); };
    $('pal-toggle').onclick = () => this.togglePalettePanel();
    $('clip-clear').onclick = () => { this.clips = []; this.renderClips(); this.scheduleSave(); };
    $('commit-msg').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); this.commit(); } });
  }

  // ───── 확인 창 ─────
  confirm({ title, text, buttons }) {
    return new Promise(resolve => {
      const modal = $('modal');
      $('modal-title').textContent = title;
      $('modal-text').textContent = text;
      const prevFocus = document.activeElement;
      const close = v => {
        modal.hidden = true;
        this.modalOpen = false;
        window.removeEventListener('keydown', key, true);
        if (prevFocus && prevFocus.focus) prevFocus.focus({ preventScroll: true });
        resolve(v);
      };
      const key = e => {
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(buttons.find(b => !b.primary && b.value !== true)?.value ?? null); }
        else if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); close(buttons.find(b => b.primary)?.value ?? null); }
      };
      $('modal-buttons').replaceChildren(...buttons.map(b =>
        el('button', { class: b.primary ? 'primary' : '', onclick: () => close(b.value) }, b.label)));
      modal.hidden = false;
      this.modalOpen = true;
      window.addEventListener('keydown', key, true);
      $('modal-buttons').querySelector('.primary')?.focus();
    });
  }
}

const app = new App();
globalThis.ideApp = app; // 디버깅용
app.init();
