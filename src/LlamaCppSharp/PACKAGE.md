# Bubo.LlamaCppSharp

Managed .NET 8 wrapper surface for Bubo's pinned llama.cpp native assets.

This package intentionally avoids shelling out to `llama-cli`, `llama-server`, Ollama, or other host binaries. Native assets are supplied by RID-specific packages and loaded through the .NET runtime native library resolver.
