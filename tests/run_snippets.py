"""aheui/snippets 테스트 모음 실행기 (test.sh와 같은 판정 규칙, 표준 라이브러리만 사용).

사용법:
    uv run --no-project python tests/run_snippets.py <snippets 경로> [--exe 경로] [--flags "--no-opt"] [세트 ...]

snippets 저장소: https://github.com/aheui/snippets
"""
import argparse
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_EXE = ROOT / "src" / "Aheui" / "bin" / "Release" / "net10.0" / "aheui.exe"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("snippets", type=pathlib.Path)
    ap.add_argument("sets", nargs="*", help="실행할 세트 (기본: 전부, 'undefined' 제외)")
    ap.add_argument("--exe", type=pathlib.Path, default=DEFAULT_EXE)
    ap.add_argument("--flags", default="")
    ap.add_argument("--timeout", type=float, default=30)
    args = ap.parse_args()

    sets = args.sets or sorted(
        d.name for d in args.snippets.iterdir()
        if d.is_dir() and not d.name.startswith((".", "_")) and d.name != "undefined"
    )
    ok = fail = 0
    for name in sets:
        d = args.snippets / name
        print(f"testset: {name}")
        for f in sorted(d.glob("*.aheui")):
            out_file = f.with_suffix(".out")
            if not out_file.exists():
                continue
            in_file = f.with_suffix(".in")
            stdin = in_file.read_bytes() if in_file.exists() else b""
            cmd = [str(args.exe), *args.flags.split(), str(f)]
            t0 = time.perf_counter()
            try:
                p = subprocess.run(cmd, input=stdin, capture_output=True, timeout=args.timeout)
            except subprocess.TimeoutExpired:
                fail += 1
                print(f"  FAIL {f.stem}: timeout")
                continue
            dt = (time.perf_counter() - t0) * 1000
            # test.sh는 $(cat ...)로 비교하므로 끝 개행은 무시된다
            actual = p.stdout.rstrip(b"\n")
            expected = out_file.read_bytes().rstrip(b"\n")
            good = actual == expected
            code_file = f.with_suffix(".exitcode")
            if good and code_file.exists():
                good = p.returncode == int(code_file.read_text().strip())
            if good:
                ok += 1
                print(f"  ok   {f.stem} ({dt:.0f} ms)")
            else:
                fail += 1
                print(f"  FAIL {f.stem} exit={p.returncode}")
                print(f"       expected: {expected[:200]!r}")
                print(f"       actual:   {actual[:200]!r}")
                if p.stderr:
                    print(f"       stderr:   {p.stderr[:300]!r}")
    print(f"test status: {ok}/{ok + fail}")
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
