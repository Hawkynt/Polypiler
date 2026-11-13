#!/usr/bin/env python3
"""Checks backlog issue files against ISSUES.md and backlog/README.md.

usage: lint.py [--assume-anchors] [path ...]     (default: the whole backlog)

--assume-anchors treats the anchor and language-tree IDs fixed in backlog/README.md as existing, so
one area can be checked before the others are written.

References are always resolved against the whole backlog, so a single area can be linted alone.
"""
import pathlib
import re
import subprocess
import sys

from common import BACKLOG, REFERENCE, ROOT, load_all

SCOPES = ("lang", "frontend", "backend", "isa", "format", "optimization", "analysis", "ir", "types", "intrinsics",
          "legalize", "interop", "objects", "permutation", "runtime", "verify", "catalog", "package", "adapter",
          "upstream", "e2e", "cli", "infra", "spec", "spike", "bug")
TYPES = {"enhancement", "bug", "documentation"}
AREAS = {"area:" + name for name in ("infrastructure", "ir", "analysis", "frontend", "backend", "recovery", "bitperm",
                                     "interop", "catalog", "cli", "verification", "retro", "object-model", "upstream")}
COMMON = ["Why", "User stories", "Scope", "Preconditions and inputs", "Acceptance criteria", "Test design",
          "Measurements", "Evidence to attach", "Implementation plan", "Target state", "Definition of Ready"]
SECTIONS = {
  "1-epic": ["Why", "User stories", "Scope", "Sub-issues", "Done when", "Target state"],
  "2-frontend": COMMON + ["Language profile", "Construct table", "Semantic traps", "Specification"],
  "3-backend": COMMON + ["Target profile", "Lowering table", "Runtime helpers", "Specification"],
  "4-optimization": COMMON + ["Optimization", "Specification"],
  "5-e2e": COMMON + ["Translation"],
  "6-isa-format": COMMON + ["Format / ISA", "Specification"],
  "7-feature": COMMON + ["Specification"],
  "8-spike": ["Question", "Why now", "Time box", "Options", "Deciding evidence", "Deliverable"],
}
STATES = ("parsed", "lifted", "recognized", "proved", "legalized", "emitted", "compiled", "executed", "verified",
          "performance-validated")
PINS = {
  "Hawkynt/PB-Compiler": ("0d73acbea185a2597357d53f7ce9acb44758ba76", "PB-Compiler.Hawkynt"),
  "Hawkynt/Hawkynt.github.io": ("045397b4c934487bc2eb385125fdbd9589502f7f", "Hawkynt.github.io"),
  "Hawkynt/MediaDroid": ("7f8b6b995cbe54a6afb290a900748393bf01e272", "MediaDroyd.Hawkynt"),
  "Hawkynt/CompressionWorkbench": ("21e609396e8fceb80e49185cc34cab7bce7f6483", "CompressionWorkbench.Hawkynt"),
  "Hawkynt/PNGCrushCS": ("9d8117695e12457bf0c1158d1fc6ebccc53acc45", "PNGCrushCS.Hawkynt"),
  "Hawkynt/Randomizer": ("656a1560350f1551de65c1e4bab179e3b048e282", None),
  "Hawkynt/C--FrameworkExtensions": ("be3e13cbbe28dd54cc973c64b84cd71e1147f95f", None),
}
FIXTURE = re.compile(r"(Hawkynt/[A-Za-z0-9.\-]+)@([0-9a-f]{7,40}):([^\s`|)]+)")
PLACEHOLDER = re.compile(r"<[^<>\n`]*…[^<>\n`]*>|\bTBD\b|\bTODO\b")
OPT_ID = re.compile(r"\bO\d{4}\b")


def sections(body):
  return {match.group(1).strip(): match.start() for match in re.finditer(r"(?m)^## (.+)$", body)}


def section_text(body, name):
  found = sections(body)
  if name not in found:
    return ""
  start = found[name]
  later = [position for position in found.values() if position > start]
  return body[start:min(later) if later else len(body)]


def path_exists_at_pin(checkout, sha, path):
  repo = ROOT.parent / checkout
  if not repo.is_dir():
    return None
  result = subprocess.run(["git", "-C", str(repo), "cat-file", "-e", f"{sha}:{path}"], capture_output=True)
  return result.returncode == 0


def check(issue, by_id, problems):
  def fail(message):
    problems.append(f"{issue.path.relative_to(ROOT)}: {message}")

  meta, body = issue.meta, issue.body
  for key in ("id", "title", "template", "labels", "milestone", "state"):
    if key not in meta:
      fail(f"front matter lacks '{key}'")
  if issue.id != issue.path.stem:
    fail(f"id '{issue.id}' does not match the file name")
  title = str(meta.get("title", ""))
  scope = re.match(r"^([a-z0-9]+)(\([^)]+\))?: \S", title)
  if not scope or scope.group(1) not in SCOPES:
    fail(f"title does not follow '<scope>: <outcome>': {title}")
  template = meta.get("template", "")
  if template not in SECTIONS:
    fail(f"unknown template '{template}'")
    return
  labels = set(issue.list("labels"))
  if len(labels & TYPES) != 1:
    fail("exactly one of enhancement/bug/documentation is required")
  for label in labels - TYPES - AREAS:
    fail(f"unknown label '{label}'")
  if not labels & AREAS:
    fail("at least one area:* label is required")
  if meta.get("milestone") not in ("P0", "P1", "P2", "P3", "P4", "P5", "P6"):
    fail(f"milestone must be P0..P6, is '{meta.get('milestone')}'")
  state = meta.get("state")
  if state not in ("ready", "draft"):
    fail(f"state must be ready or draft, is '{state}'")

  depends = issue.list("depends")
  for target in depends + ([meta["parent"]] if meta.get("parent") else []):
    if target not in by_id:
      fail(f"unknown issue '{target}' in depends/parent")
  if issue.id in depends:
    fail("depends on itself")
  referenced = set(REFERENCE.findall(body))
  for target in sorted(referenced - set(by_id)):
    fail(f"body references unknown issue [[{target}]]")
  for target in depends:
    if target not in referenced:
      fail(f"dependency {target} is not mentioned as [[{target}]] in the body")
  parent = meta.get("parent")
  if parent in by_id:
    if by_id[parent].meta.get("template") == "promised":
      pass
    elif by_id[parent].meta.get("template") != "1-epic":
      fail(f"parent {parent} is not an epic")
    elif issue.id not in REFERENCE.findall(section_text(by_id[parent].body, "Sub-issues")):
      fail(f"parent {parent} does not list this issue under Sub-issues")

  present = sections(body)
  for name in SECTIONS[template]:
    if name not in present:
      fail(f"section '## {name}' is missing")
  for match in PLACEHOLDER.finditer(body):
    fail(f"placeholder left in body: {match.group(0)[:40]}")
  if "\n## Target state" in body and not any(word in section_text(body, "Target state") for word in STATES):
    fail("Target state names none of the PRD §23.6 states")
  if "As a **" not in section_text(body, "User stories") and template != "8-spike":
    fail("no user story in the form 'As a **role**, I want …'")

  for repo, sha, path in FIXTURE.findall(body):
    if repo not in PINS:
      fail(f"fixture repository {repo} has no pin in backlog/README.md")
      continue
    pin, checkout = PINS[repo]
    if sha != pin:
      fail(f"fixture {repo}@{sha[:10]} does not use the pinned commit {pin[:10]}")
    elif checkout and path_exists_at_pin(checkout, sha, path.rstrip(".,;")) is False:
      fail(f"fixture path does not exist at the pin: {repo}:{path}")

  if template == "1-epic":
    children = REFERENCE.findall(section_text(body, "Sub-issues"))
    if len(children) < 2:
      fail("an epic needs at least two sub-issues")
    for child in children:
      if child in by_id and by_id[child].meta.get("template") != "promised" and by_id[child].meta.get("parent") != issue.id:
        fail(f"sub-issue {child} does not name this epic as parent")
    return
  if template == "8-spike":
    return

  if template == "4-optimization" and not OPT_ID.search(title):
    fail("optimization title carries no O#### id")
  if state == "draft":
    if "Blocking questions" not in present:
      fail("a draft needs a '## Blocking questions' section")
    return

  gherkin = section_text(body, "Acceptance criteria")
  if len(re.findall(r"(?m)^\s*Scenario( Outline)?:", gherkin)) < 2:
    fail("fewer than two Gherkin scenarios (one positive, one negative)")
  design = section_text(body, "Test design")
  for technique in ("Happy path", "Exception", "Boundary", "Equivalence"):
    if technique not in design:
      fail(f"test design lacks a '{technique}' row")
  measurements = section_text(body, "Measurements")
  if "Line coverage" not in measurements or "Branch coverage" not in measurements or "95" not in measurements:
    fail("measurements lack the 95 % line and branch coverage rows")
  scope_text = section_text(body, "Scope")
  if "**In scope**" not in scope_text or "**Out of scope**" not in scope_text:
    fail("scope needs both an In scope and an Out of scope list")
  ready = section_text(body, "Definition of Ready")
  if "- [ ]" in ready or ready.count("- [x]") < 10:
    fail("state is ready but the Definition of Ready checklist is not fully ticked")


def find_cycles(by_id, problems):
  colour = {}

  def visit(node, trail):
    colour[node] = 1
    for target in by_id[node].list("depends"):
      if by_id.get(target) is not None and by_id[target].meta.get("template") == "promised":
        continue
      if target not in by_id:
        continue
      if colour.get(target) == 1:
        problems.append("dependency cycle: " + " -> ".join(trail + [node, target]))
      elif target not in colour:
        visit(target, trail + [node])
    colour[node] = 2

  for node in by_id:
    if node not in colour:
      visit(node, [])


LANGUAGES = ("CS", "JS", "PB", "C", "CPP", "RS", "KT", "PL", "PY", "TS", "JAVA", "GO", "DELPHI", "FB", "PHP", "RB", "VB", "CBM")
FAMILIES = ("SCALARS", "CONTROL", "MEMORY", "FUNCTIONS", "TYPES")


def promised_ids():
  """IDs backlog/README.md fixes in advance: the anchors and the language trees."""
  readme = (BACKLOG / "README.md").read_text(encoding="utf-8")
  table = readme[readme.index("## Areas, ID prefixes and anchor issues"):]
  promised = {name for name in re.findall(r"`([A-Z][A-Z0-9\-]*[A-Z0-9])`", table) if "-" in name}
  for code in LANGUAGES:
    promised |= {f"LANG-{code}", f"RUN-{code}", f"FE-{code}", f"BE-{code}"}
    promised |= {f"{side}-{code}-{family}" for side in ("FE", "BE") for family in FAMILIES}
  return promised


class Promised:
  """Stands in for an anchor issue another area has not written yet (--assume-anchors)."""
  def __init__(self, identifier):
    self.id, self.meta, self.body = identifier, {"template": "promised"}, ""

  def list(self, key):
    return []


def main(arguments):
  assume = "--assume-anchors" in arguments
  arguments = [argument for argument in arguments if argument != "--assume-anchors"]
  everything, load_errors = load_all()
  by_id, problems = {}, [f"{path.relative_to(ROOT)}: {error}" for path, error in load_errors]
  optimization_ids = {}
  for issue in everything:
    if issue.id in by_id:
      problems.append(f"duplicate id {issue.id}: {issue.path.relative_to(ROOT)} and {by_id[issue.id].path.relative_to(ROOT)}")
    by_id[issue.id] = issue
    if issue.meta.get("template") == "4-optimization":
      found = OPT_ID.search(str(issue.meta.get("title", "")))
      if found:
        if found.group(0) in optimization_ids:
          problems.append(f"optimization id {found.group(0)} used by {issue.id} and {optimization_ids[found.group(0)]}")
        optimization_ids[found.group(0)] = issue.id
  if assume:
    for identifier in promised_ids() - set(by_id):
      by_id[identifier] = Promised(identifier)
  else:
    for identifier in sorted(promised_ids() - set(by_id)):
      problems.append(f"backlog/README.md promises {identifier}, but no file defines it")
  selected = [pathlib.Path(argument).resolve() for argument in arguments] or [BACKLOG]
  for issue in everything:
    if any(issue.path == path or path in issue.path.parents for path in selected):
      check(issue, by_id, problems)
  if not arguments:
    find_cycles(by_id, problems)
  for problem in sorted(set(problems)):
    print(problem)
  ready = sum(1 for issue in everything if issue.meta.get("state") == "ready")
  print(f"{len(everything)} issues ({ready} ready, {len(everything) - ready} draft), {len(set(problems))} problems")
  return 1 if problems else 0


if __name__ == "__main__":
  sys.exit(main(sys.argv[1:]))
