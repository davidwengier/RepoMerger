---
mode: agent
description: Investigate one restore issue in the merged Razor worktree and fix it with a post-merge cleanup step
tools:
  - codebase
  - editFiles
  - runCommands
  - search
---

Investigate **one** restore warning or error in the merged Razor-on-Roslyn worktree and fix it by adding or updating a post-merge cleanup step in RepoMerger.

Default worktree:

- `D:\Code\repo-merge-work\razor\target`

Follow this workflow:

1. Run `restore.cmd` in the target worktree.
2. Pick one concrete warning or error from the output.
3. Analyze the cause, preferring explanations where Razor is carrying configuration, package references, versions, targets, or imports that Roslyn already supplies centrally.
4. Implement the fix in `src\RepoMerger\Utilities\PostMergeCleanupRunner.cs` as a post-merge cleanup step.
5. Prefer **removing or simplifying** Razor-local configuration over adding new compatibility logic.
6. Re-run `restore.cmd` to validate that the specific issue is gone or has been replaced by the next issue in the queue.
7. Commit the RepoMerger change after the validation run.

Commit message requirements:

- The subject should describe the cleanup.
- The body should explain **why the fix is appropriate**, for example:
  - `Razor doesn't need to specify xunit.extensibility.execution because Roslyn already adds it in eng\targets\XUnit.targets.`
  - `Razor should use Roslyn's shared Microsoft.Extensions versioning instead of carrying its own ObjectPool version entry.`

Final response requirements:

- Briefly summarize:
  - the warning or error selected
  - the root cause
  - the cleanup step that was implemented
  - the result of re-running `restore.cmd`

Guardrails:

- Fix only one issue per invocation unless multiple diagnostics clearly share the same root cause.
- Do not modify Roslyn's central infrastructure when a Razor-local cleanup can solve the problem.
- Err on the side of deferring to Roslyn packages, versions, targets, and build/test conventions.
