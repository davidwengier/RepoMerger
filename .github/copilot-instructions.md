# RepoMerger Copilot Instructions

## Repository focus

- This repo is a C# CLI for merging `dotnet/razor` into `dotnet/roslyn` under `src\Razor`.
- The runnable project lives at the repo root. Build from the repo root with `dotnet build RepoMerger.slnx`.
- Command-line defaults and entrypoint behavior live in `Program.cs`.
- Most merge-specific fixes live in `Utilities\PostMergeCleanupRunner.cs`.

## Preferred workflow

- Prefer the short run name `razor` for validation runs to avoid long Windows paths.
- For single-step cleanup iteration, run from the repo root:

  ```powershell
  dotnet run -- --run-name razor --post-merge-cleanup-step <step-name>
  ```

- For end-to-end validation, run a full clean merge and wait for the entire cleanup pass to finish before drawing conclusions. Do not trust half-complete cleanup results.
- If the user asks to debug `build.cmd -restore -a` issues in the merged Razor worktree, use the project skill in `.github\skills\post-merge-cleanup\SKILL.md`.

## Post-merge cleanup conventions

- Append new cleanup steps to the end of `PostMergeCleanupRunner.Steps` so one-step reruns see the same prerequisites as a full clean run.
- If a one-step validation fails and you update that step, reset the target repo/worktree to the commit immediately before that cleanup step before rerunning it.
- Keep each cleanup step idempotent and focused on one root cause.
- Prefer Roslyn's central props, targets, package versions, analyzers, and repo metadata over Razor-local duplication.
- If Razor has behavior Roslyn does not provide, preserve it as a Razor-local overlay under `src\Razor` instead of deleting it outright.
- Before deleting or rewriting shared config, compare the Razor source version with the Roslyn target version first.
- Keep MSBuild/XML edits surgical: preserve encoding/BOM, avoid unrelated reformatting, and remove stale comments when removing the associated imports or items.
- If a cleanup step creates or deletes files, explicitly stage them with `git add`; the cleanup commit helper only commits tracked changes.

## Merge-specific heuristics

- Moving Razor content under `src\Razor` before history filtering is intentional. It preserves root infrastructure such as `.github`, `eng`, `global.json`, solution files, and other repo-root build assets.
- `global.json` changes should usually stay Roslyn-owned. Only merge Razor entries that are actually restore-critical, such as missing `msbuild-sdks`.
- Many merge/test failures come from path assumptions after nesting under `src\Razor`; prefer fixing shared helpers instead of patching one failing test at a time.

## Commit guidance

- When a post-merge cleanup step creates a commit in the Roslyn target worktree, that commit should carry the step-specific rationale for why the cleanup is correct in Roslyn.
- If you also commit the RepoMerger change in this repo, keep that commit focused on the tool change itself.
