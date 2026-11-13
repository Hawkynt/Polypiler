#!/usr/bin/env python3
"""Writes docs/optimizations/ from the optimization issues of the backlog.

usage: optimization_docs.py [--repo owner/name]

One page per optimization (what it does, an example before and after, when it applies, when it must
not fire, how it is switched off) and docs/optimizations/README.md, the checklist that links them.
A page that already exists is left alone unless it still carries the "planned" marker: once the
implementing pull request has rewritten a page, this script no longer touches it.
"""
import json
import re
import sys

from common import BACKLOG, REFERENCE, ROOT, load_all

TARGET = ROOT / "docs" / "optimizations"
MARKER = "<!-- status: planned -->"
DOCUMENT = re.compile(r"ID / document:\*\*\s*`(O\d{4})`[^`]*`docs/optimizations/([^`]+\.md)`")
BULLET = re.compile(r"(?m)^- \*\*([^*:]+):\*\*[ \t]*")
HEADINGS = {
  "Before (IR)": "Before",
  "After (IR)": "After",
  "Legality preconditions": "When it applies",
  "Proof class": "How it is proven",
  "Counterexamples": "When it must not fire",
  "Switch": "Switching it off",
  "Interactions": "Interactions with other passes",
}
GROUPS = [
  ("optimize", "OPT-", "General passes"),
  ("optimize", "DYN-", "De-dynamicization"),
  ("optimize", "LEG-", "Legalization"),
  ("recognize", "REC-", "Recognition and recovery"),
  ("permutation", "PERM-", "Bit permutations"),
  ("objects", "OBJ-", "Language-construct lowering and recovery"),
  ("interop", "IOP-", "Interop and layouts"),
  ("borrowed", "OPT-LOOP-", "Loop transformations"),
  ("borrowed", "OPT-MEM-", "Memory and data layout"),
  ("borrowed", "OPT-RED-", "Redundancy"),
  ("borrowed", "OPT-PROP-", "Value propagation and arithmetic rewrites"),
  ("borrowed", "OPT-IPA-", "Interprocedural"),
  ("borrowed", "OPT-VEC-", "Vectorization refinements"),
  ("borrowed", "OPT-LAYOUT-", "Control-flow shaping and block layout"),
  ("borrowed", "OPT-PGO-", "Profile-guided"),
  ("borrowed", "OPT-STR-", "Strings"),
]


def section(body, name):
  match = re.search(rf"(?m)^## {re.escape(name)}\s*$", body)
  if not match:
    return ""
  rest = body[match.end():]
  end = re.search(r"(?m)^## ", rest)
  return rest[:end.start() if end else len(rest)].strip("\n")


def dedent(text):
  lines = text.split("\n")
  return "\n".join(line[2:] if line.startswith("  ") else line for line in lines).strip("\n")


def page(issue, identifier, numbers, repo):
  def link(match):
    name = match.group(1)
    if name in numbers:
      return f"[#{numbers[name]['number']}](https://github.com/{repo}/issues/{numbers[name]['number']})"
    return f"`{name}`"

  title = re.sub(r"^optimization:\s*O\d{4}\s*", "", issue.meta["title"]).strip()
  title = title[0].upper() + title[1:]
  tracked = f" — tracked in {link(re.match('(.*)', issue.id))}" if issue.id in numbers else ""
  parts = [f"# {identifier} — {title}", "",
           MARKER,
           f"> **Status: planned.** Not implemented yet{tracked}. "
           f"Once implemented it is switched off with `--disable-opt {identifier}`.", "",
           "## What it does", "", REFERENCE.sub(link, section(issue.body, "Why")), ""]
  text = section(issue.body, "Optimization")
  pieces = BULLET.split(text)
  example_open = False
  for label, content in zip(pieces[1::2], pieces[2::2]):
    label = label.strip()
    if label == "ID / document":
      continue
    if label in ("Before (IR)", "After (IR)") and not example_open:
      parts += ["## Example", ""]
      example_open = True
    level = "###" if label in ("Before (IR)", "After (IR)") else "##"
    parts += [f"{level} {HEADINGS.get(label, label)}", "", REFERENCE.sub(link, dedent(content)), ""]
  return "\n".join(parts).rstrip() + "\n"


def main(arguments):
  repo = arguments[arguments.index("--repo") + 1] if "--repo" in arguments else "Hawkynt/Polypiler"
  issues, _ = load_all()
  state = BACKLOG / ".published.json"
  numbers = json.loads(state.read_text()) if state.exists() else {}
  TARGET.mkdir(parents=True, exist_ok=True)
  entries = []
  for issue in issues:
    if issue.meta.get("template") != "4-optimization":
      continue
    match = DOCUMENT.search(issue.body)
    if not match:
      print(f"{issue.id}: no 'ID / document' line", file=sys.stderr)
      continue
    identifier, name = match.groups()
    path = TARGET / name
    if not path.exists() or MARKER in path.read_text(encoding="utf-8"):
      path.write_text(page(issue, identifier, numbers, repo), encoding="utf-8")
    done = MARKER not in path.read_text(encoding="utf-8")
    title = re.sub(r"^optimization:\s*O\d{4}\s*", "", issue.meta["title"]).strip()
    title = title[0].upper() + title[1:]
    entries.append((issue, identifier, name, title, done))

  entries.sort(key=lambda entry: entry[1])
  lines = ["# Optimizations", "",
           "Every optimization, recognition rule and lowering rewrite Polypiler plans or has, one page each:",
           "what it does, an example before and after, when it applies, when it must **not** fire, and how",
           "to switch it off (`--disable-opt O####`). A ticked box means it is implemented and verified;",
           "the page then describes what the code does rather than what is planned. The families from",
           "\"Loop transformations\" on are borrowed from PB-Compiler; the [catalog](pb-compiler-catalog.md)",
           "maps every one of its optimization documents to the page here that covers it.", "",
           f"**{sum(1 for entry in entries if entry[4])} of {len(entries)} implemented.**", ""]
  for directory, prefix, heading in GROUPS:
    group = [entry for entry in entries
             if entry[0].path.parent.name == directory and entry[0].id.startswith(prefix)]
    if not group:
      continue
    lines += [f"## {heading}", ""]
    for issue, identifier, name, title, done in group:
      lines.append(f"- [{'x' if done else ' '}] [{identifier}]({name}) — {title} ({issue.meta['milestone']})")
    lines.append("")
  (TARGET / "README.md").write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")
  print(f"{len(entries)} pages, {sum(1 for entry in entries if entry[4])} implemented")
  return 0


if __name__ == "__main__":
  sys.exit(main(sys.argv[1:]))
