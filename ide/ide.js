'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const codeEl = $('code'), inputEl = $('input'), outputEl = $('output'), gridEl = $('grid');
  const STORE_KEY = 'aheui-ide';

  let machine = null;        // 현재 실행기 (코드를 고치면 새로 만든다)
  let running = false;
  let timer = null;
  let cellEls = [];          // cellEls[y][x]
  let curEl = null;
  let breakpoints = new Set();
  let nativeAvailable = false;

  // ───── 저장/복원 ─────
  function load() {
    try {
      const saved = JSON.parse(localStorage.getItem(STORE_KEY) || 'null');
      if (saved) { codeEl.value = saved.code || ''; inputEl.value = saved.input || ''; return; }
    } catch { /* 저장소를 못 쓰는 환경 */ }
    codeEl.value = AHEUI_SAMPLES[0].code;
  }
  let saveTimer = null;
  function saveLater() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => {
      try { localStorage.setItem(STORE_KEY, JSON.stringify({ code: codeEl.value, input: inputEl.value })); } catch { }
    }, 300);
  }

  // ───── 실행기 ─────
  function rebuild() {
    stop();
    machine = new Aheui.Machine(codeEl.value, inputEl.value);
    renderGrid();
    renderState();
  }

  function stop() {
    running = false;
    clearTimeout(timer);
    timer = null;
    updateButtons();
  }

  function renderGrid() {
    const rows = machine.rows;
    const width = Math.max(1, ...rows.map(r => r.length));
    const frag = document.createDocumentFragment();
    cellEls = [];
    curEl = null;
    rows.forEach((row, y) => {
      const rowEl = document.createElement('div');
      rowEl.className = 'row';
      const els = [];
      for (let x = 0; x < width; x++) {
        const el = document.createElement('span');
        const c = x < row.length ? row[x] : 0x20;
        const d = Aheui.decompose(c);
        el.className = 'cell ' + (d ? Aheui.category(d.cho) : 'other');
        el.textContent = c === 0x20 ? '' : String.fromCodePoint(c);
        el.dataset.x = x;
        el.dataset.y = y;
        if (breakpoints.has(`${x},${y}`)) el.classList.add('bp');
        rowEl.appendChild(el);
        els.push(el);
      }
      cellEls.push(els);
      frag.appendChild(rowEl);
    });
    gridEl.replaceChildren(frag);
  }

  function storeName(i) {
    if (i === 0) return '(없음)';
    if (i === Aheui.QUEUE) return 'ㅇ 큐';
    if (i === Aheui.PIPE) return 'ㅎ 통로';
    return Aheui.JONG[i];
  }

  function renderState() {
    const m = machine;
    // 커서
    if (curEl) curEl.classList.remove('cur');
    curEl = cellEls[m.y] && cellEls[m.y][m.x];
    if (curEl) {
      curEl.classList.add('cur', 'visited');
      curEl.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
    // 상태
    const stEl = $('st-state');
    stEl.className = '';
    if (m.error) { stEl.textContent = '오류: ' + m.error; stEl.className = 'error'; }
    else if (m.halted) stEl.textContent = `종료 (종료 코드 ${m.exitCode})`;
    else stEl.textContent = running ? '실행 중' : m.steps ? '일시정지' : '준비';
    $('st-steps').textContent = m.steps.toLocaleString();
    $('st-pos').textContent = `(${m.x}, ${m.y})`;
    $('st-dir').textContent = Aheui.DIR_NAMES[m.dir];
    $('st-storage').textContent = storeName(m.storage);
    // 저장공간: 현재 것과 값이 있는 것만
    const frag = document.createDocumentFragment();
    for (let i = 0; i < 28; i++) {
      const items = m.items(i);
      if (!items.length && i !== m.storage) continue;
      const row = document.createElement('div');
      row.className = 'store' + (i === m.storage ? ' current' : '') + (items.length ? '' : ' empty');
      const name = document.createElement('span');
      name.className = 'name';
      name.textContent = storeName(i);
      const vals = document.createElement('span');
      vals.className = 'vals';
      const shown = items.length > 200 ? ['…', ...items.slice(-200)] : items;
      vals.textContent = items.length ? shown.join(' ') + (i === Aheui.QUEUE ? '  ← 뒤' : '  ← 위') : '비어 있음';
      row.append(name, vals);
      frag.appendChild(row);
    }
    $('stores').replaceChildren(frag);
    outputEl.textContent = m.output;
    outputEl.scrollTop = outputEl.scrollHeight;
    updateButtons();
  }

  function updateButtons() {
    const done = !machine || machine.halted;
    $('btn-run').disabled = running || done;
    $('btn-run').textContent = machine && machine.steps && !done ? '▶ 계속' : '▶ 실행';
    $('btn-pause').disabled = !running;
    $('btn-step').disabled = running || done;
  }

  // 속도 1~5: 한 단계씩 지연, 6~10: 프레임마다 많은 단계
  const DELAYS = [600, 250, 100, 40, 10];
  const BATCH = [20, 300, 5000, 100000, Infinity];

  function run() {
    if (!machine || machine.halted) return;
    running = true;
    updateButtons();
    let first = true;
    const tick = () => {
      if (!running) return;
      const speed = +$('speed').value;
      const limit = speed <= 5 ? 1 : BATCH[speed - 6];
      const deadline = performance.now() + 30;
      for (let i = 0; i < limit; i++) {
        if (!first && breakpoints.has(`${machine.x},${machine.y}`)) { stop(); renderState(); return; }
        first = false;
        if (!machine.step()) { stop(); renderState(); return; }
        if ((i & 1023) === 1023 && performance.now() > deadline) break;
      }
      renderState();
      timer = setTimeout(tick, speed <= 5 ? DELAYS[speed - 1] : 0);
    };
    tick();
  }

  function step() {
    if (!machine || machine.halted || running) return;
    machine.step();
    renderState();
  }

  // ───── 네이티브 실행 (aheui ide 서버로 열었을 때만) ─────
  async function detectNative() {
    if (location.protocol === 'file:') return;
    try {
      const r = await fetch('api/info');
      nativeAvailable = r.ok && (await r.json()).native;
    } catch { nativeAvailable = false; }
    $('btn-native').hidden = !nativeAvailable;
    $('native-off').hidden = nativeAvailable;
  }

  async function runNative() {
    if (!nativeAvailable) return;
    stop();
    const btn = $('btn-native');
    btn.disabled = true;
    $('st-state').textContent = '네이티브 실행 중…';
    try {
      const r = await fetch('api/run', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ code: codeEl.value, input: inputEl.value, timeoutMs: 30000 }),
      });
      const res = await r.json();
      if (res.error) throw new Error(res.error);
      outputEl.textContent = res.stdout;
      const stEl = $('st-state');
      if (res.timedOut) { stEl.textContent = '네이티브: 30초 시간 초과로 중단'; stEl.className = 'error'; }
      else {
        const err = res.stderr.split('\n').filter(l => l && !l.startsWith('[aheui]')).join(' ');
        stEl.textContent = `네이티브: 종료 코드 ${res.exitCode}, ${res.elapsedMs.toFixed(0)} ms` + (err ? ` — ${err}` : '');
        stEl.className = err ? 'error' : '';
      }
    } catch (e) {
      $('st-state').textContent = '네이티브 실행 실패: ' + e.message;
      $('st-state').className = 'error';
    } finally {
      btn.disabled = false;
    }
  }

  // ───── 파일 ─────
  function saveFile() {
    const blob = new Blob([codeEl.value], { type: 'text/plain;charset=utf-8' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'program.aheui';
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }

  // ───── 이벤트 ─────
  $('btn-run').onclick = run;
  $('btn-pause').onclick = () => { stop(); renderState(); };
  $('btn-step').onclick = step;
  $('btn-reset').onclick = rebuild;
  $('btn-native').onclick = runNative;
  $('btn-clear-out').onclick = () => { outputEl.textContent = ''; if (machine) machine.output = ''; };
  $('btn-new').onclick = () => { codeEl.value = ''; inputEl.value = ''; breakpoints.clear(); saveLater(); rebuild(); };
  $('btn-open').onclick = () => $('file-input').click();
  $('btn-save').onclick = saveFile;
  $('file-input').onchange = async e => {
    const f = e.target.files[0];
    if (!f) return;
    codeEl.value = await f.text();
    breakpoints.clear();
    saveLater();
    rebuild();
    e.target.value = '';
  };

  const sel = $('sel-sample');
  AHEUI_SAMPLES.forEach((s, i) => sel.add(new Option(s.name, i)));
  sel.onchange = () => {
    const s = AHEUI_SAMPLES[sel.value];
    if (!s) return;
    codeEl.value = s.code;
    inputEl.value = s.input;
    breakpoints.clear();
    sel.value = '';
    saveLater();
    rebuild();
  };

  let editTimer = null;
  codeEl.addEventListener('input', () => {
    saveLater();
    clearTimeout(editTimer);
    editTimer = setTimeout(rebuild, 150);
  });
  inputEl.addEventListener('input', () => {
    saveLater();
    // 실행 중에도 입력을 바꿀 수 있게: 이미 읽은 위치는 유지
    if (machine) machine.input = Array.from(inputEl.value, ch => ch.codePointAt(0));
  });

  gridEl.addEventListener('click', e => {
    const el = e.target.closest('.cell');
    if (!el) return;
    const key = `${el.dataset.x},${el.dataset.y}`;
    if (breakpoints.has(key)) breakpoints.delete(key); else breakpoints.add(key);
    el.classList.toggle('bp');
  });
  gridEl.addEventListener('mouseover', e => {
    const el = e.target.closest('.cell');
    if (!el) return;
    const x = +el.dataset.x, y = +el.dataset.y;
    $('cell-info').textContent = `(${x}, ${y}) ${Aheui.describe(machine.cell(x, y))}`;
  });

  document.addEventListener('keydown', e => {
    if (e.key === 'F5' && e.shiftKey) { e.preventDefault(); rebuild(); }
    else if (e.key === 'F5') { e.preventDefault(); run(); }
    else if (e.key === 'F6') { e.preventDefault(); stop(); renderState(); }
    else if (e.key === 'F10') { e.preventDefault(); step(); }
    else if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); runNative(); }
    else if (e.key === 's' && e.ctrlKey) { e.preventDefault(); saveFile(); }
  });

  load();
  rebuild();
  detectNative();
})();
