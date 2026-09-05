<#
.SYNOPSIS
Runs and strictly validates the formal ContainerBenchmark shard matrix.

.DESCRIPTION
Runs the selected shards sequentially in a hidden, High-priority Player. The script
derives the process name from ExePath and refuses concurrent matching Players or existing target artifacts. A shard
passes only when its exit code, JSON schema/content, checkpoint hash, and derived HTML
all satisfy the formal evidence contract.

.PARAMETER Channel
ReleaseTiming adds -benchmarkTimingOnly. DevelopmentGc measures GC and does not add it.

.PARAMETER ExePath
Path to the IL2CPP UnityContainerBenchmark.exe for the selected channel.

.PARAMETER OutputRoot
Root directory under which one directory is created per shard. Existing target files
are never overwritten.

.PARAMETER Shards
Ordered shard list. Defaults to linear-small, linear-large, and quadratic-stress.

.EXAMPLE
.\Run-ContainerBenchmarkMatrix.ps1 -Channel ReleaseTiming `
    -ExePath .\Build\Release\UnityContainerBenchmark.exe `
    -OutputRoot .\BenchmarkResults\release-timing

.EXAMPLE
.\Run-ContainerBenchmarkMatrix.ps1 -Channel DevelopmentGc `
    -ExePath .\Build\Development\UnityContainerBenchmark.exe `
    -OutputRoot .\BenchmarkResults\development-gc
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('ReleaseTiming', 'DevelopmentGc')]
    [string]$Channel,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExePath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputRoot,

    [ValidateSet('linear-small', 'linear-large', 'quadratic-stress')]
    [string[]]$Shards = @('linear-small', 'linear-large', 'quadratic-stress')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedCaseCounts = [ordered]@{
    'linear-small'     = 595
    'linear-large'     = 231
    'quadratic-stress' = 102
}

$expectedPackages = [ordered]@{
    collectionsManifestRequest = '2.5.7'
    collectionsResolvedVersion = '6.5.0'
    burstResolvedVersion        = '1.8.30'
    mathematicsResolvedVersion  = '1.4.0'
    packagesLockSha256          = '5496DF13106EE5FE221DD76C28BE21048FA43163AF6C657FF034EE7FE2B779E6'
}
$statisticTolerance = 1e-9
$matrixMutex = $null
$matrixMutexOwned = $false

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$PropertyName,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    if ($null -eq $InputObject.PSObject.Properties[$PropertyName]) {
        throw "${Context}: JSON 缺少字段 '$PropertyName'"
    }
}

function Assert-Near {
    param(
        [Parameter(Mandatory = $true)] [double]$Actual,
        [Parameter(Mandatory = $true)] [double]$Expected,
        [Parameter(Mandatory = $true)] [string]$Context
    )

    $tolerance = $script:statisticTolerance * [Math]::Max(1.0, [Math]::Abs($Expected))
    if ([double]::IsNaN($Actual) -or [double]::IsInfinity($Actual) -or [Math]::Abs($Actual - $Expected) -gt $tolerance) {
        throw "$Context 与 sampleMs 重算值不一致：stored=$Actual expected=$Expected"
    }
}

function Assert-RowStatistics {
    param(
        [Parameter(Mandatory = $true)] [object]$Row,
        [Parameter(Mandatory = $true)] [string]$Context
    )

    Assert-JsonProperty -InputObject $Row -PropertyName 'timedOperationCount' -Context $Context
    foreach ($name in @('medianMs', 'meanMs', 'p95Ms', 'totalMs', 'nsPerOp')) {
        Assert-JsonProperty -InputObject $Row -PropertyName $name -Context $Context
    }
    $samples = [double[]]@($Row.sampleMs | ForEach-Object { [double]$_ })
    foreach ($sample in $samples) {
        Assert-Condition (-not [double]::IsNaN($sample) -and -not [double]::IsInfinity($sample) -and $sample -gt 0.0) "$Context 包含非有限或非正耗时样本"
    }
    Assert-Condition ([long]$Row.timedOperationCount -gt 0L) "$Context timedOperationCount 必须为正数"

    [Array]::Sort($samples)
    $count = $samples.Count
    if (($count % 2) -eq 0) {
        $median = ($samples[$count / 2 - 1] + $samples[$count / 2]) / 2.0
    }
    else {
        $median = $samples[[int]($count / 2)]
    }
    $total = 0.0
    foreach ($sample in $samples) { $total += $sample }
    $mean = $total / $count
    $p95 = $samples[[int][Math]::Ceiling(0.95 * $count) - 1]
    $nsPerOp = $median * 1e6 / [long]$Row.timedOperationCount

    Assert-Near ([double]$Row.medianMs) $median "$Context medianMs"
    Assert-Near ([double]$Row.meanMs) $mean "$Context meanMs"
    Assert-Near ([double]$Row.p95Ms) $p95 "$Context p95Ms"
    Assert-Near ([double]$Row.totalMs) $total "$Context totalMs"
    Assert-Near ([double]$Row.nsPerOp) $nsPerOp "$Context nsPerOp"
}

function Assert-NoBenchmarkProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Context,

        [Parameter(Mandatory = $true)]
        [string]$ProcessName
    )

    $running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $processList = ($running | ForEach-Object { "PID=$($_.Id)" }) -join ', '
        throw "${Context}: 检测到正在运行的 $ProcessName（$processList）。正式矩阵禁止并发，请等待现有 Player 退出。"
    }
}

function ConvertTo-NativeArgument {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    # Windows 文件名不允许双引号；这里同时拒绝非路径参数中的双引号，
    # 避免 Start-Process 的单字符串命令行发生参数边界歧义。
    if ($Value.Contains('"')) {
        throw "命令行参数不能包含双引号: $Value"
    }
    if ($Value.Length -eq 0 -or $Value -match '\s') {
        return '"' + $Value + '"'
    }
    return $Value
}

function Read-CheckpointSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CheckpointPath
    )

    if (-not (Test-Path -LiteralPath $CheckpointPath -PathType Leaf)) {
        return $null
    }

    try {
        $snapshot = Get-Content -LiteralPath $CheckpointPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert-JsonProperty -InputObject $snapshot -PropertyName 'completedCases' -Context $CheckpointPath
        Assert-JsonProperty -InputObject $snapshot -PropertyName 'totalCases' -Context $CheckpointPath
        Assert-JsonProperty -InputObject $snapshot -PropertyName 'failedCases' -Context $CheckpointPath
        Assert-JsonProperty -InputObject $snapshot -PropertyName 'skippedCases' -Context $CheckpointPath
        Assert-JsonProperty -InputObject $snapshot -PropertyName 'runStatus' -Context $CheckpointPath
        return $snapshot
    }
    catch {
        # checkpoint 由 Player 原子替换；读取失败只影响本次进度展示，最终验收仍会严格失败。
        return $null
    }
}

function Wait-BenchmarkProcess {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory = $true)]
        [string]$Shard,

        [Parameter(Mandatory = $true)]
        [int]$ExpectedCases,

        [Parameter(Mandatory = $true)]
        [string]$CheckpointPath,

        [Parameter(Mandatory = $true)]
        [string]$ProcessName
    )

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastCompleted = -1
    $lastHeartbeatSeconds = 0.0

    while (-not $Process.WaitForExit(5000)) {
        $otherPlayers = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
                Where-Object { $_.Id -ne $Process.Id })
        if ($otherPlayers.Count -gt 0) {
            $otherList = ($otherPlayers | ForEach-Object { "PID=$($_.Id)" }) -join ', '
            throw "[$Shard] 运行期间检测到额外 $ProcessName（$otherList），正式矩阵拒绝并发"
        }
        $snapshot = Read-CheckpointSnapshot -CheckpointPath $CheckpointPath
        if ($null -ne $snapshot -and [int]$snapshot.completedCases -ne $lastCompleted) {
            $lastCompleted = [int]$snapshot.completedCases
            Write-Host ("[{0}] 进度 {1}/{2}，失败 {3}，跳过 {4}，状态 {5}，已运行 {6:N0}s" -f `
                    $Shard,
                    $snapshot.completedCases,
                    $snapshot.totalCases,
                    $snapshot.failedCases,
                    $snapshot.skippedCases,
                    $snapshot.runStatus,
                    $watch.Elapsed.TotalSeconds)
            $lastHeartbeatSeconds = $watch.Elapsed.TotalSeconds
        }
        elseif (($watch.Elapsed.TotalSeconds - $lastHeartbeatSeconds) -ge 60.0) {
            Write-Host ("[{0}] 当前用例仍在运行；最近完成 {1}/{2}，已运行 {3:N0}s" -f `
                    $Shard,
                    [Math]::Max(0, $lastCompleted),
                    $ExpectedCases,
                    $watch.Elapsed.TotalSeconds)
            $lastHeartbeatSeconds = $watch.Elapsed.TotalSeconds
        }
    }

    # 确保系统已发布最终退出码。
    $Process.WaitForExit()
    $watch.Stop()
    return $watch.Elapsed
}

function Test-BenchmarkArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Plan,

        [Parameter(Mandatory = $true)]
        [int]$ExitCode,

        [Parameter(Mandatory = $true)]
        [ValidateSet('ReleaseTiming', 'DevelopmentGc')]
        [string]$MeasurementChannel
    )

    Assert-Condition ($ExitCode -eq 0) "[$($Plan.Shard)] Player 退出码应为 0，实际为 $ExitCode；日志: $($Plan.LogPath)"
    Assert-Condition (Test-Path -LiteralPath $Plan.JsonPath -PathType Leaf) "[$($Plan.Shard)] 缺少最终 JSON: $($Plan.JsonPath)"
    Assert-Condition (Test-Path -LiteralPath $Plan.CheckpointPath -PathType Leaf) "[$($Plan.Shard)] 缺少 checkpoint: $($Plan.CheckpointPath)"
    Assert-Condition (Test-Path -LiteralPath $Plan.HtmlPath -PathType Leaf) "[$($Plan.Shard)] 缺少由 JSON stem 派生的 HTML: $($Plan.HtmlPath)"
    Assert-Condition (Test-Path -LiteralPath $Plan.LogPath -PathType Leaf) "[$($Plan.Shard)] 缺少 Player 日志: $($Plan.LogPath)"

    $htmlInfo = Get-Item -LiteralPath $Plan.HtmlPath
    Assert-Condition ($htmlInfo.Length -gt 1000000L) "[$($Plan.Shard)] HTML 过小，疑似未内联 ECharts/数据: $($htmlInfo.Length) bytes"
    $html = Get-Content -LiteralPath $Plan.HtmlPath -Raw -Encoding UTF8
    Assert-Condition ($html.Contains('var ROOT =')) "[$($Plan.Shard)] HTML 缺少内联 ROOT 数据"
    Assert-Condition (-not $html.Contains('@@JSON@@') -and -not $html.Contains('@@ECHARTS@@')) "[$($Plan.Shard)] HTML 残留模板占位符"
    Assert-Condition (([regex]::Matches($html, '<script(?:\s|>)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count -eq 2) "[$($Plan.Shard)] HTML 应恰有两个内联 script"
    Assert-Condition (-not [regex]::IsMatch($html, '<script[^>]+src\s*=', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) "[$($Plan.Shard)] HTML 含外部 script src"
    Assert-Condition (-not [regex]::IsMatch($html, '<link[^>]+href\s*=', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) "[$($Plan.Shard)] HTML 含外部 link href"

    $suite = Get-Content -LiteralPath $Plan.JsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $requiredSuiteProperties = @(
        'runStatus', 'runMode', 'selectionPolicy', 'benchmarkShard', 'measurementChannel', 'timingOnlyRequested',
        'checkpointEnabled', 'checkpointFile', 'totalCases', 'completedCases',
        'failedCases', 'skippedCases', 'gcMetricCalibrated', 'environmentError',
        'startedUtc', 'endedUtc', 'buildGuid', 'buildKind', 'scriptingBackend',
        'collectionsManifestRequest', 'collectionsResolvedVersion', 'burstResolvedVersion',
        'mathematicsResolvedVersion', 'packagesLockSha256', 'results'
    )
    foreach ($propertyName in $requiredSuiteProperties) {
        Assert-JsonProperty -InputObject $suite -PropertyName $propertyName -Context $Plan.JsonPath
    }

    Assert-Condition ($suite.runStatus -ceq 'Completed') "[$($Plan.Shard)] runStatus 应为 Completed，实际为 '$($suite.runStatus)'"
    Assert-Condition ($suite.runMode -ceq 'Shard') "[$($Plan.Shard)] runMode 应为 Shard，实际为 '$($suite.runMode)'"
    Assert-Condition ($suite.selectionPolicy -ceq 'practical-capped-v2') "[$($Plan.Shard)] selectionPolicy 应为 practical-capped-v2，实际为 '$($suite.selectionPolicy)'"
    Assert-Condition ($suite.benchmarkShard -ceq $Plan.Shard) "[$($Plan.Shard)] JSON benchmarkShard 不匹配: '$($suite.benchmarkShard)'"
    Assert-Condition ($suite.scriptingBackend -ceq 'IL2CPP') "[$($Plan.Shard)] scriptingBackend 应为 IL2CPP，实际为 '$($suite.scriptingBackend)'"
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$suite.buildGuid)) "[$($Plan.Shard)] buildGuid 为空"
    foreach ($packageField in $script:expectedPackages.Keys) {
        Assert-Condition ([string]$suite.$packageField -ceq [string]$script:expectedPackages[$packageField]) "[$($Plan.Shard)] $packageField 不匹配: '$($suite.$packageField)'"
    }
    Assert-Condition ([bool]$suite.checkpointEnabled) "[$($Plan.Shard)] checkpointEnabled 应为 true"
    Assert-Condition ([int]$suite.totalCases -eq $Plan.ExpectedCases) "[$($Plan.Shard)] totalCases 应为 $($Plan.ExpectedCases)，实际为 $($suite.totalCases)"
    Assert-Condition ([int]$suite.completedCases -eq $Plan.ExpectedCases) "[$($Plan.Shard)] completedCases 应为 $($Plan.ExpectedCases)，实际为 $($suite.completedCases)"
    Assert-Condition ([int]$suite.failedCases -eq 0) "[$($Plan.Shard)] failedCases 应为 0，实际为 $($suite.failedCases)"
    Assert-Condition ([int]$suite.skippedCases -eq 0) "[$($Plan.Shard)] skippedCases 应为 0，实际为 $($suite.skippedCases)"
    Assert-Condition ([string]::IsNullOrWhiteSpace([string]$suite.environmentError)) "[$($Plan.Shard)] environmentError 非空: $($suite.environmentError)"
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$suite.endedUtc)) "[$($Plan.Shard)] endedUtc 为空"

    $checkpointFullPath = [System.IO.Path]::GetFullPath([string]$suite.checkpointFile)
    Assert-Condition ($checkpointFullPath -ieq $Plan.CheckpointPath) "[$($Plan.Shard)] JSON 内 checkpointFile 与目标不一致: $checkpointFullPath"

    $results = @($suite.results)
    $expectedRows = 2 * $Plan.ExpectedCases
    Assert-Condition ($results.Count -eq $expectedRows) "[$($Plan.Shard)] results 应为 $expectedRows 行（每用例预热+稳态），实际为 $($results.Count)"

    $warmups = @($results | Where-Object { $_.isWarmup -eq $true })
    $steady = @($results | Where-Object { $_.isWarmup -ne $true })
    Assert-Condition ($warmups.Count -eq $Plan.ExpectedCases) "[$($Plan.Shard)] 预热行应为 $($Plan.ExpectedCases)，实际为 $($warmups.Count)"
    Assert-Condition ($steady.Count -eq $Plan.ExpectedCases) "[$($Plan.Shard)] 稳态行应为 $($Plan.ExpectedCases)，实际为 $($steady.Count)"

    foreach ($row in $results) {
        foreach ($propertyName in @('id', 'isWarmup', 'sampleMs', 'gcBytes', 'gcBytesAvailable', 'validated', 'skipped')) {
            Assert-JsonProperty -InputObject $row -PropertyName $propertyName -Context "[$($Plan.Shard)] result"
        }
        Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$row.id)) "[$($Plan.Shard)] 存在空 id 结果行"
        Assert-Condition (-not [bool]$row.skipped) "[$($Plan.Shard)] 结果行 '$($row.id)' 被标记为 skipped"
        Assert-Condition ([bool]$row.validated) "[$($Plan.Shard)] 结果行 '$($row.id)' validated=false"

        $sampleCount = @($row.sampleMs).Count
        if ([bool]$row.isWarmup) {
            Assert-Condition ($sampleCount -eq 1) "[$($Plan.Shard)] 预热行 '$($row.id)' 应有 1 个样本，实际为 $sampleCount"
        }
        else {
            Assert-Condition ($sampleCount -eq 10) "[$($Plan.Shard)] 稳态行 '$($row.id)' 应有 10 个样本，实际为 $sampleCount"
        }
        $rowKind = if ([bool]$row.isWarmup) { 'warmup' } else { 'steady' }
        Assert-RowStatistics -Row $row -Context "[$($Plan.Shard)] '$($row.id)'/$rowKind"

        if ($MeasurementChannel -ceq 'ReleaseTiming') {
            Assert-Condition (-not [bool]$row.gcBytesAvailable) "[$($Plan.Shard)] Release 行 '$($row.id)' 的 GC 应为 N/A"
            Assert-Condition ([long]$row.gcBytes -eq -1L) "[$($Plan.Shard)] Release 行 '$($row.id)' 的 gcBytes 应为 -1，实际为 $($row.gcBytes)"
        }
        else {
            Assert-Condition ([bool]$row.gcBytesAvailable) "[$($Plan.Shard)] Development 行 '$($row.id)' 缺少可用 GC 指标"
            Assert-Condition ([long]$row.gcBytes -ge 0L) "[$($Plan.Shard)] Development 行 '$($row.id)' 的 gcBytes 非法: $($row.gcBytes)"
        }
    }

    $groups = @($results | Group-Object -Property id)
    Assert-Condition ($groups.Count -eq $Plan.ExpectedCases) "[$($Plan.Shard)] 唯一用例 id 应为 $($Plan.ExpectedCases)，实际为 $($groups.Count)"
    foreach ($group in $groups) {
        $groupWarmups = @($group.Group | Where-Object { $_.isWarmup -eq $true })
        $groupSteady = @($group.Group | Where-Object { $_.isWarmup -ne $true })
        Assert-Condition ($group.Count -eq 2 -and $groupWarmups.Count -eq 1 -and $groupSteady.Count -eq 1) "[$($Plan.Shard)] 用例 '$($group.Name)' 不是恰好一条预热加一条稳态"
    }

    if ($MeasurementChannel -ceq 'ReleaseTiming') {
        Assert-Condition ($suite.buildKind -ceq 'Release Player') "[$($Plan.Shard)] Release buildKind 应为 Release Player，实际为 '$($suite.buildKind)'"
        Assert-Condition ($suite.measurementChannel -ceq 'TimingOnly') "[$($Plan.Shard)] Release measurementChannel 应为 TimingOnly，实际为 '$($suite.measurementChannel)'"
        Assert-Condition ([bool]$suite.timingOnlyRequested) "[$($Plan.Shard)] Release timingOnlyRequested 应为 true"
        Assert-Condition (-not [bool]$suite.gcMetricCalibrated) "[$($Plan.Shard)] Release gcMetricCalibrated 应为 false"
    }
    else {
        Assert-Condition ($suite.buildKind -ceq 'Development Player') "[$($Plan.Shard)] Development buildKind 应为 Development Player，实际为 '$($suite.buildKind)'"
        Assert-Condition ($suite.measurementChannel -ceq 'DevelopmentGC') "[$($Plan.Shard)] Development measurementChannel 应为 DevelopmentGC，实际为 '$($suite.measurementChannel)'"
        Assert-Condition (-not [bool]$suite.timingOnlyRequested) "[$($Plan.Shard)] Development timingOnlyRequested 应为 false"
        Assert-Condition ([bool]$suite.gcMetricCalibrated) "[$($Plan.Shard)] Development gcMetricCalibrated 应为 true"
    }

    $finalHash = (Get-FileHash -LiteralPath $Plan.JsonPath -Algorithm SHA256).Hash
    $checkpointHash = (Get-FileHash -LiteralPath $Plan.CheckpointPath -Algorithm SHA256).Hash
    Assert-Condition ($finalHash -ceq $checkpointHash) "[$($Plan.Shard)] final JSON 与 checkpoint 的 SHA256 不一致"

    return [pscustomobject]@{
        Shard       = $Plan.Shard
        Cases       = $Plan.ExpectedCases
        Rows        = $results.Count
        Failed      = [int]$suite.failedCases
        Skipped     = [int]$suite.skippedCases
        Sha256      = $finalHash
        BuildGuid   = [string]$suite.buildGuid
        PackageKey  = (($script:expectedPackages.Keys | ForEach-Object { [string]$suite.$_ }) -join '|')
        Json        = $Plan.JsonPath
        Html        = $Plan.HtmlPath
    }
}

try {
    $matrixMutex = [System.Threading.Mutex]::new($false, 'Local\ContainerBenchmarkFormalMatrix')
    try {
        $matrixMutexOwned = $matrixMutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $matrixMutexOwned = $true
    }
    Assert-Condition $matrixMutexOwned '已有另一个 ContainerBenchmark 正式矩阵运行器持有互斥锁'
    Assert-Condition (Test-Path -LiteralPath $ExePath -PathType Leaf) "Player 不存在: $ExePath"
    $resolvedExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
    Assert-Condition ([System.IO.Path]::GetExtension($resolvedExePath) -ieq '.exe') "ExePath 必须指向 Windows .exe: $resolvedExePath"
    $benchmarkProcessName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedExePath)
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($benchmarkProcessName)) "无法从 ExePath 推导 Player 进程名: $resolvedExePath"
    Assert-NoBenchmarkProcess -Context '启动前检查' -ProcessName $benchmarkProcessName

    $resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
    $matrixManifestPath = [System.IO.Path]::Combine($resolvedOutputRoot, 'matrix-manifest.json')
    if (Test-Path -LiteralPath $resolvedOutputRoot) {
        Assert-Condition (Test-Path -LiteralPath $resolvedOutputRoot -PathType Container) "OutputRoot 已存在但不是目录: $resolvedOutputRoot"
    }

    Assert-Condition ($Shards.Count -gt 0) '至少需要一个分片'
    $seenShards = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($shard in $Shards) {
        Assert-Condition ($seenShards.Add($shard)) "分片列表包含重复项: $shard"
    }

    $channelStem = if ($Channel -ceq 'ReleaseTiming') { 'release-timing' } else { 'development-gc' }
    $plans = @()
    foreach ($shard in $Shards) {
        $shardDirectory = [System.IO.Path]::Combine($resolvedOutputRoot, $shard)
        $stem = "$channelStem-$shard"
        $jsonPath = [System.IO.Path]::Combine($shardDirectory, "$stem.json")
        $checkpointPath = [System.IO.Path]::ChangeExtension($jsonPath, 'checkpoint.json')
        $htmlPath = [System.IO.Path]::ChangeExtension($jsonPath, 'html')
        $logPath = [System.IO.Path]::Combine($shardDirectory, "$stem.log")
        $plans += [pscustomobject]@{
            Shard          = $shard
            ExpectedCases  = [int]$expectedCaseCounts[$shard]
            Directory      = $shardDirectory
            JsonPath       = $jsonPath
            CheckpointPath = $checkpointPath
            HtmlPath       = $htmlPath
            LogPath        = $logPath
        }
    }

    # 在启动第一个 Player 前检查整个矩阵，避免跑到中途才发现后续分片会覆盖旧证据。
    foreach ($plan in $plans) {
        if (Test-Path -LiteralPath $plan.Directory -PathType Leaf) {
            throw "分片输出目录位置已被文件占用: $($plan.Directory)"
        }
        foreach ($targetPath in @($plan.JsonPath, $plan.CheckpointPath, $plan.HtmlPath, $plan.LogPath, "$($plan.CheckpointPath).tmp")) {
            if (Test-Path -LiteralPath $targetPath) {
                throw "拒绝覆盖已有正式产物: $targetPath"
            }
        }
    }
    Assert-Condition (-not (Test-Path -LiteralPath $matrixManifestPath)) "拒绝覆盖已有矩阵清单: $matrixManifestPath"

    New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null
    foreach ($plan in $plans) {
        New-Item -ItemType Directory -Path $plan.Directory -Force | Out-Null
    }

    Write-Host "ContainerBenchmark 正式矩阵"
    Write-Host "  通道: $Channel"
    Write-Host "  Player: $resolvedExePath"
    Write-Host "  输出: $resolvedOutputRoot"
    Write-Host "  分片: $($Shards -join ' -> ')"

    $summaries = @()
    $executableDirectory = [System.IO.Path]::GetDirectoryName($resolvedExePath)
    foreach ($plan in $plans) {
        Assert-NoBenchmarkProcess -Context "[$($plan.Shard)] 启动前检查" -ProcessName $benchmarkProcessName

        $arguments = @(
            '-batchmode',
            '-nographics',
            '-autoRun',
            '-benchmarkShard', $plan.Shard,
            '-benchmarkOutput', $plan.JsonPath,
            '-logFile', $plan.LogPath
        )
        if ($Channel -ceq 'ReleaseTiming') {
            $arguments += '-benchmarkTimingOnly'
        }
        $argumentLine = (($arguments | ForEach-Object { ConvertTo-NativeArgument -Value ([string]$_) }) -join ' ')

        Write-Host ""
        Write-Host "[$($plan.Shard)] 启动：预计 $($plan.ExpectedCases) 个用例" -ForegroundColor Cyan
        $process = Start-Process `
            -FilePath $resolvedExePath `
            -ArgumentList $argumentLine `
            -WorkingDirectory $executableDirectory `
            -WindowStyle Hidden `
            -PassThru

        try {
            if ($process.HasExited) {
                throw "Player 在设置 High 优先级前已退出"
            }
            $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::High
            $process.Refresh()
            Assert-Condition ($process.PriorityClass -eq [System.Diagnostics.ProcessPriorityClass]::High) "无法确认 Player 已设置为 High 优先级"
        }
        catch {
            $priorityFailure = $_.Exception.Message
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
            throw "[$($plan.Shard)] 设置 High 优先级失败，已终止本次启动: $priorityFailure"
        }

        try {
            $elapsed = Wait-BenchmarkProcess `
                -Process $process `
                -Shard $plan.Shard `
                -ExpectedCases $plan.ExpectedCases `
                -CheckpointPath $plan.CheckpointPath `
                -ProcessName $benchmarkProcessName
        }
        catch {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
            throw
        }
        $exitCode = $process.ExitCode

        $summary = Test-BenchmarkArtifacts -Plan $plan -ExitCode $exitCode -MeasurementChannel $Channel
        $summary | Add-Member -NotePropertyName Duration -NotePropertyValue $elapsed
        $summaries += $summary
        Write-Host ("[{0}] 验收通过：{1} 用例 / {2} 行 / SHA256 {3} / {4:c}" -f `
                $plan.Shard,
                $summary.Cases,
                $summary.Rows,
                $summary.Sha256.Substring(0, 12),
                $elapsed) -ForegroundColor Green
    }

    $totalCases = [int](($summaries | Measure-Object -Property Cases -Sum).Sum)
    $totalRows = [int](($summaries | Measure-Object -Property Rows -Sum).Sum)
    $totalFailed = [int](($summaries | Measure-Object -Property Failed -Sum).Sum)
    $totalSkipped = [int](($summaries | Measure-Object -Property Skipped -Sum).Sum)
    $expectedTotalCases = [int](($plans | Measure-Object -Property ExpectedCases -Sum).Sum)
    Assert-Condition ($totalCases -eq $expectedTotalCases) "矩阵汇总用例数应为 $expectedTotalCases，实际为 $totalCases"
    Assert-Condition ($totalRows -eq (2 * $expectedTotalCases)) "矩阵汇总结果行应为 $(2 * $expectedTotalCases)，实际为 $totalRows"
    Assert-Condition ($totalFailed -eq 0) "矩阵汇总失败数应为 0，实际为 $totalFailed"
    Assert-Condition ($totalSkipped -eq 0) "矩阵汇总跳过数应为 0，实际为 $totalSkipped"
    $uniqueBuildGuids = @($summaries | Select-Object -ExpandProperty BuildGuid -Unique)
    Assert-Condition ($uniqueBuildGuids.Count -eq 1) "同一通道所有分片必须来自同一 buildGuid，实际 $($uniqueBuildGuids.Count) 个"
    $uniquePackageKeys = @($summaries | Select-Object -ExpandProperty PackageKey -Unique)
    Assert-Condition ($uniquePackageKeys.Count -eq 1) "同一通道所有分片包快照不一致"

    $playerSha256 = (Get-FileHash -LiteralPath $resolvedExePath -Algorithm SHA256).Hash
    $runnerSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    $manifest = [ordered]@{
        schemaVersion = 'container-benchmark-matrix-v1'
        completedUtc = [DateTime]::UtcNow.ToString('o')
        channel = $Channel
        player = $resolvedExePath
        playerSha256 = $playerSha256
        runnerSha256 = $runnerSha256
        buildGuid = $uniqueBuildGuids[0]
        packageSnapshot = $expectedPackages
        totalCases = $totalCases
        totalRows = $totalRows
        failedCases = $totalFailed
        skippedCases = $totalSkipped
        sources = @($summaries | Select-Object Shard, Cases, Rows, Sha256, BuildGuid, Json, Html)
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $matrixManifestPath -Encoding UTF8

    Write-Host ""
    Write-Host "矩阵全部通过：$totalCases 用例 / $totalRows 行 / $totalFailed fail / $totalSkipped skip" -ForegroundColor Green
    $summaries |
        Select-Object Shard, Cases, Rows, Failed, Skipped,
            @{ Name = 'Duration'; Expression = { $_.Duration.ToString('c') } },
            @{ Name = 'SHA256'; Expression = { $_.Sha256.Substring(0, 12) } },
            Json, Html |
        Format-Table -AutoSize

    $matrixMutex.ReleaseMutex()
    $matrixMutexOwned = $false
    $matrixMutex.Dispose()
    $matrixMutex = $null
}
catch {
    if ($matrixMutexOwned -and $null -ne $matrixMutex) {
        try { $matrixMutex.ReleaseMutex() } catch { }
    }
    if ($null -ne $matrixMutex) {
        $matrixMutex.Dispose()
    }
    Write-Error -ErrorAction Continue ("ContainerBenchmark 矩阵失败: " + $_.Exception.Message)
    exit 1
}

exit 0
