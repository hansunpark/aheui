// 데스크톱 창(WebView2, src/Aheui.Ide)과 주고받는 통로. 브라우저에서 열면 available = false.
const wv = globalThis.chrome && globalThis.chrome.webview;
let seq = 0;
const pending = new Map();

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    const p = pending.get(m && m.id);
    if (!p) return;
    pending.delete(m.id);
    if (m.ok) p.resolve(m.result); else p.reject(new Error(m.error));
  });
}

export const host = {
  available: !!wv,
  call(type, payload = {}) {
    if (!wv) return Promise.reject(new Error('데스크톱 앱에서만 쓸 수 있습니다'));
    const id = ++seq;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      wv.postMessage({ id, type, ...payload });
    });
  },
};

/** 브라우저용 대체: 파일 고르기 */
export function pickFileInBrowser(accept) {
  return new Promise(resolve => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = accept;
    input.onchange = async () => {
      const f = input.files[0];
      resolve(f ? { path: null, name: f.name, content: await f.text() } : null);
    };
    input.click();
  });
}

/** 브라우저용 대체: 내려받기로 저장 */
export function downloadInBrowser(name, content) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([content], { type: 'text/plain;charset=utf-8' }));
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  return { path: null, name };
}
