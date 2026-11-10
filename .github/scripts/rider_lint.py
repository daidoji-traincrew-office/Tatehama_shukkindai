#!/usr/bin/env python3
"""ReSharper CLI (jb inspectcode / cleanupcode) で、変更した .cs の変更行だけを検査する。標準ライブラリのみ。

  rider_lint.py check --sln X.sln [--exclude d/]... [--base REF] [--fix] [--format text|github]   (CI・手動実行)
  rider_lint.py hook  --sln X.sln [--exclude d/]...                                               (Claude Code フック。stdinのJSONを読む)

check の終了コード: 指摘あり=1 / なし=0 / ツール失敗=2。
前提: sln は gitルート直下にある(SARIFのuri=slnからの相対パス を gitルート相対パスとして突き合わせる)。
      `dotnet jb` は呼び出し側リポジトリの .config/dotnet-tools.json で解決される。

hook が依存する Claude Code フック仕様 (変わったらここを見直す):
  - stdin JSON: hook_event_name ("PostToolUse" / "Stop" / "SubagentStop"), session_id,
    stop_hook_active (Stop系で、差し戻し後の再実行なら true), tool_input.file_path (PostToolUse。絶対パス),
    agent_id (サブエージェント内のイベントにだけ付く。.touched をエージェント単位に分けるのに使う)
  - settings.json の PostToolUse matcher は "Edit|Write|MultiEdit"
  - exit 2 + stderr = Stop を差し戻して stderr を Claude に見せる。stdout の {"systemMessage": ...} = ユーザーへの通知
  - fail-open: exit 2 を返すのは「指摘あり かつ stop_hook_active=false」のときだけ。想定外は全て exit 0。
"""
import argparse
import contextlib
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from contextlib import contextmanager
from pathlib import Path
from urllib.parse import unquote

MAX_AGE = 14 * 24 * 3600
JB_TIMEOUT = 270  # hook でだけ使う制限秒数 (settings.json のフック timeout 300 より短くする。cleanupcode+inspectcode の合計)。check (CI) には適用しない


class LintError(Exception):
    pass


def git(*args, root=None):
    return subprocess.run(["git", "-c", "core.quotePath=false", *args], cwd=root, capture_output=True,
                          text=True, encoding="utf-8", check=True).stdout


def git_root(start="."):
    return git("rev-parse", "--show-toplevel", root=start).strip()


def split_z(out):
    return [p for p in out.split("\0") if p]


def excluded(path, excludes):
    return any(path.startswith(e.rstrip("/") + "/") for e in excludes if e.rstrip("/"))


def untracked_cs():
    return set(split_z(git("ls-files", "-z", "--others", "--exclude-standard", "--", "*.cs")))


def changed_files(base, excludes):
    """(対象ファイル一覧, 未追跡ファイルの集合)。-z: 日本語・スペース入りパスを扱うため。"""
    untracked = untracked_cs()
    paths = split_z(git("diff", "-z", "--name-only", "--diff-filter=d", base, "--", "*.cs")) + sorted(untracked)
    return [p for p in paths if os.path.isfile(p) and not excluded(p, excludes)], untracked


def changed_lines(base):
    """{path: set(行番号)}。git diff -U0 のhunkの新側の行範囲。"""
    out = git("diff", "-U0", "--no-color", "--no-ext-diff", "--no-prefix", base, "--", "*.cs")
    result, cur, prev = {}, None, ""
    for line in out.splitlines():
        header = line.startswith("+++ ") and prev.startswith("--- ")  # 直前が '--- ' のときだけファイルヘッダ
        prev = line
        if header:
            p = line[4:].rstrip("\t")
            cur = None if p == "/dev/null" else p
            if cur is not None:
                result.setdefault(cur, set())
        elif line.startswith("@@") and cur is not None:
            m = re.match(r"@@ -\S+ \+(\d+)(?:,(\d+))? @@", line)
            if m:
                start, count = int(m.group(1)), int(m.group(2) or 1)
                result[cur].update(range(start, start + count))
    return result


def cache_root():
    return Path(os.environ.get("RIDER_LINT_CACHES") or Path.home() / ".cache" / "rider-lint")


def caches_dir(sln):
    """設定ファイルの内容ハッシュでキー付けする(設定変更後に古い結果を返さないため)。"""
    h = hashlib.sha256()
    for f in [".editorconfig", *sorted(Path().glob("*.sln*.DotSettings")), ".config/dotnet-tools.json"]:
        with contextlib.suppress(OSError):
            h.update(Path(f).read_bytes())
    h.update(sln.encode())
    root, key = cache_root().resolve(), h.hexdigest()[:12]
    d = root / key
    d.mkdir(parents=True, exist_ok=True)
    d.touch()
    if root not in (Path("/"), Path.home().resolve()):  # 誤設定で危険な場所を消さない
        for old in root.iterdir():  # 14日より古い他キーだけ削除(他リポジトリ/worktreeの並行実行を壊さない)
            if old.is_dir() and old.name != key and time.time() - old.stat().st_mtime > MAX_AGE:
                shutil.rmtree(old, ignore_errors=True)
    return d


def jb(args, log, deadline):
    remaining = None if deadline is None else deadline - time.monotonic()
    if remaining is not None and remaining <= 0:
        raise LintError(f"{log} skipped: {JB_TIMEOUT}秒の制限を超えました")
    try:
        r = subprocess.run(["dotnet", "jb", *args], capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=remaining)
    except subprocess.TimeoutExpired:
        raise LintError(f"{log} timed out ({JB_TIMEOUT}秒の制限)") from None
    except OSError as e:
        raise LintError(f"{log} を起動できません (dotnet がありませんか?): {e}") from e
    if r.returncode != 0:
        lines = [l for l in (r.stdout + r.stderr).splitlines() if not l.lstrip().startswith("at ")]
        raise LintError(f"{log} failed:\n" + "\n".join(lines[-20:]))


def run_jb(sln, files, fix_files, caches, sarif, timeout=None):
    deadline = None if timeout is None else time.monotonic() + timeout
    common = [f"--caches-home={caches}", "--no-build"]
    if fix_files:
        jb(["cleanupcode", sln, "--profile=ClaudeCleanup", f"--include={';'.join(fix_files)}", *common],
           "cleanupcode", deadline)
    jb(["inspectcode", sln, f"--include={';'.join(files)}", "--severity=SUGGESTION", "--format=Sarif",
        f"--output={sarif}", *common], "inspectcode", deadline)


def filter_sarif(sarif, changed, untracked):
    """変更行(未追跡は全行)に入る指摘 [(path, line, rule, msg)] を返す。"""
    found, seen_uri, any_result = set(), False, False
    for run in json.loads(Path(sarif).read_text(encoding="utf-8")).get("runs", []):
        for r in run.get("results", []):
            any_result = True
            for loc in r.get("locations", []):
                pl = loc.get("physicalLocation", {})
                path = unquote(pl.get("artifactLocation", {}).get("uri", "")).replace("\\", "/")
                line = pl.get("region", {}).get("startLine")
                if not path or line is None:
                    continue
                seen_uri = seen_uri or os.path.isfile(path)
                if path in untracked or line in changed.get(path, ()):
                    found.add((path, line, r.get("ruleId", "?"), r.get("message", {}).get("text", "")))
    if any_result and not seen_uri:
        print("rider_lint: 警告: SARIFのuriが gitルート上のファイルに一致しません(slnがリポジトリ直下にない?)。"
              "指摘が0件に見えている可能性があります。", file=sys.stderr)
    return sorted(found)


def lint(sln, files, fix_files, untracked, base, timeout=None):
    """(指摘一覧, 自動修正でファイルが変わったか)。ツール失敗は LintError。"""
    # --include は ';' 区切りなので、';' を含むパスは渡せない
    for f in files:
        if ";" in f:
            print(f"rider_lint: ';' を含むパスは検査できないため除外: {f}", file=sys.stderr)
    files, fix_files = [f for f in files if ";" not in f], [f for f in fix_files if ";" not in f]
    if not files:
        return [], False
    def digest():
        return [hashlib.sha256(Path(f).read_bytes()).digest() for f in fix_files]
    before = digest()
    with tempfile.TemporaryDirectory() as tmp:
        sarif = Path(tmp) / "result.sarif"
        run_jb(sln, files, fix_files, caches_dir(sln), sarif, timeout)
        found = filter_sarif(sarif, changed_lines(base), untracked)
    return found, digest() != before


def esc(s, prop=False):
    s = s.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    return s.replace(":", "%3A").replace(",", "%2C") if prop else s


def fmt_finding(f, fmt="text"):
    path, line, rule, msg = f
    msg = msg.replace("\n", " ")
    if fmt == "github":
        return f"::warning file={esc(path, True)},line={line}::{esc(f'[{rule}] {msg}')}"
    return f"{path}:{line}: [{rule}] {msg}"


def cmd_check(a):
    files, untracked = changed_files(a.base, a.exclude)
    if not files:
        return 0
    try:
        found, _ = lint(a.sln, files, files if a.fix else [], untracked, a.base)
    except LintError as e:
        print(e, file=sys.stderr)
        return 2
    for f in found:
        print(fmt_finding(f, a.format))
    return 1 if found else 0


# ---- hook ----

def say(msg):
    print(json.dumps({"systemMessage": msg}, ensure_ascii=False))


@contextmanager
def flock(path):
    import fcntl  # Windows では存在しない。hook 経路でだけ必要
    with open(path, "w") as f:
        fcntl.flock(f, fcntl.LOCK_EX)
        yield


def read_names(touched):
    """.touched の記録を重複なしで読む。"""
    if not touched.exists():
        return []
    return list(dict.fromkeys(touched.read_text(encoding="utf-8").splitlines()))


def write_names(touched, names):
    touched.write_text("".join(n + "\n" for n in names), encoding="utf-8")


def sanitize(s):
    return re.sub(r"[^A-Za-z0-9_-]", "_", str(s or ""))


def cmd_hook(a):
    data = json.load(sys.stdin)
    event = data.get("hook_event_name")
    if event not in ("PostToolUse", "Stop", "SubagentStop"):
        return 0
    root = git_root(os.environ.get("CLAUDE_PROJECT_DIR") or ".")
    os.chdir(root)
    session = sanitize(data.get("session_id")) or "nosession"
    # .touched はエージェント単位。PostToolUse に agent_id が来ない仕様だった場合は、サブエージェントの編集が
    # メインの .touched に入ってメインの Stop で検査され、SubagentStop は空なので何もしない(どちらでも安全側)。
    agent = sanitize(data.get("agent_id"))
    state = cache_root()
    state.mkdir(parents=True, exist_ok=True)
    for pattern in ("*.touched", "*.lock"):
        for f in state.glob(pattern):
            with contextlib.suppress(OSError):
                if f.name != "jb.lock" and time.time() - f.stat().st_mtime > MAX_AGE:
                    f.unlink(missing_ok=True)
    touched = state / (f"{session}_{agent}.touched" if agent else f"{session}.touched")
    state_lock = state / f"{session}.lock"  # .touched の読み書きだけを守る短いロック
    if event == "PostToolUse":
        with flock(state_lock):
            return record(data, root, touched, a.exclude)
    return on_stop(a, bool(data.get("stop_hook_active")), touched, state_lock, state / "jb.lock")


def record(data, root, touched, excludes):
    path = (data.get("tool_input") or {}).get("file_path")
    if not path or not path.endswith(".cs"):
        return 0
    real_root = os.path.realpath(root)
    rel = os.path.relpath(os.path.realpath(path), real_root)
    if rel.startswith("..") or excluded(rel, excludes):
        return 0
    if rel not in read_names(touched):
        with open(touched, "a", encoding="utf-8") as f:
            f.write(rel + "\n")
    return 0


def on_stop(a, active, touched, state_lock, jb_lock):
    with flock(state_lock):
        names = read_names(touched)
    if not names:
        return 0

    def consume():  # 今回処理した分だけ除く(jb 実行中に PostToolUse が追記した分は残す)
        with flock(state_lock):
            write_names(touched, [n for n in read_names(touched) if n not in names])

    files = [n for n in names if os.path.isfile(n) and not excluded(n, a.exclude)]
    if not files:
        consume()
        return 0
    try:
        with flock(jb_lock):  # jb とキャッシュの同時実行を防ぐ(全セッション共通)
            found, fixed = lint(a.sln, files, files, untracked_cs() & set(files), "HEAD", timeout=JB_TIMEOUT)
    except LintError as e:
        consume()  # 同じ内容では再実行しない
        say(f"rider_lint 実行失敗(この内容では再実行しません): {e}")
        return 0
    out = "\n".join(fmt_finding(f) for f in found)
    if not found:
        consume()
        if fixed:
            say("Rider検査: 指摘は cleanupcode による自動修正で解消しました(ファイルが書き換わっています)。")
        return 0
    if active:  # 差し戻しは1回まで
        consume()
        say("Rider検査の指摘が残っています(差し戻し済みのため終了しました):\n" + out)
        return 0
    note = "(機械的に直せるものは cleanupcode で自動修正済みです。ファイルが書き換わっているので、編集前に必ず再Readしてください)\n" if fixed else ""
    print(f"Rider(ReSharper CLI)の検査で、あなたが変更した行に指摘があります。修正してください。\n{note}\n{out}", file=sys.stderr)
    return 2


def main():
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("check", "hook"):
        p = sub.add_parser(name)
        p.add_argument("--sln", required=True)
        p.add_argument("--exclude", action="append", default=[])
        if name == "check":
            p.add_argument("--base", default="HEAD")
            p.add_argument("--fix", action="store_true")
            p.add_argument("--format", choices=["text", "github"], default="text")
    a = ap.parse_args()
    try:
        if a.cmd == "check":
            os.chdir(git_root())
            return cmd_check(a)
        return cmd_hook(a)
    except Exception as e:  # noqa: BLE001
        print(f"rider_lint {a.cmd}: {e!r}", file=sys.stderr)
        return 2 if a.cmd == "check" else 0  # hook は fail-open: フックの不具合で作業を止めない


if __name__ == "__main__":
    sys.exit(main())
