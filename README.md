# GreatTamanaApp

Avalonia chat/agent desktop app. Target: `net10.0`, win-x64, single-file.

## Setup
1. Copy `GTA.App/Config/init.ini.example` to `init.ini` and fill in the values. `init.ini` is gitignored.
2. Optional: `Config/secret.ini` overrides `init.ini`. Also gitignored.

## Build
    dotnet build GTA.slnx -c Release
    dotnet publish GTA.App -c Release

## Projects
- GTA.App: UI (exe)
- GTA.Core: config, LLM clients, credentials
- GTA.Builder: agentic loop
- GTA.Tools / GTA.Tools.Impl: tool descriptors / implementations
- GTA.PptxLib, GTA.DocxLib: document extractors
