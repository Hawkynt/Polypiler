"""Shared loading of backlog issue files (front matter + body)."""
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[2]
BACKLOG = ROOT / "backlog"
REFERENCE = re.compile(r"\[\[([A-Z0-9][A-Z0-9+\-]*)\]\]")


class Issue:
  def __init__(self, path, meta, body):
    self.path, self.meta, self.body = path, meta, body
    self.id = meta.get("id", "")

  def list(self, key):
    value = self.meta.get(key, [])
    return value if isinstance(value, list) else [value]


def _scalar(text):
  text = text.strip()
  if len(text) >= 2 and text[0] == text[-1] and text[0] in "\"'":
    return text[1:-1]
  return text


def _value(text):
  text = text.split(" #", 1)[0].strip() if not text.strip().startswith(("\"", "'")) else text.strip()
  if text.startswith("[") and text.endswith("]"):
    inner = text[1:-1].strip()
    return [_scalar(part) for part in inner.split(",")] if inner else []
  return _scalar(text)


def load(path):
  text = path.read_text(encoding="utf-8")
  if not text.startswith("---\n"):
    raise ValueError("missing front matter")
  end = text.index("\n---\n", 4)
  meta = {}
  for line in text[4:end].splitlines():
    if not line.strip():
      continue
    key, separator, rest = line.partition(":")
    if not separator:
      raise ValueError(f"bad front matter line: {line}")
    meta[key.strip()] = _value(rest)
  return Issue(path, meta, text[end + 5:].lstrip("\n"))


def load_all(paths=None):
  files = []
  for base in (paths or [BACKLOG]):
    base = pathlib.Path(base)
    files += [base] if base.is_file() else sorted(base.rglob("*.md"))
  issues, errors = [], []
  for file in files:
    if file.name == "README.md":
      continue
    try:
      issues.append(load(file))
    except Exception as error:  # reported by the caller
      errors.append((file, str(error)))
  return issues, errors
