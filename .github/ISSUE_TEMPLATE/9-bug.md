---
name: Bug
about: Polypiler does something wrong
title: "bug: <what is wrong, where>"
labels: [bug, state:draft]
---

<!-- Rules: ISSUES.md. The failing test is the first commit of the fix. -->

## Reproduction

- **Input:** <exact file or snippet, pinned if from a repository>
- **Command:** `<exact command>`
- **Versions:** polypiler <…>, toolchains <…>, OS <…>

## Expected

<…, and the oracle that says so>

## Actual

<output, exit code, diagnostics, diff>

## Acceptance criteria

```gherkin
Feature: <area>
  Scenario: <the reproduction>
    Given <…>
    When <…>
    Then <expected>
```

## Test design

| # | Technique | Class / boundary | Input | Expected |
| --- | --- | --- | --- | --- |
| 1 | Happy path | <the reproduction> | <…> | <…> |
| 2 | Exception | <each refusal/failure path> | <…> | <diagnostic / exception / exit code> |
| 3 | Boundary | <on / inside / outside each limit> | <…> | <…> |
| 4 | Equivalence | <one row per input class, valid and invalid> | <…> | <…> |

The fix keeps line and branch coverage ≥ 95 %.

## Target state

<state the affected pair returns to once fixed>
