// 아희 디버깅용 단계 실행기 (IDE 전용).
// 네이티브 인터프리터(src/Aheui)와 같은 의미를 따른다. 값은 BigInt라 범위 제한이 없다.
'use strict';

const Aheui = (() => {
  const CHO = 'ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ';
  const JUNG = 'ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ';
  const JONG = ['', 'ㄱ', 'ㄲ', 'ㄳ', 'ㄴ', 'ㄵ', 'ㄶ', 'ㄷ', 'ㄹ', 'ㄺ', 'ㄻ', 'ㄼ', 'ㄽ', 'ㄾ', 'ㄿ', 'ㅀ',
    'ㅁ', 'ㅂ', 'ㅄ', 'ㅅ', 'ㅆ', 'ㅇ', 'ㅈ', 'ㅊ', 'ㅋ', 'ㅌ', 'ㅍ', 'ㅎ'];
  const STROKES = [0, 2, 4, 4, 2, 5, 5, 3, 5, 7, 9, 9, 7, 9, 9, 8, 4, 4, 6, 2, 4, 0, 3, 4, 3, 4, 4, 0];
  const QUEUE = 21, PIPE = 27;
  // 방향: 하위 2비트 0 위 1 아래 2 왼쪽 3 오른쪽, 4 = 두 칸. 반대 방향 = d ^ 1
  const UP = 0, DOWN = 1, LEFT = 2, RIGHT = 3, FAST = 4;
  const DIR_NAMES = ['↑', '↓', '←', '→', '⇈', '⇊', '⇇', '⇉'];

  function decompose(c) {
    if (c < 0xAC00 || c > 0xD7A3) return null;
    const i = c - 0xAC00;
    return { cho: Math.floor(i / 588), jung: Math.floor(i % 588 / 28), jong: i % 28 };
  }

  function applyVowel(jung, d) {
    switch (jung) {
      case 0: return RIGHT;
      case 2: return RIGHT | FAST;
      case 4: return LEFT;
      case 6: return LEFT | FAST;
      case 8: return UP;
      case 12: return UP | FAST;
      case 13: return DOWN;
      case 17: return DOWN | FAST;
      case 18: return (d & 2) ? d : d ^ 1; // ㅡ
      case 20: return (d & 2) ? d ^ 1 : d; // ㅣ
      case 19: return d ^ 1;               // ㅢ
      default: return d;
    }
  }

  /** 초성별 분류 (색칠/설명용) */
  function category(cho) {
    switch (cho) {
      case 18: return 'halt';
      case 2: case 3: case 4: case 5: case 16: case 12: return 'arith';
      case 6: case 7: case 8: case 17: return 'stack';
      case 9: case 10: return 'storage';
      case 14: return 'branch';
      default: return 'nop';
    }
  }

  function parse(source) {
    const lines = source.replace(/\r\n?/g, '\n').replace(/^﻿/, '').split('\n');
    while (lines.length && lines[lines.length - 1] === '') lines.pop();
    return lines.map(l => Array.from(l, ch => ch.codePointAt(0)));
  }

  class Machine {
    constructor(source, input) {
      this.rows = parse(source);
      this.height = this.rows.length;
      this.input = Array.from(input || '', ch => ch.codePointAt(0));
      this.inPos = 0;
      this.reset();
    }

    reset() {
      this.x = 0; this.y = 0; this.dir = DOWN; this.storage = 0;
      this.stores = Array.from({ length: 28 }, () => []);
      this.queueHead = 0; // 큐는 배열 + 시작 위치로 표현
      this.output = '';
      this.steps = 0;
      this.halted = this.height === 0 || this.rows.every(r => r.length === 0);
      this.exitCode = this.halted ? 0 : null;
      this.error = null;
      this.inPos = 0;
    }

    cell(x, y) {
      const row = this.rows[y];
      return row && x < row.length ? row[x] : 0x20;
    }

    move() {
      const step = (this.dir & FAST) ? 2 : 1;
      switch (this.dir & 3) {
        case UP: this.y -= step; if (this.y < 0) this.y = this.height - 1; break;
        case DOWN: this.y += step; if (this.y >= this.height) this.y = 0; break;
        case LEFT: {
          const w = this.rows[this.y].length;
          if (w) { this.x -= step; if (this.x < 0) this.x = w - 1; }
          break;
        }
        default: {
          const w = this.rows[this.y].length;
          if (w) { this.x += step; if (this.x >= w) this.x = 0; }
        }
      }
    }

    // ── 저장공간 ──
    size(s) { return s === QUEUE ? this.stores[s].length - this.queueHead : this.stores[s].length; }
    push(s, v) { this.stores[s].push(v); }
    pop(s) {
      if (s !== QUEUE) return this.stores[s].pop();
      const q = this.stores[s];
      const v = q[this.queueHead++];
      if (this.queueHead > 1024 && this.queueHead * 2 > q.length) { q.splice(0, this.queueHead); this.queueHead = 0; }
      return v;
    }
    /** 논리 순서 (스택: 바닥→맨 위, 큐: 앞→뒤) */
    items(s) { return s === QUEUE ? this.stores[s].slice(this.queueHead) : this.stores[s]; }

    readNumber() {
      const inp = this.input;
      while (this.inPos < inp.length && [0x20, 9, 10, 13, 11, 12].includes(inp[this.inPos])) this.inPos++;
      let sign = 1n;
      if (inp[this.inPos] === 0x2B || inp[this.inPos] === 0x2D) { if (inp[this.inPos] === 0x2D) sign = -1n; this.inPos++; }
      let digits = '';
      while (this.inPos < inp.length && inp[this.inPos] >= 0x30 && inp[this.inPos] <= 0x39) digits += String.fromCharCode(inp[this.inPos++]);
      return digits ? sign * BigInt(digits) : -1n;
    }

    readChar() {
      return this.inPos < this.input.length ? BigInt(this.input[this.inPos++]) : -1n;
    }

    /** 한 칸 실행. 실행 후 커서는 다음 칸으로 이동해 있다. */
    step() {
      if (this.halted) return false;
      this.steps++;
      const d = decompose(this.cell(this.x, this.y));
      if (d) {
        this.dir = applyVowel(d.jung, this.dir);
        if (!this.exec(d.cho, d.jong)) this.dir ^= 1; // 저장공간 부족 / ㅊ에서 0 → 반대로
        if (this.halted) return false;
      }
      this.move();
      return true;
    }

    /** 명령 실행. false면 커서가 반대 방향으로 간다. */
    exec(cho, jong) {
      const s = this.storage;
      const need = n => this.size(s) >= n;
      switch (cho) {
        case 18: { // ㅎ 끝냄
          const v = need(1) ? this.pop(s) : 0n;
          this.halted = true;
          this.exitCode = Number(BigInt.asIntN(32, v));
          return true;
        }
        case 3: case 4: case 16: case 2: case 5: case 12: { // ㄷ ㄸ ㅌ ㄴ ㄹ ㅈ
          if (!need(2)) return false;
          const a = this.pop(s), b = this.pop(s);
          let r;
          switch (cho) {
            case 3: r = b + a; break;
            case 4: r = b * a; break;
            case 16: r = b - a; break;
            case 12: r = b >= a ? 1n : 0n; break;
            default:
              if (a === 0n) {
                this.error = '0으로 나누었습니다';
                this.halted = true;
                this.exitCode = 1;
                return true;
              }
              r = cho === 2 ? b / a : b % a; // BigInt: 0 쪽으로 버림
          }
          this.push(s, r);
          return true;
        }
        case 6: { // ㅁ 뽑기
          if (!need(1)) return false;
          const v = this.pop(s);
          if (jong === 21) this.output += v.toString();
          else if (jong === 27) {
            const cp = (v >= 0n && v <= 0x10FFFFn && !(v >= 0xD800n && v <= 0xDFFFn)) ? Number(v) : 0xFFFD;
            this.output += String.fromCodePoint(cp);
          }
          return true;
        }
        case 7: { // ㅂ 넣기
          const v = jong === 21 ? this.readNumber() : jong === 27 ? this.readChar() : BigInt(STROKES[jong]);
          this.push(s, v);
          return true;
        }
        case 8: { // ㅃ 중복
          if (!need(1)) return false;
          if (s === QUEUE) {
            const q = this.stores[s];
            const v = q[this.queueHead];
            if (this.queueHead > 0) q[--this.queueHead] = v;
            else q.unshift(v);
          } else {
            const st = this.stores[s];
            st.push(st[st.length - 1]);
          }
          return true;
        }
        case 17: { // ㅍ 바꿔치기
          if (!need(2)) return false;
          const arr = this.stores[s];
          const [i, j] = s === QUEUE ? [this.queueHead, this.queueHead + 1] : [arr.length - 1, arr.length - 2];
          [arr[i], arr[j]] = [arr[j], arr[i]];
          return true;
        }
        case 9: this.storage = jong; return true;           // ㅅ 선택
        case 10: {                                           // ㅆ 이동
          if (!need(1)) return false;
          this.push(jong, this.pop(s));
          return true;
        }
        case 14: {                                           // ㅊ 조건
          if (!need(1)) return false;
          return this.pop(s) !== 0n;
        }
        default: return true;                                // ㅇ ㄱ ㄲ ㅉ ㅋ
      }
    }
  }

  function describe(c) {
    const d = decompose(c);
    if (!d) return c === 0x20 ? '빈칸' : '한글 아님 (아무것도 안 함)';
    const cmd = {
      0: '없음', 1: '없음', 2: 'ㄴ 나누기', 3: 'ㄷ 더하기', 4: 'ㄸ 곱하기', 5: 'ㄹ 나머지', 6: 'ㅁ 뽑기',
      7: 'ㅂ 넣기', 8: 'ㅃ 중복', 9: 'ㅅ 선택', 10: 'ㅆ 이동', 11: '없음', 12: 'ㅈ 비교', 13: '없음',
      14: 'ㅊ 조건', 15: '없음', 16: 'ㅌ 빼기', 17: 'ㅍ 바꿔치기', 18: 'ㅎ 끝냄',
    }[d.cho];
    let arg = '';
    if (d.cho === 6) arg = d.jong === 21 ? ' (숫자 출력)' : d.jong === 27 ? ' (문자 출력)' : '';
    else if (d.cho === 7) arg = d.jong === 21 ? ' (숫자 입력)' : d.jong === 27 ? ' (문자 입력)' : ` ${STROKES[d.jong]}`;
    else if (d.cho === 9 || d.cho === 10) arg = ` → ${JONG[d.jong] || '(없음)'}`;
    return `${CHO[d.cho]}${JUNG[d.jung]}${JONG[d.jong] ? ' ' + JONG[d.jong] : ''} · ${cmd}${arg}`;
  }

  return { Machine, decompose, category, describe, parse, JONG, DIR_NAMES, QUEUE, PIPE };
})();

if (typeof module !== 'undefined') module.exports = Aheui;
