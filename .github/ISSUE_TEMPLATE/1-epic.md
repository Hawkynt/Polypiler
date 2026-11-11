---
name: Epic
about: A language, format or CPU too large for one pull request; implemented only through sub-issues
title: "lang(<language>): <language> as source and target"
labels: [enhancement, state:draft]
---

<!-- Rules: ISSUES.md. An epic carries no implementation; it closes when its last sub-issue closes. -->

## Why

<User job and PRD section.>

## User stories

- As a **<role from ISSUES.md>**, I want **<capability>**, so that **<benefit>**.

## Scope

**In scope**
- <…>

**Out of scope**
- <… → #issue or "not planned">

## Sub-issues

Link each as a GitHub sub-issue. A language needs at least the frontend and the backend.

- [ ] `frontend(<language>): …` #<n>
- [ ] `backend(<language>): …` #<n>
- [ ] `infra: <language> toolchain runner and CI job` #<n>
- [ ] `e2e(<source>→<target>): …` #<n>

## Done when

<The epic-level gate, e.g. "one real algorithm works as frontend and backend in a multi-language round trip" (PRD §8).>

## Target state

<highest PRD §23.6 state the epic reaches once all sub-issues are done>
