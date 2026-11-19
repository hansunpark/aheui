// 코드 공간 문서: 칸 내용, 형광펜, 중단점, 되돌리기/다시하기, 버전(커밋) 기록.
// Machine이 바로 읽을 수 있도록 { height, rowLength(y), get(x,y) } 를 제공한다.

const key = (x, y) => `${x},${y}`;
export const parseKey = k => k.split(',').map(Number);

let nextCommitId = 1;

export class CodeDoc {
  constructor(text = '') {
    this.rows = [];           // rows[y][x] = 코드값, 0 = 빈칸
    this.hl = new Map();      // "x,y" → 색
    this.bp = new Set();      // "x,y"
    this.undoStack = [];
    this.redoStack = [];
    this.commits = [];        // { id, time, message, snap }
    this.listeners = new Set();
    this._tx = null;
    this._lenCache = [];
    this._height = 0;
    this.version = 0;         // 바뀔 때마다 증가 (렌더/자동 저장용)
    if (text) this._loadText(text);
  }

  // ───── 읽기 ─────
  get(x, y) {
    const r = this.rows[y];
    return r && x >= 0 && x < r.length ? r[x] : 0;
  }

  rowLength(y) {
    if (y < 0 || y >= this.rows.length) return 0;
    let n = this._lenCache[y];
    if (n === undefined) {
      const r = this.rows[y];
      n = r.length;
      while (n > 0 && !r[n - 1]) n--;
      this._lenCache[y] = n;
    }
    return n;
  }

  get height() {
    if (this._height < 0) {
      let h = this.rows.length;
      while (h > 0 && this.rowLength(h - 1) === 0) h--;
      this._height = h;
    }
    return this._height;
  }

  get width() {
    let w = 0;
    for (let y = 0; y < this.height; y++) w = Math.max(w, this.rowLength(y));
    return w;
  }

  isEmpty() { return this.height === 0; }

  /** 글자가 있는 칸들 */
  *cells() {
    for (let y = 0; y < this.rows.length; y++) {
      const r = this.rows[y];
      for (let x = 0; x < r.length; x++) if (r[x]) yield [x, y, r[x]];
    }
  }

  text() {
    const lines = [];
    for (let y = 0; y < this.height; y++) {
      let s = '';
      for (let x = 0; x < this.rowLength(y); x++) {
        const c = this.get(x, y);
        s += c ? String.fromCodePoint(c) : ' ';
      }
      lines.push(s);
    }
    return lines.join('\n');
  }

  // ───── 쓰기 (모두 트랜잭션으로 기록) ─────
  begin(label) {
    if (this._tx) { this._tx.depth++; return; }
    this._tx = { label, cells: new Map(), hl: new Map(), bp: new Map(), time: Date.now(), depth: 1 };
  }

  end() {
    const tx = this._tx;
    if (!tx) return;
    if (--tx.depth > 0) return;
    this._tx = null;
    if (!tx.cells.size && !tx.hl.size && !tx.bp.size) return;
    // 연속 타이핑은 하나로 묶는다
    const last = this.undoStack[this.undoStack.length - 1];
    // (같은 줄에서 바로 오른쪽 칸으로 이어서 친 경우만)
    const first = tx.cells.size === 1 ? parseKey([...tx.cells.keys()][0]) : null;
    tx.lastCell = first;
    const adjacent = first && last && last.lastCell && first[1] === last.lastCell[1] && Math.abs(first[0] - last.lastCell[0]) <= 1;
    if (tx.label === 'type' && last && last.label === 'type' && adjacent && tx.time - last.time < 1500) {
      for (const [k, c] of tx.cells) {
        const prev = last.cells.get(k);
        last.cells.set(k, { before: prev ? prev.before : c.before, after: c.after });
      }
      last.time = tx.time;
      last.lastCell = first;
    } else {
      this.undoStack.push(tx);
      if (this.undoStack.length > 1000) this.undoStack.shift();
    }
    this.redoStack = [];
    this._changed();
  }

  transact(label, fn) {
    this.begin(label);
    try { return fn(); } finally { this.end(); }
  }

  set(x, y, cp) {
    if (x < 0 || y < 0) return;
    cp = cp || 0;
    const before = this.get(x, y);
    if (before === cp) return;
    const auto = !this._tx;
    if (auto) this.begin('edit');
    const k = key(x, y);
    const rec = this._tx.cells.get(k);
    this._tx.cells.set(k, { before: rec ? rec.before : before, after: cp });
    this._setRaw(x, y, cp);
    if (auto) this.end();
  }

  setHighlight(x, y, color) {
    if (x < 0 || y < 0) return;
    const k = key(x, y);
    const before = this.hl.get(k) || null;
    color = color || null;
    if (before === color) return;
    const auto = !this._tx;
    if (auto) this.begin('highlight');
    const rec = this._tx.hl.get(k);
    this._tx.hl.set(k, { before: rec ? rec.before : before, after: color });
    if (color) this.hl.set(k, color); else this.hl.delete(k);
    if (auto) this.end();
  }

  toggleBreakpoint(x, y) {
    const k = key(x, y);
    const before = this.bp.has(k);
    this.transact('breakpoint', () => {
      this._tx.bp.set(k, { before, after: !before });
      if (before) this.bp.delete(k); else this.bp.add(k);
    });
  }

  hasBreakpoint(x, y) { return this.bp.has(key(x, y)); }
  highlight(x, y) { return this.hl.get(key(x, y)) || null; }

  /** 전체 내용을 바꾼다 (되돌리기 가능) */
  replaceAll(snap, label = 'replace') {
    const other = CodeDoc.fromSnapshot(snap);
    this.transact(label, () => {
      const ys = Math.max(this.rows.length, other.rows.length);
      for (let y = 0; y < ys; y++) {
        const xs = Math.max(this.rows[y] ? this.rows[y].length : 0, other.rows[y] ? other.rows[y].length : 0);
        for (let x = 0; x < xs; x++) this.set(x, y, other.get(x, y));
      }
      for (const k of new Set([...this.hl.keys(), ...other.hl.keys()])) {
        const [x, y] = parseKey(k);
        this.setHighlight(x, y, other.hl.get(k));
      }
      for (const k of new Set([...this.bp, ...other.bp])) {
        if (this.bp.has(k) !== other.bp.has(k)) {
          this._tx.bp.set(k, { before: this.bp.has(k), after: other.bp.has(k) });
          if (other.bp.has(k)) this.bp.add(k); else this.bp.delete(k);
        }
      }
    });
  }

  get canUndo() { return this.undoStack.length > 0; }
  get canRedo() { return this.redoStack.length > 0; }

  undo() { return this._applyHistory(this.undoStack, this.redoStack, 'before'); }
  redo() { return this._applyHistory(this.redoStack, this.undoStack, 'after'); }

  _applyHistory(from, to, field) {
    const tx = from.pop();
    if (!tx) return null;
    for (const [k, c] of tx.cells) { const [x, y] = parseKey(k); this._setRaw(x, y, c[field]); }
    for (const [k, c] of tx.hl) { if (c[field]) this.hl.set(k, c[field]); else this.hl.delete(k); }
    for (const [k, c] of tx.bp) { if (c[field]) this.bp.add(k); else this.bp.delete(k); }
    to.push(tx);
    this._changed();
    return tx;
  }

  _setRaw(x, y, cp) {
    while (this.rows.length <= y) this.rows.push([]);
    const r = this.rows[y];
    if (cp) { while (r.length < x) r.push(0); r[x] = cp; }
    else if (x < r.length) {
      r[x] = 0;
      while (r.length && !r[r.length - 1]) r.pop();
    }
    this._lenCache[y] = undefined;
    this._height = -1;
  }

  _loadText(text) {
    const lines = text.replace(/\r\n?/g, '\n').replace(/^﻿/, '').split('\n');
    lines.forEach((line, y) => {
      let x = 0;
      for (const ch of line) {
        const cp = ch.codePointAt(0);
        if (cp !== 0x20 && cp !== 0x09) this._setRaw(x, y, cp);
        x++;
      }
    });
    this._height = -1;
  }

  _changed() {
    this.version++;
    for (const fn of this.listeners) fn(this);
  }

  onChange(fn) { this.listeners.add(fn); return () => this.listeners.delete(fn); }

  // ───── 스냅샷 / 버전 ─────
  snapshot() {
    return { text: this.text(), hl: [...this.hl], bp: [...this.bp] };
  }

  static fromSnapshot(snap) {
    const d = new CodeDoc(snap.text || '');
    for (const [k, c] of snap.hl || []) d.hl.set(k, c);
    for (const k of snap.bp || []) d.bp.add(k);
    return d;
  }

  commit(message) {
    const c = { id: nextCommitId++, time: Date.now(), message: message || '(메시지 없음)', snap: this.snapshot() };
    this.commits.push(c);
    this._changed();
    return c;
  }

  /** 커밋과 지금 내용이 다른 칸들 */
  diff(snap) {
    const other = CodeDoc.fromSnapshot(snap);
    const out = new Set();
    const ys = Math.max(this.rows.length, other.rows.length);
    for (let y = 0; y < ys; y++) {
      const xs = Math.max(this.rows[y] ? this.rows[y].length : 0, other.rows[y] ? other.rows[y].length : 0);
      for (let x = 0; x < xs; x++) if (this.get(x, y) !== other.get(x, y)) out.add(key(x, y));
    }
    return out;
  }

  toJSON() {
    return { ...this.snapshot(), commits: this.commits };
  }

  static fromJSON(o) {
    const d = CodeDoc.fromSnapshot(o);
    d.commits = (o.commits || []).map(c => ({ ...c }));
    for (const c of d.commits) nextCommitId = Math.max(nextCommitId, c.id + 1);
    return d;
  }
}

export { key as cellKey };
