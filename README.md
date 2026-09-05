# Unity Container Benchmark

A standalone Unity 6 project for comparing managed .NET containers with Unity Collections in IL2CPP Players.

## Status

The benchmark harness and report pipeline are implemented. Formal dual-channel measurements are not published yet; generated results must pass the repository validation gates before they are treated as evidence.

## Requirements

- Unity `6000.5.9f1`
- Unity Collections Core package `6.5.0` (Editor-bundled, resolved as `builtin`)
- Windows x64 IL2CPP build support
- PowerShell 5.1 or newer for the formal matrix runner

The project intentionally contains no game assets or dependencies from its source game project.

## Open and run

Open the repository root as a Unity project and load `Assets/Scenes/Container.unity`.

- `Container > 阶段1自测(10K全用例)` runs the 119-case correctness suite.
- The Player UI configures and runs interactive subsets.
- Formal unattended runs use the command-line interface documented below.

## Reproducible Player builds

```powershell
Unity.exe -batchmode -nographics -quit `
  -projectPath . `
  -executeMethod ContainerBenchmarkStandaloneBuild.BuildFromCommandLine `
  -containerBuildChannel ReleaseTiming `
  -containerBuildOutput .\Build\Release\UnityContainerBenchmark.exe `
  -logFile .\Build\release-build.log

Unity.exe -batchmode -nographics -quit `
  -projectPath . `
  -executeMethod ContainerBenchmarkStandaloneBuild.BuildFromCommandLine `
  -containerBuildChannel DevelopmentGc `
  -containerBuildOutput .\Build\Development\UnityContainerBenchmark.exe `
  -logFile .\Build\development-build.log
```

Release Players provide timing evidence and must be launched with `-benchmarkTimingOnly`. Development Players provide the GC allocation channel and reject timing-only mode.

## Formal matrix

```powershell
.\tools\ContainerBenchmark\Run-ContainerBenchmarkMatrix.ps1 `
  -Channel ReleaseTiming `
  -ExePath .\Build\Release\UnityContainerBenchmark.exe `
  -OutputRoot .\BenchmarkResults\release-timing

.\tools\ContainerBenchmark\Run-ContainerBenchmarkMatrix.ps1 `
  -Channel DevelopmentGc `
  -ExePath .\Build\Development\UnityContainerBenchmark.exe `
  -OutputRoot .\BenchmarkResults\development-gc
```

The two channels must run sequentially. Running them concurrently invalidates timing data.

## Evidence contract

- IL2CPP Player only
- one warm-up pass plus ten steady-state samples
- median is the primary timing statistic; mean and nearest-rank p95 are retained
- setup, per-pass reset, validation, disposal, UI and export stay outside the timed region
- mutating cases rebuild and release their fixture every pass; explicitly read-only cases build once, reset outside timing for each pass, and release once after the 1+10 sequence
- every result validates its observable outcome
- timing comes only from the Release Timing channel; GC allocation comes only from the Development GC channel
- final reports are assembled only when all six shard artifacts, checkpoints, hashes, package snapshots and build GUIDs agree

## Licensing and privacy

No open-source license has been selected for this repository yet. The project source remains all rights reserved unless and until a top-level license is added; public visibility alone does not grant reuse rights.

Generated reports may contain operating-system and hardware metadata. Review them before publishing. Third-party attribution is recorded in `THIRD_PARTY_NOTICES.md`.
