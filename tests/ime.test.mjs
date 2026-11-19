// 두벌식 입력기 검사: node tests/ime.test.mjs
import { HangulIme, jamoForKey } from '../ide/js/ime.js';

function type(keys) {
  // keys: 자모 문자열, '<' = 백스페이스
  const ime = new HangulIme();
  let out = '';
  for (const k of keys) {
    if (k === '<') { if (!ime.backspace()) out = out.slice(0, -1); continue; }
    const c = ime.feed(k);
    if (c) out += c;
  }
  const last = ime.flush();
  return out + (last || '');
}

const cases = [
  ['ㅂㅏㅇ', '방'], ['ㅂㅏㅇㅂㅏㅇ', '방방'], ['ㄷㅏ', '다'], ['ㅁㅏㅇㅎㅢ', '망희'],
  ['ㅎㅡㅣ', '희'], ['ㄱㅏㅂㅅ', '값'], ['ㄱㅏㅂㅅㅣ', '갑시'], ['ㅏ', '아'], ['ㅏㅓ', '아어'],
  ['ㄷㅏㄹㄱ', '닭'], ['ㄷㅏㄹㄱㅏ', '달가'], ['ㅃㅏㄸ', '빠'], ['ㅃㅏㄸㅏ', '빠따'],
  ['ㄱ', ''], ['ㄱㄴㅏ', '나'], ['ㅂㅏㅇ<', '바'], ['ㅂㅏㅇ<<', ''], ['ㅂㅏㅇ<<<', ''],
  ['ㄱㅏㄴㅏ<', '가'], ['ㅂㅏㄹㅂ<', '발'], ['ㅁㅜㅓ', '뭐'],
];
let fail = 0;
for (const [keys, want] of cases) {
  const got = type(keys);
  if (got !== want) { fail++; console.log(`FAIL ${keys}: ${got} != ${want}`); }
}
if (jamoForKey('KeyQ', true) !== 'ㅃ' || jamoForKey('KeyK', false) !== 'ㅏ' || jamoForKey('Digit1', false) !== null) { fail++; console.log('FAIL keymap'); }
console.log(`ime: ${cases.length + 1 - fail}/${cases.length + 1}`);
process.exit(fail ? 1 : 0);
