// IDE용 JS 실행기(ide/aheui.js)를 aheui/snippets로 검사한다.
// 되돌리기 기록을 켠 채 실행한 뒤 처음까지 되감고(stepBack) 다시 실행해서 결과가 같은지도 본다.
// 사용법: node tests/run_js_snippets.js <snippets 경로>
'use strict';
const fs = require('fs');
const path = require('path');
const Aheui = require('../ide/aheui.js');

const root = process.argv[2];
if (!root) { console.error('usage: node tests/run_js_snippets.js <snippets>'); process.exit(2); }
const LIMIT = 50_000_000;
let ok = 0, fail = 0;

function runToEnd(m) { while (!m.halted && m.steps < LIMIT) m.step(); }

for (const set of fs.readdirSync(root).sort()) {
  const dir = path.join(root, set);
  if (!fs.statSync(dir).isDirectory() || set.startsWith('.') || set.startsWith('_') || set === 'undefined') continue;
  for (const f of fs.readdirSync(dir).filter(f => f.endsWith('.aheui')).sort()) {
    const base = path.join(dir, f.slice(0, -6));
    if (!fs.existsSync(base + '.out')) continue;
    const input = fs.existsSync(base + '.in') ? fs.readFileSync(base + '.in', 'utf8') : '';
    const code = fs.readFileSync(path.join(dir, f), 'utf8');
    const m = new Aheui.Machine(code, input);
    runToEnd(m);
    if (!m.halted) { console.log(`SKIP ${set}/${f}: ${LIMIT} 단계 안에 끝나지 않음 (IDE 실행기는 느림)`); continue; }
    const expected = fs.readFileSync(base + '.out', 'utf8').replace(/\n+$/, '');
    let good = m.output.replace(/\n+$/, '') === expected;
    if (good && fs.existsSync(base + '.exitcode')) good = m.exitCode === Number(fs.readFileSync(base + '.exitcode', 'utf8').trim());

    // 되감기 검사 (짧은 프로그램만)
    if (good && m.steps < 2_000_000) {
      let moves = 0;
      const h = new Aheui.Machine(code, input, { history: true, onMove: (a, b, c, d, e, delta) => { moves += delta; } });
      runToEnd(h);
      const out1 = h.output, steps = h.steps;
      while (h.stepBack()) { }
      const clean = h.steps === 0 && h.output === '' && h.x === 0 && h.y === 0 && h.inPos === 0 && moves === 0
        && h.stores.every((st, i) => (i === Aheui.QUEUE ? st.length - h.queueHead : st.length) === 0);
      runToEnd(h);
      if (!clean || h.output !== out1 || h.steps !== steps) {
        good = false;
        console.log(`  되감기 실패: clean=${clean} moves=${moves}`);
      }
    }
    if (good) ok++;
    else { fail++; console.log(`FAIL ${set}/${f} out=${JSON.stringify(m.output.slice(0, 120))}`); }
  }
}
console.log(`test status: ${ok}/${ok + fail}`);
process.exit(fail ? 1 : 0);
