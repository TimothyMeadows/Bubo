# Git Apply Patch Fixture

Apply a guarded unified diff through the Docker sandbox.

```bubo-actions
[
  {
    "tool": "git_apply_patch",
    "arguments": {
      "patch": "diff --git a/generated/git-apply-result.txt b/generated/git-apply-result.txt\nnew file mode 100644\nindex 0000000..d3b0738\n--- /dev/null\n+++ b/generated/git-apply-result.txt\n@@ -0,0 +1 @@\n+Patched through git apply.\n"
    }
  }
]
```
