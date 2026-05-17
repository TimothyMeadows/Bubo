# Gotchas

## Stacked PR Merge Semantics

- GitHub marks stacked PRs as merged when they are merged into their configured base branch, even if that base is another feature branch rather than `main`.
- `Closes #N` in a PR targeting a non-default stacked branch may not auto-close the linked issue. Verify issue state after merges and close completed task issues manually when needed.
- After stacked PR merges, inspect `git log --graph --all` or `git branch -r --contains <sha>` before assuming work is present on `main`.

## Current Main Branch Caveat

- After PRs #7-#11 were merged, only PR #7 was merged directly into `main`; PRs #8-#11 were merged into their stacked feature bases. Future implementation work should verify whether to integrate the final stack branch into `main` before building on the broader runtime features.

## Windows And WSL Git Status

- The OpenCaw `pr-readiness-check.sh` script runs through WSL and may report broad false-positive modifications on this Windows checkout because of line-ending normalization. Cross-check PR scope with native PowerShell `git status --short --branch` before committing.

## Model-Safe Tooling

- Do not expose generic `run_command` to one-shot model-proposed actions. Keep command execution behind deterministic/user-authored fixtures or a future explicit approval-gated loop.

## OpenCaw Bootstrap On Windows

- Invoke OpenCaw shell scripts from the `.opencaw` working directory using relative POSIX-style paths such as `./commands/create-host-ai-scaffold.sh`; Windows absolute paths passed to `bash` can fail under Git Bash path translation.
- Put Git Bash before the Windows WSL launcher on `PATH` for .NET tests that spawn `bash` with a Windows working directory: `C:\Program Files\Git\bin;C:\Program Files\Git\usr\bin`.

## Bubo Folder Workspace Boundary

- `--folder` is the shared writable root for tools, Git, Docker commands, and OpenCaw. External `--input` is allowed as a host-runtime read path, the Markdown run report goes to stdout, and Bubo-owned review sidecars must resolve under `<folder>/.ai/artifacts`.
- Do not route generic `write_file`/patch/Git changes into `.ai/artifacts`; those are code/file operations on requested guarded paths. Only Bubo-owned review sidecars belong there by default.
- `--input` can also be inline Markdown text. Missing values that look like Markdown paths, rooted paths, or separator-containing paths still fail as missing files instead of being silently treated as prompts.

## Windows CUDA Native Builds

- Visual Studio developer shells may set `Platform=x64`; clear it for managed `dotnet` verification or output probing can look under `bin/x64/Release` instead of the default SDK project output path.
- Ninja is a good Windows CUDA generator with VS Build Tools, but single-config CMake builds must receive `-DCMAKE_BUILD_TYPE=Release`; `--config Release` alone is not enough.
- CUDA Toolkit 13.2 installs `cublas64_13.dll` under `CUDA\v13.2\bin\x64`; CUDA runtime smoke tests need both `bin` and `bin\x64` on `PATH`.
