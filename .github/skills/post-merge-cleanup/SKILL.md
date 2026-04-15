---
name: post-merge-cleanup
description: Investigate one build warning or error in the merged Razor worktree and fix it by adding a post-merge cleanup step in RepoMerger. Use this when asked to debug build.cmd -restore -a issues after merging src\Razor into Roslyn.
---

Use this skill for the recurring Razor-on-Roslyn cleanup loop:

- run `build.cmd -restore -a`
- pick one warning or error
- find the Razor-specific cause
- add a post-merge cleanup step at the **end** of RepoMerger's cleanup list
- rerun only that cleanup step against the existing merged worktree
- if the step does not fix the issue, reset the merged target repo to the commit immediately before that cleanup step and rerun just that step after fixing it
- rerun restore to validate the issue is gone
- ensure the rationale is attached to the Roslyn cleanup commit, not the RepoMerger repo commit

Unless the user says otherwise, use the existing merged worktree here:

- `C:\Code\repo-merge-work\razor\target`

Prefer the short run name `razor` for RepoMerger validation runs to avoid long Windows paths.

## Preferred approach

Err on the side of **removing** Razor-local configuration and deferring to Roslyn's existing packages, versions, targets, conventions, and infrastructure.

But do **not** delete Razor-local config blindly. First compare the Razor source file to the Roslyn target file it would use after the merge:

- if the files are effectively the same, removing the Razor-local duplication is preferred
- if Razor carries meaningful settings that Roslyn does not, preserve them with a **Razor-local overlay** under `src\Razor` and strip out only the keys that Roslyn already provides
- for `.globalconfig` conflicts, prefer copying Razor's files into `src\Razor`, trimming duplicate keys, and rewriting Razor imports to point at those local files rather than re-importing Roslyn's shared files twice

Good fixes look like:

- removing duplicate package references that Roslyn already adds centrally
- removing Razor-local analyzer references that Roslyn already enforces elsewhere
- rewriting Razor package versions to use Roslyn's shared version properties
- deleting redundant imports or build/test settings that conflict with Roslyn infrastructure
- preserving Razor-only analyzer settings by localizing them under `src\Razor` when Roslyn already imports overlapping shared config

Avoid fixing the issue by changing Roslyn's central infrastructure unless the user explicitly asks for that.

When editing MSBuild/XML/config files, keep the diff **surgical**:

- remove stale comments when removing the associated imports or items
- preserve existing encoding/BOM and avoid unrelated reformatting
- prefer targeted text rewrites over whole-file XML reserialization when the latter would churn unrelated lines

## Workflow

1. Run `build.cmd -restore -a` in `C:\Code\repo-merge-work\razor\target`.
2. Choose **one** warning or error to address.
3. Identify the root cause by searching under `src\Razor` and comparing it to Roslyn's root-level build/test/package infrastructure.
4. If the issue involves props/targets/globalconfigs/editorconfigs, compare the **Razor source checkout** with the **merged Roslyn target** before removing anything so you can tell whether the config is duplicate or Razor-specific.
5. Implement the fix in `Utilities\PostMergeCleanupRunner.cs` as a new or updated post-merge cleanup step.
   - If you are adding a **new** cleanup step, append it to the **end** of the `Steps` array so single-step validation exercises the same preconditions as a full clean run.
6. Keep the cleanup:
   - idempotent
   - focused on a single root cause
   - biased toward removing Razor-specific duplication
   - careful not to lose Razor-only behavior when a local overlay is safer than deletion
7. Build RepoMerger:

   ```powershell
   dotnet build RepoMerger.slnx
   ```

8. Apply the cleanup against the existing worktree by running only the step you just changed:

   ```powershell
   dotnet run -- --run-name razor --post-merge-cleanup-step <step-name>
   ```

9. If the step did not fix the issue, reset the merged target repo to the commit immediately before that cleanup step so the next retry runs in the same state a clean run would produce, then rerun step 8 after updating RepoMerger.

10. Re-run:

   ```powershell
   .\build.cmd -restore -a
   ```

   Confirm that the specific issue you chose is gone or that restore has moved on to the next issue in the queue.

11. Commit the RepoMerger change.

## Commit message guidance

If the cleanup produces or requires a commit in the **Roslyn target worktree**, that commit body should explain **why** the cleanup is correct in Roslyn, for example:

- `Razor doesn't need to specify xunit.extensibility.execution because Roslyn already adds it in eng\targets\XUnit.targets.`
- `Razor should use Roslyn's shared Microsoft.Extensions versioning instead of carrying its own ObjectPool version entry.`

If you also commit the RepoMerger change in **this** repository, keep that commit focused on the tool change itself. Do not put the Roslyn-specific rationale body on the RepoMerger repo commit unless the user explicitly asks for that.

## Final response

Summarize:

- the warning or error you selected
- the root cause
- the cleanup step you implemented
- the result of re-running `build.cmd -restore -a`

Stop after that summary.
