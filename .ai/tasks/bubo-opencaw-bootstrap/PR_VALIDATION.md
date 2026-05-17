# PR Validation: Native OpenCaw Bootstrap Support

## Branch

`feature/bubo-opencaw-bootstrap`

## Issue

https://github.com/TimothyMeadows/Bubo/issues/31

## Validation

- `dotnet build Bubo.sln`: passed.
- `dotnet test Bubo.sln --no-build`: passed, 106 tests.
- `dotnet format Bubo.sln --verify-no-changes --no-restore`: passed.
- `git diff --check`: passed.

## Scope

- Adds OpenCaw startup/bootstrap support before `INPUT.md` processing.
- Renames the OpenCaw baseline submodule mount to `.opencaw`.
- Preserves host `.ai` memory, fragments, learnings, and task files.
- Wires loaded OpenCaw context into inference system prompts.
