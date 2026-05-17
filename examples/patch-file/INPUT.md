# Patch File Fixture

Patch a generated note with a bounded old/new text replacement.

```bubo-actions
[
  {
    "tool": "write_file",
    "arguments": {
      "path": "generated/patch-target.txt",
      "content": "Hello from Bubo.\n"
    }
  },
  {
    "tool": "patch_file",
    "arguments": {
      "path": "generated/patch-target.txt",
      "old": "Hello from Bubo.",
      "new": "Patched by Bubo."
    }
  }
]
```
