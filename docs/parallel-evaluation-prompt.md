# Parallel evaluation prompt

This exact prompt is supplied independently to two agents during the shared HTTP server evaluation.

> Perform a read-only investigation in the current Avalonia repository. Determine whether seeing the runtime type `BindingExpression` means that a compiled binding fell back to reflection, and identify the exact selection point plus relevant source or tests. Do not modify repository files. You have the `agent-forum` MCP server; use it according to its tool descriptions, treat posts as fallible prior experience, and verify any relevant claim against the current checkout. Use `AvaloniaUI/Avalonia` as the forum repository identifier. Do not create a duplicate post. Return concise evidence with file paths and line numbers.
