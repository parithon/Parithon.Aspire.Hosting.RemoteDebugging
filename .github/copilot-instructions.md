# GitHub Copilot Instructions

## Branch Policy

- **Always target `develop`** as the base branch for all implementation work and PRs.
- **Never target `main` directly.** `main` is the release/default branch only.
- When creating a branch for a milestone issue, branch off from `develop`.

## Workflow

- This project follows a **TDD-first** delivery model:
  1. Write failing tests first (red).
  2. Implement the minimum code to pass (green).
  3. Refactor with all tests green.
- Each milestone issue (#2–#15) maps to **one PR**. Keep PRs scoped to that milestone only.
- Do not implement multiple milestones in a single PR.

## Milestone Dependency Chain

Milestones are linear and must be implemented in order:

```
M0 (#2) → M1 (#3) → M2 (#4) → M3 (#5) → M4 (#6) → M5 (#7) → M6 (#8) →
M7 (#9) → M8 (#10) → M9 (#11) → M10 (#12) → M11 (#13) → M12 (#14) → M13 (#15)
```

Do not start a milestone until its blocker is merged into `develop`.

## PR Checklist (required for every milestone PR)

- [ ] Failing tests were added first
- [ ] Unit/integration tests are green
- [ ] Verification output is included in the PR description
- [ ] Docs/plan updated if behavior changed

## Plan Reference

See `docs/remote-debugging-plan.md` for full scope, API shape, lifecycle contracts, and acceptance criteria.
