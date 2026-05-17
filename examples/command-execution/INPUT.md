# Command Execution Fixture

Run a safe allowlisted command and record the result in the stdout report.

```bubo-actions
[
  {
    "tool": "run_command",
    "arguments": {
      "executable": "dotnet",
      "arguments": ["--version"]
    }
  }
]
```
