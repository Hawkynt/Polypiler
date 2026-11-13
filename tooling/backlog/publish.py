#!/usr/bin/env python3
"""Publishes backlog issue files as GitHub issues.

usage: publish.py [--apply] [--hold] [--limit N] [--repo owner/name] [path ...]

Without --apply nothing is changed: the plan is printed. With --apply the script
  1. creates the state:* labels if missing,
  2. creates every issue not yet recorded in backlog/.published.json,
  3. rewrites [[ID]] references to #<number>, puts a "Blocked by" block on top of every body and
     updates each issue whose content changed,
  4. sets the label — epic, state:draft, state:blocked while a dependency is open, state:ready
     otherwise — unless the issue is closed or in flight, and adds the Readiness review section,
  5. links each issue to its parent epic as a GitHub sub-issue,
  6. records every direct dependency as a native "blocked by" relation (dependencies implied by
     another blocker are listed in the body only: GitHub lets one issue block at most 100 others).
It is idempotent: re-run it after an interruption or after editing files and only the differences
are sent. --hold labels everything state:draft, for publishing while files are still being edited.
"""
import datetime
import hashlib
import json
import subprocess
import sys
import time

from common import BACKLOG, REFERENCE, load_all

STATE_FILE = BACKLOG / ".published.json"
STATE_LABELS = {
  "epic": ("5319e7", "Carries no implementation; closes with its last sub-issue. Never claimed."),
  "state:draft": ("ededed", "Definition of Ready not met; must not be worked"),
  "state:ready": ("0e8a16", "Definition of Ready met; can be claimed"),
  "state:blocked": ("b60205", "Cannot proceed; the last comment names the blocker"),
}
CREATE_PAUSE = 7.5   # GitHub allows about 500 content-creating requests per hour
WRITE_PAUSE = 1.0


def gh(*arguments, stdin=None, tolerate=()):
  for attempt in range(10):
    result = subprocess.run(["gh", *arguments], input=stdin, capture_output=True, text=True)
    if result.returncode == 0:
      return result.stdout.strip()
    error = result.stderr.strip()
    if any(text in error for text in tolerate):
      return None
    if "rate limit" in error.lower() or "HTTP 429" in error or "abuse" in error.lower():
      wait = 90 * (attempt + 1)
      print(f"    rate limited, waiting {wait} s", flush=True)
      time.sleep(wait)
      continue
    if "HTTP 5" in error and attempt < 3:
      time.sleep(15)
      continue
    raise RuntimeError(f"gh {' '.join(arguments[:4])} failed: {error}")
  raise RuntimeError(f"gh {' '.join(arguments[:4])} kept being rate limited")


DIRECT = {}


def reduce_dependencies(issues):
  """Direct blockers per issue: a dependency already implied by another dependency is left out.

  GitHub lets one issue block at most 100 others, and the foundation issues are prerequisites of
  several hundred; recording only the direct ones keeps every relation within that limit without
  changing when an issue becomes workable.
  """
  depends = {issue.id: issue.list("depends") for issue in issues}
  reach = {}

  def closure(identifier, trail=()):
    if identifier not in reach:
      found = set()
      for target in depends.get(identifier, []):
        if target in trail:
          continue
        found.add(target)
        found |= closure(target, trail + (identifier,))
      reach[identifier] = found
    return reach[identifier]

  for identifier, targets in depends.items():
    DIRECT[identifier] = [target for target in targets
                          if not any(target in closure(other) for other in targets if other != target)]


def save(numbers):
  STATE_FILE.write_text(json.dumps(numbers, indent=2, sort_keys=True) + "\n")


def render(issue, numbers):
  def number(identifier):
    return f"#{numbers[identifier]['number']}" if identifier in numbers else f"`{identifier}`"

  head = []
  parent = issue.meta.get("parent")
  if parent:
    head.append(f"**Part of:** {number(parent)}")
  depends = issue.list("depends")
  direct = DIRECT.get(issue.id, depends)
  if depends:
    block = ("**Blocked by** — do not start before every one of these is closed:\n"
             + "\n".join(f"- {number(identifier)}" for identifier in direct))
    indirect = [identifier for identifier in depends if identifier not in direct]
    if indirect:
      block += ("\n\n<details><summary>Also required, but already implied by the blockers above ("
                + str(len(indirect)) + ")</summary>\n\n"
                + ", ".join(number(identifier) for identifier in indirect) + "\n\n</details>")
    head.append(block)
  elif issue.meta.get("template") != "1-epic":
    head.append("**Blocked by:** nothing — can start as soon as it is `state:ready`.")
  body = REFERENCE.sub(lambda match: number(match.group(1)), issue.body)
  return ("\n\n".join(head) + "\n\n---\n\n" + body.rstrip() + "\n\n" + review(issue, numbers, number)
          + f"\n<!-- backlog:{issue.id} -->\n")


def review(issue, numbers, number):
  """The Readiness review section the templates require, derived from the front matter."""
  date = numbers.get(issue.id, {}).get("reviewed") or datetime.date.today().isoformat()
  depends = issue.list("depends")
  blockers = ", ".join(number(identifier) for identifier in DIRECT.get(issue.id, depends)) if depends else "none"
  if issue.meta.get("template") == "1-epic":
    decision = ("epic — never claimed; it closes with its last sub-issue. Acceptance criteria, test design, "
                "measurements, evidence and implementation plan: N/A — an epic carries no implementation, "
                "each sub-issue specifies its own.")
  elif issue.meta["state"] == "draft":
    decision = "`state:draft` — the unknowns under *Blocking questions* must be resolved and reviewed first."
  elif depends:
    decision = ("`state:blocked` until every blocker is closed, then `state:ready` — the work order is complete "
                "and waits for nothing but its dependencies.")
  else:
    decision = "`state:ready` — the work order is complete and has no unmet prerequisite."
  return ("## Readiness review\n\n"
          f"- **Reviewer / date:** backlog author, as delegate of the maintainer; {date}\n"
          f"- **Decision:** {decision}\n"
          f"- **Blockers:** {blockers}\n\n"
          "Changes to scope, fixture hash, profile/ABI, dependency, licence or acceptance contract require fresh\n"
          "review before implementation continues. Claim a Ready issue with `in-flight` and `<number>-<slug>`.\n")


def main(arguments):
  apply, hold = "--apply" in arguments, "--hold" in arguments
  repo, limit, skip, paths = "Hawkynt/Polypiler", None, set(), []
  for index, argument in enumerate(arguments):
    if argument == "--repo":
      repo = arguments[index + 1]
      skip.add(index + 1)
    elif argument == "--limit":
      limit = int(arguments[index + 1])
      skip.add(index + 1)
    elif not argument.startswith("--") and index not in skip:
      paths.append(argument)
  issues, errors = load_all(paths or None)
  everything, _ = load_all()
  sys.setrecursionlimit(10000)
  reduce_dependencies(everything)
  if errors:
    for path, error in errors:
      print(f"{path}: {error}")
    return 1
  issues.sort(key=lambda issue: (issue.meta["milestone"], str(issue.path.parent), issue.meta.get("template") != "1-epic",
                                 issue.id))
  numbers = json.loads(STATE_FILE.read_text()) if STATE_FILE.exists() else {}
  new = [issue for issue in issues if issue.id not in numbers][:limit]
  print(f"{len(issues)} files, {len(numbers)} already published, {len(new)} to create in {repo}")
  if not apply:
    for issue in new:
      print(f"  would create [{issue.meta['milestone']}] {issue.meta['title']}")
    print("dry run; pass --apply to publish")
    return 0

  for label, (colour, description) in STATE_LABELS.items():
    gh("label", "create", label, "-R", repo, "--color", colour, "--description", description, "--force")
  milestones = {entry["title"].split(" ")[0]: entry["number"]
                for entry in json.loads(gh("api", f"repos/{repo}/milestones?state=all&per_page=100"))}

  open_numbers = set()
  if numbers:
    listed = gh("api", "--paginate", f"repos/{repo}/issues?state=open&per_page=100", "--jq", ".[].number")
    open_numbers = {int(line) for line in listed.split()}

  def state_label(issue):
    if issue.meta.get("template") == "1-epic":
      return "epic"
    if hold or issue.meta["state"] == "draft":
      return "state:draft"
    blocked = any(target not in numbers or numbers[target]["number"] in open_numbers
                  for target in issue.list("depends"))
    return "state:blocked" if blocked else "state:ready"

  for count, issue in enumerate(new, 1):
    body = render(issue, numbers)
    created = json.loads(gh("api", f"repos/{repo}/issues", "--input", "-", stdin=json.dumps({
      "title": issue.meta["title"], "body": body, "labels": issue.list("labels") + [state_label(issue)],
      "milestone": milestones[issue.meta["milestone"]]})))
    numbers[issue.id] = {"number": created["number"], "id": created["id"], "linked": False, "blocked_by": [],
                         "hash": "", "state": state_label(issue), "reviewed": datetime.date.today().isoformat()}
    open_numbers.add(created["number"])
    save(numbers)
    print(f"  [{count}/{len(new)}] #{created['number']} {issue.id}", flush=True)
    time.sleep(CREATE_PAUSE)

  published = [issue for issue in issues if issue.id in numbers]
  for count, issue in enumerate(published, 1):
    entry = numbers[issue.id]
    body = render(issue, numbers)
    digest = hashlib.sha256((issue.meta["title"] + "\0" + body + "\0" + issue.meta["milestone"]
                             + "\0" + ",".join(sorted(issue.list("labels")))).encode()).hexdigest()
    if entry.get("hash") != digest or entry.get("state") != state_label(issue):
      current = json.loads(gh("api", f"repos/{repo}/issues/{entry['number']}"))
      payload = {"title": issue.meta["title"], "body": body, "milestone": milestones[issue.meta["milestone"]]}
      present = [label["name"] for label in current["labels"]]
      if current["state"] == "open" and "in-flight" not in present and "state:blocked" not in present:
        keep = [name for name in present
                if not name.startswith("state:") and name != "epic" and name not in issue.list("labels")]
        payload["labels"] = issue.list("labels") + keep + [state_label(issue)]
        entry["state"] = state_label(issue)
      gh("api", "-X", "PATCH", f"repos/{repo}/issues/{entry['number']}", "--input", "-", stdin=json.dumps(payload))
      entry["hash"] = digest
      save(numbers)
      time.sleep(WRITE_PAUSE)
    parent = issue.meta.get("parent")
    if parent and parent in numbers and not entry.get("linked"):
      gh("api", "-X", "POST", f"repos/{repo}/issues/{numbers[parent]['number']}/sub_issues", "--input", "-",
         stdin=json.dumps({"sub_issue_id": entry["id"]}), tolerate=("already", "duplicate"))
      entry["linked"] = True
      save(numbers)
      time.sleep(WRITE_PAUSE)
    for target in DIRECT.get(issue.id, issue.list("depends")):
      if target in numbers and target not in entry.setdefault("blocked_by", []):
        gh("api", "-X", "POST", f"repos/{repo}/issues/{entry['number']}/dependencies/blocked_by", "--input", "-",
           stdin=json.dumps({"issue_id": numbers[target]["id"]}),
           tolerate=("already", "duplicate", "more than 100", "more than 50"))
        entry["blocked_by"].append(target)
        save(numbers)
        time.sleep(WRITE_PAUSE)
    if count % 25 == 0:
      print(f"  updated and linked {count}/{len(published)}", flush=True)
  print("published")
  return 0


if __name__ == "__main__":
  sys.exit(main(sys.argv[1:]))
