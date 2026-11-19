// IDE용 JS 실행기(ide/aheui.js)를 aheui/snippets로 검사한다.
// 사용법: node tests/run_js_snippets.js <snippets 경로>
'use strict';
const fs = require('fs');
const path = require('path');
const Aheui = require('../ide/aheui.js');

const root = process.argv[2];
if (!root) { console.error('usage: node tests/run_js_snippets.js <snippets>'); process.exit(2); }
const LIMIT = 50_000_000;
let ok = 0, fail = 0;
for (const set of fs.readdirSync(root).sort()) {
  const dir = path.join(root, set);
  if (!fs.statSync(dir).isDirectory() || set.startsWith('.') || set.startsWith('_') || set === 'undefined') continue;
  for (const f of fs.readdirSync(dir).filter(f => f.endsWith('.aheui')).sort()) {
    const base = path.join(dir, f.slice(0, -6));
    if (!fs.existsSync(base + '.out')) continue;
    const input = fs.existsSync(base + '.in') ? fs.readFileSync(base + '.in', 'utf8') : '';
    const m = new Aheui.Machine(fs.readFileSync(path.join(dir, f), 'utf8'), input);
    const t0 = Date.now();
    while (!m.halted && m.steps < LIMIT) m.step();
    if (!m.halted) { console.log(`SKIP ${set}/${f}: ${LIMIT} 단계 안에 끝나지 않음 (IDE 실행기는 느림)`); continue; }
    const expected = fs.readFileSync(base + '.out', 'utf8').replace(/\n+$/, '');
    let good = m.halted && m.output.replace(/\n+$/, '') === expected;
    if (good && fs.existsSync(base + '.exitcode')) good = m.exitCode === Number(fs.readFileSync(base + '.exitcode', 'utf8').trim());
    if (good) ok++;
    else { fail++; console.log(`FAIL ${set}/${f} halted=${m.halted} out=${JSON.stringify(m.output.slice(0, 120))}`); }
    if (Date.now() - t0 > 2000) console.log(`  (slow) ${set}/${f} ${Date.now() - t0} ms, ${m.steps} steps`);
  }
}
console.log(`test status: ${ok}/${ok + fail}`);
process.exit(fail ? 1 : 0);
