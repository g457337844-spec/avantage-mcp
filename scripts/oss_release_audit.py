"""Prepare an audited, history-free filesystem snapshot; does not publish to GitHub."""
from __future__ import annotations

import argparse
import fnmatch
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "oss-release.json"
BLOCK_EXT = {".exe", ".dll", ".pdb", ".zip", ".pem", ".key", ".pfx",
             ".p12", ".opj", ".opju", ".vgd", ".vgt", ".vgp", ".vgx"}
BINARY_EXT = {".png", ".ico", ".jpg", ".jpeg", ".webp", ".gif"}
RULES = [
    ("private-key", re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", re.I)),
    ("access-token", re.compile(r"(?:gh[pousr]_[A-Za-z0-9]{25,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{24,})")),
    ("user-profile-path", re.compile(r"(?:[A-Z]:[\\\/]+Users[\\\/]+[^\\\/\s\"']+|[A-Z]:[\\\/]+Desktop[\\\/]+|[A-Z]:[\\\/]+桌面[\\\/]+|[A-Z]:[\\\/]+小图[\\\/]+|[A-Z]:[\\\/]+文件[\\\/]+)", re.I)),
    ("user-email", re.compile(r"[A-Z0-9._%+-]+@(?:gmail|qq|163|outlook|hotmail)\.com", re.I)),
]

def allowed(name: str, config: dict) -> bool:
    includes = config["include"]
    excludes = config["exclude"]
    selected = any(name == p or name.startswith(p.rstrip("/") + "/") for p in includes)
    rejected = any(fnmatch.fnmatchcase(name, pat) or name == pat
                   or name.startswith(pat.rstrip("/") + "/") for pat in excludes)
    return selected and not rejected

def tracked_files() -> list[str]:
    data = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT)
    return sorted(p.decode("utf-8") for p in data.split(b"\0") if p)

def inspect(config: dict) -> tuple[list[Path], list[dict]]:
    violations: list[dict] = []
    files: list[Path] = []
    selected = [p for p in tracked_files() if allowed(p, config)]
    if not selected:
        violations.append({"file": "<manifest>", "issue": "empty-export"})
    if "LICENSE" not in selected:
        violations.append({"file": "LICENSE", "issue": "missing-approved-license"})
    for name in selected:
        source = ROOT / name
        if source.is_symlink() or not source.is_file():
            violations.append({"file": name, "issue": "symlink-or-missing-file"})
            continue
        if source.suffix.lower() in BLOCK_EXT:
            violations.append({"file": name, "issue": "blocked-binary-or-dataset"})
            continue
        files.append(source)
        if source.suffix.lower() in BINARY_EXT:
            continue
        try:
            text = source.read_text(encoding="utf-8")
        except UnicodeError:
            violations.append({"file": name, "issue": "not-utf8-or-unknown-binary"})
            continue
        for label, pattern in RULES:
            matches = list(pattern.finditer(text))
            if matches:
                violations.append({
                    "file": name,
                    "issue": label,
                    "lines": sorted({text.count("\n", 0, m.start()) + 1 for m in matches})[:12]
                })
    for name in config.get("manual_review", []):
        if name in selected:
            violations.append({"file": name, "issue": "requires-third-party-rights-review"})
    return files, violations

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export", type=Path, help="New empty destination outside repository (no .git/history)")
    args = parser.parse_args()
    config = json.loads(MANIFEST.read_text(encoding="utf-8"))
    files, violations = inspect(config)
    print(json.dumps({"project": config.get("project"), "files_selected": len(files),
                      "status": "BLOCKED" if violations else "READY_FOR_MANUAL_REVIEW",
                      "findings": violations}, ensure_ascii=False, indent=2))
    if violations:
        return 2
    if args.export:
        target = args.export.expanduser().resolve()
        if target == ROOT or ROOT in target.parents or target.exists():
            raise SystemExit("Export destination must be a new directory outside the private repository.")
        for source in files:
            destination = target / source.relative_to(ROOT)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
        print("Created a source-only snapshot without .git history:", target)
        print("Review the snapshot before creating a NEW public GitHub repository.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
