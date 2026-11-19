// 두벌식 한글 입력기. 운영체제 IME를 쓰지 않고 물리 키(KeyboardEvent.code)를 직접 받아 조합한다.

const CHO = 'ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ';
const JUNG = 'ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ';
const JONG = ['', 'ㄱ', 'ㄲ', 'ㄳ', 'ㄴ', 'ㄵ', 'ㄶ', 'ㄷ', 'ㄹ', 'ㄺ', 'ㄻ', 'ㄼ', 'ㄽ', 'ㄾ', 'ㄿ', 'ㅀ',
  'ㅁ', 'ㅂ', 'ㅄ', 'ㅅ', 'ㅆ', 'ㅇ', 'ㅈ', 'ㅊ', 'ㅋ', 'ㅌ', 'ㅍ', 'ㅎ'];

const VOWEL_COMBO = { 'ㅗㅏ': 'ㅘ', 'ㅗㅐ': 'ㅙ', 'ㅗㅣ': 'ㅚ', 'ㅜㅓ': 'ㅝ', 'ㅜㅔ': 'ㅞ', 'ㅜㅣ': 'ㅟ', 'ㅡㅣ': 'ㅢ' };
const JONG_COMBO = {
  'ㄱㅅ': 'ㄳ', 'ㄴㅈ': 'ㄵ', 'ㄴㅎ': 'ㄶ', 'ㄹㄱ': 'ㄺ', 'ㄹㅁ': 'ㄻ', 'ㄹㅂ': 'ㄼ', 'ㄹㅅ': 'ㄽ',
  'ㄹㅌ': 'ㄾ', 'ㄹㅍ': 'ㄿ', 'ㄹㅎ': 'ㅀ', 'ㅂㅅ': 'ㅄ',
};
const JONG_SPLIT = Object.fromEntries(Object.entries(JONG_COMBO).map(([k, v]) => [v, [k[0], k[1]]]));

/** 두벌식 자판: KeyboardEvent.code → [기본, Shift] */
export const DUBEOLSIK = {
  KeyQ: ['ㅂ', 'ㅃ'], KeyW: ['ㅈ', 'ㅉ'], KeyE: ['ㄷ', 'ㄸ'], KeyR: ['ㄱ', 'ㄲ'], KeyT: ['ㅅ', 'ㅆ'],
  KeyY: ['ㅛ', 'ㅛ'], KeyU: ['ㅕ', 'ㅕ'], KeyI: ['ㅑ', 'ㅑ'], KeyO: ['ㅐ', 'ㅒ'], KeyP: ['ㅔ', 'ㅖ'],
  KeyA: ['ㅁ', 'ㅁ'], KeyS: ['ㄴ', 'ㄴ'], KeyD: ['ㅇ', 'ㅇ'], KeyF: ['ㄹ', 'ㄹ'], KeyG: ['ㅎ', 'ㅎ'],
  KeyH: ['ㅗ', 'ㅗ'], KeyJ: ['ㅓ', 'ㅓ'], KeyK: ['ㅏ', 'ㅏ'], KeyL: ['ㅣ', 'ㅣ'],
  KeyZ: ['ㅋ', 'ㅋ'], KeyX: ['ㅌ', 'ㅌ'], KeyC: ['ㅊ', 'ㅊ'], KeyV: ['ㅍ', 'ㅍ'], KeyB: ['ㅠ', 'ㅠ'],
  KeyN: ['ㅜ', 'ㅜ'], KeyM: ['ㅡ', 'ㅡ'],
};

export function jamoForKey(code, shift) {
  const k = DUBEOLSIK[code];
  return k ? k[shift ? 1 : 0] : null;
}

const isVowel = j => JUNG.includes(j);
const canBeJong = j => JONG.indexOf(j) > 0;

function syllable(cho, jung, jong) {
  return String.fromCodePoint(0xAC00 + (CHO.indexOf(cho) * 21 + JUNG.indexOf(jung)) * 28 + (jong ? JONG.indexOf(jong) : 0));
}

/**
 * 조합 상태: { cho, jung, jong } (각각 자모 문자열 또는 null)
 * - 홀소리만 치면 초성 ㅇ을 자동으로 붙인다 (아희에서 ㅇ은 아무것도 안 하는 초성).
 * - 닿소리만 남은 채 확정되면 버린다 (한 음절이 아니므로).
 */
export class HangulIme {
  constructor() { this.reset(); }

  reset() {
    this.state = null;
    this.undo = []; // 백스페이스용 이전 상태들
  }

  get composing() {
    const s = this.state;
    if (!s) return null;
    if (s.jung) return syllable(s.cho, s.jung, s.jong);
    return s.cho;
  }

  get isComposing() { return this.state !== null; }

  /** 현재 조합을 확정한다. 완성된 음절이면 그 글자, 아니면 null */
  flush() {
    const s = this.state;
    this.reset();
    return s && s.jung ? syllable(s.cho, s.jung, s.jong) : null;
  }

  /**
   * 자모 하나 입력.
   * @returns 확정되어 다음 칸으로 넘어간 글자 (없으면 null)
   */
  feed(j) {
    const s = this.state;
    const v = isVowel(j);
    if (!s) {
      this._set(v ? { cho: 'ㅇ', jung: j, jong: null } : { cho: j, jung: null, jong: null });
      return null;
    }
    if (!s.jung) {
      // 초성만 있음
      if (v) this._set({ ...s, jung: j });
      else { this.undo = []; this._set({ cho: j, jung: null, jong: null }); }
      return null;
    }
    if (!s.jong) {
      if (v) {
        const combo = VOWEL_COMBO[s.jung + j];
        if (combo) { this._set({ ...s, jung: combo }); return null; }
        return this._next(syllable(s.cho, s.jung, null), { cho: 'ㅇ', jung: j, jong: null });
      }
      if (canBeJong(j)) { this._set({ ...s, jong: j }); return null; }
      return this._next(syllable(s.cho, s.jung, null), { cho: j, jung: null, jong: null });
    }
    // 받침까지 있음
    if (v) {
      // 받침을 다음 음절의 초성으로 넘긴다 (겹받침이면 뒤쪽만)
      const split = JONG_SPLIT[s.jong];
      const keep = split ? split[0] : null;
      const moved = split ? split[1] : s.jong;
      return this._next(syllable(s.cho, s.jung, keep), { cho: moved, jung: j, jong: null }, { cho: moved, jung: null, jong: null });
    }
    const combo = JONG_COMBO[s.jong + j];
    if (combo) { this._set({ ...s, jong: combo }); return null; }
    return this._next(syllable(s.cho, s.jung, s.jong), { cho: j, jung: null, jong: null });
  }

  /** 백스페이스: 조합 중이면 한 단계 되돌리고 true, 조합 중이 아니면 false */
  backspace() {
    if (!this.state) return false;
    this.state = this.undo.pop() || null;
    return true;
  }

  _set(next) {
    this.undo.push(this.state);
    this.state = next;
  }

  _next(committed, next, intermediate) {
    this.undo = intermediate ? [null, intermediate] : [null];
    this.state = next;
    return committed;
  }
}
