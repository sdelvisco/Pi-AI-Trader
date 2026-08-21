# Claude Code Instructions

## Branching Policy

Each task gets its own feature branch and pull request. Do not commit directly to `main`.

- Create a new branch per task, named descriptively for the fix (e.g. `claude/positions-glob-fix`)
- Open a pull request from that branch targeting `main`
- Do NOT merge the PR yourself — Lord Sal reviews and merges manually
- Do NOT reuse an old branch name for unrelated follow-up work, even if it's already merged — create a new branch per task, every time
- If a branch's PR has already been merged and you have further changes for the same underlying issue, create a fresh branch from the current `main`, not a new commit on the old (merged) branch
- After pushing, confirm and report: the branch name, the PR number/link if created, and `git log --oneline -3` to confirm the commits are on the branch you intended — not `main`, and not a stale/reused branch

## Commit Style
- Use conventional commits: `fix:`, `feat:`, `chore:`, `docs:`
- Keep commit messages concise and descriptive

## Code Style
- Verbosely comment all generated code
- C# follows existing patterns in DualMomentumV2.cs
