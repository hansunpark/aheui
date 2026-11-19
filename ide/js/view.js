// 코드 공간 뷰: 캔버스에 직접 그리고, 키 입력은 자체 두벌식 입력기로 받는다.
import { HangulIme, jamoForKey } from './ime.js';
import { cellKey, parseKey } from './doc.js';

const A = globalThis.Aheui;
const DIR_VEC = [[0, -1], [0, 1], [-1, 0], [1, 0]];

/** 두 칸 사이를 지나는 칸들 (브레젠햄) */
function lineCells(a, b) {
  const out = [];
  let x = a.x, y = a.y;
  const dx = Math.abs(b.x - x), dy = -Math.abs(b.y - y);
  const sx = x < b.x ? 1 : -1, sy = y < b.y ? 1 : -1;
  let err = dx + dy;
  for (let i = 0; i < 10000; i++) {
    out.push({ x, y });
    if (x === b.x && y === b.y) break;
    const e2 = 2 * err;
    if (e2 >= dy) { err += dy; x += sx; }
    if (e2 <= dx) { err += dx; y += sy; }
  }
  return out;
}

function pointInPolygon(px, py, pts) {
  let inside = false;
  for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
    const [xi, yi] = pts[i], [xj, yj] = pts[j];
    if ((yi > py) !== (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

export class CodeView {
  /**
   * @param app     앱 (도구, 상태 표시줄, 테마, 클립보드 등)
   * @param session { doc, machine?, trace? }
   * @param opts    { singleLine: 한 줄짜리 팔레트 편집기 }
   */
  constructor(app, session, opts = {}) {
    this.app = app;
    this.session = session;
    this.doc = session.doc;
    this.singleLine = !!opts.singleLine;
    this.cs = this.singleLine ? 26 : 30;
    this.cursor = { x: 0, y: 0 };
    this.ime = new HangulIme();
    this.selection = new Set();
    this.drag = null;          // 진행 중인 마우스 동작
    this.placement = null;     // 한 줄 팔레트 배치
    this.pastePreview = null;  // 클립보드 붙여넣기 미리보기
    this.diffCells = null;     // 버전 비교 표시
    this.hover = null;
    this.focused = false;
    this._raf = 0;

    this.el = document.createElement('div');
    this.el.className = 'cv' + (this.singleLine ? ' single' : '');
    this.el.tabIndex = 0;
    this.el.__view = this;
    this.scroller = document.createElement('div');
    this.scroller.className = 'cv-scroll';
    this.canvas = document.createElement('canvas');
    this.canvas.className = 'cv-canvas';
    this.spacer = document.createElement('div');
    this.spacer.className = 'cv-spacer';
    this.scroller.append(this.canvas, this.spacer);
    this.el.append(this.scroller);
    this.ctx = this.canvas.getContext('2d');

    this.el.addEventListener('keydown', e => this.onKeyDown(e));
    this.el.addEventListener('focus', () => { this.focused = true; this.app.setActiveView(this); this.invalidate(); });
    this.el.addEventListener('blur', () => { this.focused = false; this.commitInPlace(); this.invalidate(); });
    this.canvas.addEventListener('pointerdown', e => this.onPointerDown(e));
    this.canvas.addEventListener('pointermove', e => this.onPointerMove(e));
    this.canvas.addEventListener('pointerup', e => this.onPointerUp(e));
    this.canvas.addEventListener('pointerleave', () => { this.hover = null; this.app.showCellInfo(null); });
    this.canvas.addEventListener('contextmenu', e => e.preventDefault());
    this.scroller.addEventListener('scroll', () => this.invalidate());
    this.scroller.addEventListener('wheel', e => {
      if (!e.ctrlKey || this.singleLine) return;
      e.preventDefault();
      this.setZoom(this.cs * (e.deltaY < 0 ? 1.1 : 1 / 1.1));
    }, { passive: false });
    new ResizeObserver(() => this.invalidate()).observe(this.scroller);
    this.doc.onChange(() => this.invalidate());
  }

  setZoom(cs) {
    this.cs = Math.max(14, Math.min(72, Math.round(cs)));
    this.invalidate();
  }

  invalidate() {
    if (this._raf) return;
    this._raf = requestAnimationFrame(() => { this._raf = 0; this.render(); });
  }

  // ───── 좌표 ─────
  cellAt(clientX, clientY) {
    const r = this.canvas.getBoundingClientRect();
    const x = Math.floor((clientX - r.left + this.scroller.scrollLeft) / this.cs);
    const y = this.singleLine ? 0 : Math.floor((clientY - r.top + this.scroller.scrollTop) / this.cs);
    return { x: Math.max(0, x), y: Math.max(0, y) };
  }

  contentPoint(clientX, clientY) {
    const r = this.canvas.getBoundingClientRect();
    return [clientX - r.left + this.scroller.scrollLeft, clientY - r.top + this.scroller.scrollTop];
  }

  ensureVisible(x, y) {
    const cs = this.cs, s = this.scroller;
    const left = x * cs, top = y * cs;
    if (left < s.scrollLeft) s.scrollLeft = left - cs;
    else if (left + cs > s.scrollLeft + s.clientWidth) s.scrollLeft = left + 2 * cs - s.clientWidth;
    if (top < s.scrollTop) s.scrollTop = top - cs;
    else if (top + cs > s.scrollTop + s.clientHeight) s.scrollTop = top + 2 * cs - s.clientHeight;
  }

  // ───── 편집 ─────
  setCursor(x, y) {
    this.cursor = { x: Math.max(0, x), y: this.singleLine ? 0 : Math.max(0, y) };
    this.ensureVisible(this.cursor.x, this.cursor.y);
    this.app.showCursor(this);
    this.invalidate();
  }

  write(x, y, ch) {
    this.doc.transact('type', () => this.doc.set(x, y, typeof ch === 'string' ? ch.codePointAt(0) : ch));
  }

  /** 조합 중인 글자를 현재 칸에 확정한다 (커서는 그대로). 확정했으면 true */
  commitInPlace() {
    const c = this.ime.flush();
    if (c) this.write(this.cursor.x, this.cursor.y, c);
    this.invalidate();
    return !!c;
  }

  typeJamo(j) {
    const committed = this.ime.feed(j);
    if (committed) {
      this.write(this.cursor.x, this.cursor.y, committed);
      this.setCursor(this.cursor.x + 1, this.cursor.y);
    }
    this.invalidate();
  }

  onKeyDown(e) {
    if (e.isComposing) return;
    const ctrl = e.ctrlKey || e.metaKey;
    let handled = true;
    if (ctrl) {
      switch (e.code) {
        case 'KeyZ': this.commitInPlace(); e.shiftKey ? this.doc.redo() : this.doc.undo(); break;
        case 'KeyY': this.commitInPlace(); this.doc.redo(); break;
        // 조합 중인 글자를 먼저 확정해야 복사에 들어간다
        case 'KeyC': this.commitInPlace(); this.copySelection(false); break;
        case 'KeyX': this.commitInPlace(); this.copySelection(true); break;
        case 'KeyV': this.commitInPlace(); this.app.pasteLatest(this); break;
        case 'KeyA': this.selectAll(); break;
        default: handled = false;
      }
      if (handled) e.preventDefault();
      return;
    }
    if (e.altKey) return;
    const { x, y } = this.cursor;
    switch (e.code) {
      case 'ArrowLeft': this.commitInPlace(); this.setCursor(x - 1, y); break;
      case 'ArrowRight': this.commitInPlace(); this.setCursor(x + 1, y); break;
      case 'ArrowUp': this.commitInPlace(); this.setCursor(x, y - 1); break;
      case 'ArrowDown': this.commitInPlace(); this.setCursor(x, y + 1); break;
      case 'Space': this.setCursor(x + (this.commitInPlace() ? 2 : 1), y); break;
      case 'BracketLeft': case 'Home': this.commitInPlace(); this.setCursor(0, y); break;
      case 'BracketRight': case 'End': this.commitInPlace(); this.setCursor(this.doc.rowLength(y), y); break;
      case 'Backspace':
        if (this.ime.backspace()) { this.invalidate(); break; }
        if (this.selection.size) { this.deleteSelection(); break; }
        if (x > 0) { this.doc.transact('delete', () => this.doc.set(x - 1, y, 0)); this.setCursor(x - 1, y); }
        break;
      case 'Delete':
        this.ime.reset();
        if (this.selection.size) this.deleteSelection();
        else this.doc.transact('delete', () => this.doc.set(x, y, 0));
        break;
      case 'Comma': this.commitInPlace(); if (!this.singleLine) this.app.stepBack(this.session); break;
      case 'Period': this.commitInPlace(); if (!this.singleLine) this.app.step(this.session); break;
      case 'Enter': case 'NumpadEnter':
        if (this.placement && this.placement.pending) this.applyPlacement();
        else this.commitInPlace();
        break;
      case 'Escape':
        if (this.placement) this.cancelPlacement();
        else if (this.ime.isComposing) { this.ime.reset(); this.invalidate(); }
        else if (this.selection.size) { this.selection.clear(); this.app.status(''); this.invalidate(); }
        else if (this.diffCells) { this.diffCells = null; this.invalidate(); }
        break;
      default: {
        const j = jamoForKey(e.code, e.shiftKey);
        if (j) this.typeJamo(j);
        else handled = /^(Digit|Numpad|Minus|Equal|Quote|Semicolon|Slash|Backslash|Backquote|Tab)/.test(e.code); // 한글이 아닌 글자는 입력하지 않는다
      }
    }
    if (handled) e.preventDefault();
  }

  // ───── 선택 ─────
  selectAll() {
    this.selection = new Set([...this.doc.cells()].map(([x, y]) => cellKey(x, y)));
    this.selectionChanged();
  }

  selectionChanged() {
    const n = this.selection.size;
    this.app.status(n ? `${n}칸 선택 · Delete 삭제 · Ctrl+C 복사 · Ctrl+X 잘라내기 · Esc 해제` : '');
    this.invalidate();
  }

  deleteSelection() {
    this.doc.transact('delete', () => {
      for (const k of this.selection) { const [x, y] = parseKey(k); this.doc.set(x, y, 0); }
    });
    this.selection.clear();
    this.selectionChanged();
  }

  copySelection(cut) {
    const cells = [];
    for (const k of this.selection) {
      const [x, y] = parseKey(k);
      const cp = this.doc.get(x, y);
      if (cp) cells.push([x, y, cp, this.doc.highlight(x, y)]);
    }
    if (!cells.length) { this.app.status('복사할 글자가 없습니다 (선택 도구로 먼저 선택하세요)'); return; }
    const minX = Math.min(...cells.map(c => c[0])), minY = Math.min(...cells.map(c => c[1]));
    const norm = cells.map(([x, y, cp, hl]) => [x - minX, y - minY, cp, hl]);
    this.app.addClip({
      w: Math.max(...norm.map(c => c[0])) + 1,
      h: Math.max(...norm.map(c => c[1])) + 1,
      cells: norm,
    });
    if (cut) this.deleteSelection();
    else this.app.status(`${cells.length}글자를 클립보드에 복사했습니다`);
  }

  // ───── 마우스 ─────
  onPointerDown(e) {
    this.el.focus({ preventScroll: true });
    const cell = this.cellAt(e.clientX, e.clientY);
    const tool = this.singleLine ? 'cursor' : this.app.tool;
    const erase = e.button === 2;
    if (e.button === 1) return;
    this.canvas.setPointerCapture(e.pointerId);
    const mode = e.shiftKey ? 'add' : e.altKey ? 'sub' : 'set';
    // 팔레트에서 배치를 켠 상태: 처음 누른 칸부터 끄는 길을 따라 글자를 놓는다
    const armed = !this.singleLine && this.app.armedPalette;
    if (armed && e.button === 0) {
      this.app.disarmPalette();
      this.commitInPlace();
      this.startPlacement(armed);
      this.placementTo(cell, e.shiftKey);
      this.drag = { type: 'place' };
      this.invalidate();
      return;
    }
    switch (tool) {
      case 'cursor':
        // 클릭 = 그 칸에 입력, 드래그 = 사각형 선택
        this.commitInPlace();
        this.setCursor(cell.x, cell.y);
        this.drag = { type: 'rect', start: cell, end: cell, mode, click: true };
        break;
      case 'pen':
        this.doc.begin('pen');
        this.drag = { type: 'pen', last: cell, erase };
        this.paint(cell, erase);
        break;
      case 'bp':
        this.doc.toggleBreakpoint(cell.x, cell.y);
        break;
      case 'rect':
        this.drag = { type: 'rect', start: cell, end: cell, mode };
        break;
      case 'lasso':
        this.drag = { type: 'lasso', pts: [this.contentPoint(e.clientX, e.clientY)], mode };
        break;
      case 'brush':
        this.drag = { type: 'brush', cells: new Set([cellKey(cell.x, cell.y)]), last: cell, mode };
        break;
    }
    this.invalidate();
  }

  onPointerMove(e) {
    const cell = this.cellAt(e.clientX, e.clientY);
    if (!this.hover || this.hover.x !== cell.x || this.hover.y !== cell.y) {
      this.hover = cell;
      this.app.showCellInfo(this, cell);
    }
    const d = this.drag;
    if (!d) return;
    switch (d.type) {
      case 'pen':
        for (const c of lineCells(d.last, cell)) this.paint(c, d.erase);
        d.last = cell;
        break;
      case 'place': this.placementTo(cell, e.shiftKey); break;
      case 'rect':
        d.end = cell;
        if (cell.x !== d.start.x || cell.y !== d.start.y) d.click = false;
        break;
      case 'lasso': d.pts.push(this.contentPoint(e.clientX, e.clientY)); break;
      case 'brush':
        for (const c of lineCells(d.last, cell)) d.cells.add(cellKey(c.x, c.y));
        d.last = cell;
        break;
    }
    this.invalidate();
  }

  onPointerUp(e) {
    const d = this.drag;
    this.drag = null;
    if (!d) return;
    if (d.type === 'pen') { this.doc.end(); return; }
    if (d.type === 'place') { this.finishPlacement(); return; }
    if (d.type === 'rect' && d.click) {
      // 그냥 클릭: 선택 해제 (Shift/Alt 클릭은 선택을 그대로 둔다)
      if (d.mode === 'set' && this.selection.size) { this.selection.clear(); this.selectionChanged(); }
      return;
    }
    const cells = this.draftCells(d);
    if (d.mode === 'set') this.selection = cells;
    else if (d.mode === 'add') for (const k of cells) this.selection.add(k);
    else for (const k of cells) this.selection.delete(k);
    this.selectionChanged();
  }

  paint(cell, erase) {
    this.doc.setHighlight(cell.x, cell.y, erase ? null : this.app.penColor);
  }

  /** 드래그 중인 선택 도구가 고른 칸들 */
  draftCells(d) {
    const out = new Set();
    if (d.type === 'rect') {
      const x0 = Math.min(d.start.x, d.end.x), x1 = Math.max(d.start.x, d.end.x);
      const y0 = Math.min(d.start.y, d.end.y), y1 = Math.max(d.start.y, d.end.y);
      for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) out.add(cellKey(x, y));
    } else if (d.type === 'lasso') {
      if (d.pts.length < 3) return out;
      const cs = this.cs;
      const xs = d.pts.map(p => p[0]), ys = d.pts.map(p => p[1]);
      const x0 = Math.max(0, Math.floor(Math.min(...xs) / cs)), x1 = Math.floor(Math.max(...xs) / cs);
      const y0 = Math.max(0, Math.floor(Math.min(...ys) / cs)), y1 = Math.floor(Math.max(...ys) / cs);
      for (let y = y0; y <= y1; y++)
        for (let x = x0; x <= x1; x++)
          if (pointInPolygon((x + 0.5) * cs, (y + 0.5) * cs, d.pts)) out.add(cellKey(x, y));
    } else if (d.type === 'brush') {
      for (const k of d.cells) out.add(k);
    }
    return out;
  }

  // ───── 클립보드 끌어 놓기 대상 ─────
  dragOver(payload, e) {
    if (this.singleLine || payload.kind !== 'clip') return;
    const cell = this.cellAt(e.clientX, e.clientY);
    const x = Math.max(0, cell.x - payload.grab.x), y = Math.max(0, cell.y - payload.grab.y);
    this.pastePreview = { clip: payload.clip, x, y, conflicts: this.app.pasteConflicts(this.doc, payload.clip, x, y) };
    this.invalidate();
  }

  dragLeave() {
    this.pastePreview = null;
    this.invalidate();
  }

  drop(payload) {
    const p = this.pastePreview;
    this.pastePreview = null;
    if (p && payload.kind === 'clip') this.app.pasteClip(this, p.clip, p.x, p.y);
    this.invalidate();
  }

  /** 팔레트 배치 드래그를 끝냈을 때: 놓인 글자를 강조하고 Enter/Esc를 기다린다 */
  finishPlacement() {
    const P = this.placement;
    if (!P) return;
    if (!P.placed.length) { this.placement = null; this.app.status(''); this.invalidate(); return; }
    P.pending = true;
    this.el.focus({ preventScroll: true });
    this.app.status(`팔레트 배치 ${P.placed.length}/${P.chars.length}글자 · Enter를 눌러 적용 · Esc 취소`, true);
    this.invalidate();
  }

  // ───── 한 줄 팔레트 배치 ─────
  startPlacement(source) {
    this.placement = { source, chars: [...source.text].map(c => c.codePointAt(0)), placed: [], pending: false };
  }

  isFree(x, y) {
    return x >= 0 && y >= 0 && !this.doc.get(x, y);
  }

  /** 마우스가 cell에 있을 때까지 배치를 늘리거나(앞으로) 줄인다(되돌아오면) */
  placementTo(cell, twoStep) {
    const P = this.placement;
    if (!P || P.pending) return;
    const placed = P.placed;
    if (!placed.length) {
      if (this.isFree(cell.x, cell.y)) placed.push({ x: cell.x, y: cell.y, step: 1 });
      return;
    }
    // 이미 놓은 칸으로 되돌아오면 그 뒤의 글자를 거둔다
    const back = placed.findIndex(p => p.x === cell.x && p.y === cell.y);
    if (back >= 0) {
      placed.length = back + 1;
      placed[back].step = 1;
      return;
    }
    for (let guard = 0; guard < 500; guard++) {
      const last = placed[placed.length - 1];
      const dx = cell.x - last.x, dy = cell.y - last.y;
      if (!dx && !dy) return;
      if (placed.length >= P.chars.length) return;
      const step = twoStep ? 2 : 1;
      const horizontal = Math.abs(dx) >= Math.abs(dy);
      const dist = horizontal ? Math.abs(dx) : Math.abs(dy);
      if (dist < step) return;
      const nx = last.x + (horizontal ? Math.sign(dx) * step : 0);
      const ny = last.y + (horizontal ? 0 : Math.sign(dy) * step);
      if (this.singleLine && ny !== 0) return;
      // 기존 글자는 덮어쓰지 않는다 (두 칸 이동은 가운데 칸을 건너뛰므로 그 칸은 차 있어도 된다)
      if (!this.isFree(nx, ny) || placed.some(p => p.x === nx && p.y === ny)) return;
      last.step = step;
      placed.push({ x: nx, y: ny, step: 1 });
    }
  }

  /** 배치된 각 칸에 들어갈 글자 (홀소리를 경로 방향으로 바꾼다) */
  placementChars() {
    const P = this.placement;
    return P.placed.map((p, i) => {
      const cp = P.chars[i];
      const d = A.decompose(cp);
      const next = P.placed[i + 1];
      if (!d || !next) return cp; // 마지막 글자는 원래 홀소리 유지
      const dx = Math.sign(next.x - p.x), dy = Math.sign(next.y - p.y);
      let dir = dy < 0 ? A.UP : dy > 0 ? A.DOWN : dx < 0 ? A.LEFT : A.RIGHT;
      if (p.step === 2) dir |= A.FAST;
      return A.compose(d.cho, A.VOWEL_FOR_DIR[dir], d.jong);
    });
  }

  applyPlacement() {
    const P = this.placement;
    const chars = this.placementChars();
    this.doc.transact('palette', () => P.placed.forEach((p, i) => this.doc.set(p.x, p.y, chars[i])));
    this.placement = null;
    this.app.status(`팔레트 ${chars.length}글자를 배치했습니다 (Ctrl+Z로 되돌리기)`);
    this.invalidate();
  }

  cancelPlacement() {
    this.placement = null;
    this.app.status('팔레트 배치를 취소했습니다');
    this.invalidate();
  }

  // ───── 그리기 ─────
  render() {
    const T = this.app.theme;
    const cs = this.cs;
    const doc = this.doc;
    const sc = this.scroller;
    const w = sc.clientWidth, h = sc.clientHeight;
    if (!w || !h) return;
    const dpr = window.devicePixelRatio || 1;
    if (this.canvas.width !== Math.round(w * dpr) || this.canvas.height !== Math.round(h * dpr)) {
      this.canvas.width = Math.round(w * dpr);
      this.canvas.height = Math.round(h * dpr);
      this.canvas.style.width = w + 'px';
      this.canvas.style.height = h + 'px';
    }
    // 스크롤 영역: 코드보다 넉넉하게
    const cols = Math.max(doc.width, this.cursor.x + 1) + (this.singleLine ? 8 : 30);
    const rows = this.singleLine ? 1 : Math.max(doc.height, this.cursor.y + 1) + 20;
    this.spacer.style.width = cols * cs + 'px';
    this.spacer.style.height = rows * cs + 'px';

    const ctx = this.ctx;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const sx = sc.scrollLeft, sy = sc.scrollTop;
    const x0 = Math.floor(sx / cs), y0 = Math.floor(sy / cs);
    const x1 = Math.ceil((sx + w) / cs), y1 = Math.ceil((sy + h) / cs);
    const inView = (x, y) => x >= x0 - 1 && x <= x1 && y >= y0 - 1 && y <= y1;
    const px = x => x * cs - sx, py = y => y * cs - sy;

    ctx.fillStyle = T.bg;
    ctx.fillRect(0, 0, w, h);

    // 격자
    ctx.strokeStyle = T.grid;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let x = x0; x <= x1; x++) { const X = Math.round(px(x)) + 0.5; ctx.moveTo(X, 0); ctx.lineTo(X, h); }
    for (let y = y0; y <= y1; y++) { const Y = Math.round(py(y)) + 0.5; ctx.moveTo(0, Y); ctx.lineTo(w, Y); }
    ctx.stroke();

    // 형광펜
    for (const [k, color] of doc.hl) {
      const [x, y] = parseKey(k);
      if (!inView(x, y)) continue;
      ctx.fillStyle = color;
      ctx.fillRect(px(x), py(y), cs, cs);
    }

    // 선택 영역 (확정 + 드래그 중)
    const sel = this.selection;
    const dg = this.drag;
    const selecting = dg && (dg.type === 'lasso' || dg.type === 'brush' || (dg.type === 'rect' && !dg.click));
    const draft = selecting ? this.draftCells(dg) : null;
    if (sel.size || draft) {
      ctx.fillStyle = T.sel;
      const drawSel = k => { const [x, y] = parseKey(k); if (inView(x, y)) ctx.fillRect(px(x) + 1, py(y) + 1, cs - 1, cs - 1); };
      if (!draft || this.drag.mode !== 'set') sel.forEach(drawSel);
      if (draft) draft.forEach(drawSel);
      ctx.strokeStyle = T.selLine;
      ctx.lineWidth = 1.5;
      if (!draft || this.drag.mode !== 'set') for (const k of sel) {
        const [x, y] = parseKey(k);
        if (!inView(x, y)) continue;
        // 바깥 경계선만
        const edges = [[0, -1, 0, 0, 1, 0], [0, 1, 0, 1, 1, 1], [-1, 0, 0, 0, 0, 1], [1, 0, 1, 0, 1, 1]];
        ctx.beginPath();
        for (const [ex, ey, ax, ay, bx, by] of edges) {
          if (sel.has(cellKey(x + ex, y + ey))) continue;
          ctx.moveTo(px(x + ax), py(y + ay));
          ctx.lineTo(px(x + bx), py(y + by));
        }
        ctx.stroke();
      }
    }

    // 버전 비교
    if (this.diffCells) {
      ctx.strokeStyle = T.diff;
      ctx.lineWidth = 2;
      for (const k of this.diffCells) {
        const [x, y] = parseKey(k);
        if (inView(x, y)) ctx.strokeRect(px(x) + 2, py(y) + 2, cs - 4, cs - 4);
      }
    }

    // 중단점
    ctx.fillStyle = T.bp;
    for (const k of doc.bp) {
      const [x, y] = parseKey(k);
      if (!inView(x, y)) continue;
      ctx.beginPath();
      ctx.arc(px(x) + cs * 0.16, py(y) + cs * 0.16, Math.max(3, cs * 0.1), 0, Math.PI * 2);
      ctx.fill();
    }

    // 실행 경로 화살표 (글자 사이 빈 곳에 그리고, 배경색 테두리를 둘러 형광펜 위에서도 보이게)
    const m = this.session.machine;
    if (this.session.trace && this.session.trace.size) this.drawTrace(ctx, px, py, inView, m ? m.steps : 0);

    // 글자
    ctx.font = `${Math.round(cs * 0.62)}px ${T.font}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (let y = y0; y <= y1; y++) {
      const r = doc.rows[y];
      if (!r) continue;
      for (let x = Math.max(0, x0); x <= Math.min(x1, r.length - 1); x++) {
        const cp = r[x];
        if (!cp) continue;
        const d = A.decompose(cp);
        ctx.fillStyle = d ? T.cat[A.category(d.cho)] : T.other;
        ctx.fillText(String.fromCodePoint(cp), px(x) + cs / 2, py(y) + cs / 2 + 1);
      }
    }

    // 팔레트 배치 미리보기
    if (this.placement && this.placement.placed.length) {
      const chars = this.placementChars();
      this.placement.placed.forEach((p, i) => {
        ctx.fillStyle = T.placeBg;
        ctx.fillRect(px(p.x), py(p.y), cs, cs);
        ctx.strokeStyle = T.accent;
        ctx.lineWidth = 2;
        ctx.strokeRect(px(p.x) + 1, py(p.y) + 1, cs - 2, cs - 2);
        ctx.fillStyle = T.accent;
        ctx.fillText(String.fromCodePoint(chars[i]), px(p.x) + cs / 2, py(p.y) + cs / 2 + 1);
      });
    }

    // 붙여넣기 미리보기
    if (this.pastePreview) {
      const { clip, x, y, conflicts } = this.pastePreview;
      ctx.globalAlpha = 0.75;
      for (const [dx, dy, cp] of clip.cells) {
        const cx = x + dx, cy = y + dy;
        if (!inView(cx, cy)) continue;
        const conflict = conflicts.has(cellKey(cx, cy));
        ctx.fillStyle = conflict ? T.conflictBg : T.placeBg;
        ctx.fillRect(px(cx), py(cy), cs, cs);
        ctx.fillStyle = conflict ? T.bp : T.accent;
        ctx.fillText(String.fromCodePoint(cp), px(cx) + cs / 2, py(cy) + cs / 2 + 1);
      }
      ctx.globalAlpha = 1;
      ctx.setLineDash([4, 3]);
      ctx.strokeStyle = T.accent;
      ctx.lineWidth = 1.5;
      ctx.strokeRect(px(x) + 0.5, py(y) + 0.5, clip.w * cs, clip.h * cs);
      ctx.setLineDash([]);
    }

    // 실행 위치
    if (m && !this.singleLine) {
      ctx.strokeStyle = m.halted ? T.halted : T.exec;
      ctx.lineWidth = 3;
      ctx.strokeRect(px(m.x) + 1.5, py(m.y) + 1.5, cs - 3, cs - 3);
    }

    // 편집 커서 + 조합 중인 글자
    const c = this.cursor;
    if (this.focused || this.ime.isComposing) {
      const comp = this.ime.composing;
      if (comp) {
        ctx.fillStyle = T.bg;
        ctx.fillRect(px(c.x) + 2, py(c.y) + 2, cs - 4, cs - 4);
        ctx.fillStyle = T.text;
        ctx.fillText(comp, px(c.x) + cs / 2, py(c.y) + cs / 2 + 1);
        ctx.fillStyle = T.accent;
        ctx.fillRect(px(c.x) + 4, py(c.y) + cs - 4, cs - 8, 2);
      }
      ctx.strokeStyle = this.focused ? T.accent : T.grid;
      ctx.lineWidth = 2;
      ctx.strokeRect(px(c.x) + 1, py(c.y) + 1, cs - 2, cs - 2);
    }

    // 사각형/올가미 선택 테두리
    const dr = this.drag;
    if (dr && dr.type === 'lasso' && dr.pts.length > 1) {
      ctx.strokeStyle = T.selLine;
      ctx.lineWidth = 1.5;
      ctx.setLineDash([5, 4]);
      ctx.beginPath();
      dr.pts.forEach(([X, Y], i) => (i ? ctx.lineTo(X - sx, Y - sy) : ctx.moveTo(X - sx, Y - sy)));
      ctx.closePath();
      ctx.stroke();
      ctx.setLineDash([]);
    }
  }

  drawTrace(ctx, px, py, inView, now) {
    const T = this.app.theme;
    const cs = this.cs;
    const gap = cs * 0.3;      // 글자 중심에서 이만큼 떨어진 곳부터 그린다
    const off = cs * 0.09;     // 오가는 경로가 겹치지 않게 옆으로 비킨다
    const head = Math.max(4, cs * 0.16);
    const segs = [];
    for (const e of this.session.trace.values()) {
      if (e.count <= 0) continue;
      if (!inView(e.fx, e.fy) && !inView(e.tx, e.ty)) continue;
      const [vx, vy] = DIR_VEC[e.dir & 3];
      const len = e.dir & 4 ? 2 : 1;
      const nx = -vy, ny = vx; // 진행 방향 기준 오른쪽
      const fcx = px(e.fx) + cs / 2 + nx * off, fcy = py(e.fy) + cs / 2 + ny * off;
      const tcx = px(e.tx) + cs / 2 + nx * off, tcy = py(e.ty) + cs / 2 + ny * off;
      const wrapped = e.tx !== e.fx + vx * len || e.ty !== e.fy + vy * len;
      const age = now - e.last;
      const alpha = age < 40 ? 1 : Math.max(0.35, 1 - age / 4000);
      if (!wrapped) segs.push([fcx + vx * gap, fcy + vy * gap, tcx - vx * gap, tcy - vy * gap, alpha]);
      else {
        // 가장자리를 넘어 반대편으로: 나가는 짧은 화살표와 들어오는 짧은 화살표
        segs.push([fcx + vx * gap, fcy + vy * gap, fcx + vx * cs * 0.62, fcy + vy * cs * 0.62, alpha]);
        segs.push([tcx - vx * cs * 0.62, tcy - vy * cs * 0.62, tcx - vx * gap, tcy - vy * gap, alpha]);
      }
    }
    const drawSeg = (s, color, width, extra) => {
      const [ax, ay, bx, by, alpha] = s;
      const ang = Math.atan2(by - ay, bx - ax);
      ctx.globalAlpha = alpha;
      ctx.strokeStyle = color;
      ctx.fillStyle = color;
      ctx.lineWidth = width;
      ctx.beginPath();
      ctx.moveTo(ax, ay);
      ctx.lineTo(bx - Math.cos(ang) * head * 0.6, by - Math.sin(ang) * head * 0.6);
      ctx.stroke();
      const hs = head + extra;
      ctx.beginPath();
      ctx.moveTo(bx + Math.cos(ang) * extra, by + Math.sin(ang) * extra);
      ctx.lineTo(bx - Math.cos(ang - 0.5) * hs, by - Math.sin(ang - 0.5) * hs);
      ctx.lineTo(bx - Math.cos(ang + 0.5) * hs, by - Math.sin(ang + 0.5) * hs);
      ctx.closePath();
      ctx.fill();
    };
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    for (const s of segs) drawSeg(s, T.arrowHalo, 5, 2);   // 테두리
    for (const s of segs) drawSeg(s, T.arrow, 2, 0);       // 본선
    ctx.globalAlpha = 1;
    ctx.lineCap = 'butt';
  }
}
