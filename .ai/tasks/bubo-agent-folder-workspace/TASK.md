# Add Explicit Agent Code Folder Workspace

## Issue

https://github.com/TimothyMeadows/Bubo/issues/33

## Goal

Add first-class support for running Bubo with a user-supplied shared `FOLDER`. `FOLDER` is the code and artifact folder the agent may inspect and modify. `INPUT` is a prompt file or inline Markdown prompt that may live outside the code folder. `OUTPUT` is the final report path, and Bubo-owned report artifacts must resolve under `FOLDER/.ai/artifacts`; code edits remain on their requested guarded paths inside `FOLDER`.

## Assumptions

- `--folder` is the preferred CLI option.
- Existing `--workspace` remains as a backward-compatible alias.
- `--input` may be either an existing Markdown file path or inline Markdown prompt text.
- If both `--folder` and `--workspace` are supplied and resolve to different paths, parsing fails.
- Default `INPUT.md` remains under the folder when not specified.
- Default `OUTPUT.md`, debug logs, and transcript artifacts live under `.ai/artifacts`.
- Explicit `--output` may name a file or subpath under `.ai/artifacts`; external output paths and root-folder output clutter are rejected.
- OpenCaw `.opencaw` and host `.ai` are loaded from `FOLDER`.

## Ordered Tasks

1. [x] Add task tracking and subagent lane plan.
2. [x] Add CLI `--folder` parsing and conflict validation with `--workspace`.
3. [x] Split runtime path resolution so input can live outside the guarded code folder while output stays inside it.
4. [x] Keep tools, Docker `/workspace`, Git operations, and OpenCaw rooted in the code folder.
5. [x] Add CLI/runtime/sandbox tests for external input, folder-contained output, and workspace escape protection.
6. [x] Update docs and examples.
7. [x] Run validation and record evidence.

## Validation Plan

- `dotnet build Bubo.sln`
- `dotnet test Bubo.sln`
- `dotnet format Bubo.sln --verify-no-changes --no-restore`
- `git diff --check`

## Implementation Notes

- Added `AgentRunPathResolver` so the guarded code folder and host input path are resolved separately while output remains guarded by the folder.
- Added inline Markdown input support with conservative missing-path detection for `.md`/`.markdown`, rooted, and separator-containing path-like values.
- Kept `WorkspaceGuard` as the only authority for code-folder tools, Git, Docker command working directory, and OpenCaw context.
- Added `--folder` as the preferred `bubo run` option and kept `--workspace` as a compatibility alias.
- Output report directories are created under `.ai/artifacts`; sandboxed command mounts still collapse required mount roots to the code folder.
- Updated README, configuration docs, examples, and CLI package notes to describe `INPUT + FOLDER -> OUTPUT` usage.

## Clarification

The user clarified that the supplied workspace/folder is also the output artifact folder. The purpose is a shared user/agent folder for coding and generated files, not a separate external report output location.

The user later clarified that this artifact routing must not redirect code writes. The implementation routes Bubo-owned run artifacts to `.ai/artifacts` while leaving `write_file`, `patch_file`, `git_apply_patch`, Git operations, and sandboxed commands on their requested guarded paths inside `--folder`.

## Validation Evidence

- `dotnet test Bubo.sln --configuration Release` passed after placing Git Bash before the Windows WSL launcher on `PATH`.
- `dotnet format Bubo.sln --verify-no-changes --no-restore` passed.
- `git diff --check` passed.

Additional validation after inline Markdown input support:

- Focused runtime inline/missing-path/non-Markdown tests passed.
- Focused CLI inline Markdown E2E test passed.
- `dotnet test Bubo.sln --configuration Release` passed.
- `dotnet format Bubo.sln --verify-no-changes --no-restore` passed.
- `git diff --check` passed.

Additional validation after `.ai/artifacts` report routing:

- Focused `AgentRunnerTests` passed.
- `LocalAgent.Cli.Tests` passed.
- `dotnet test Bubo.sln --configuration Release` passed.
- `dotnet format Bubo.sln --verify-no-changes --no-restore` passed.
- `git diff --check` passed.

## Environment Note

On this Windows host, `bash` initially resolved to `C:\Users\timot\AppData\Local\Microsoft\WindowsApps\bash.exe`, which cannot run the existing OpenCaw bootstrap test with a Windows working directory. Git Bash is installed at `C:\Program Files\Git\bin\bash.exe`; prefixing `PATH` with `C:\Program Files\Git\bin;C:\Program Files\Git\usr\bin;` made the existing bootstrap test pass.
