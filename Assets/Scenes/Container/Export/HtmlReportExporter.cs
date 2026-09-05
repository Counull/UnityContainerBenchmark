using System;using System.IO;
using System.Text;
using UnityEngine;

namespace ContainerBenchmark
{
    /// <summary>
    /// 单文件自包含 HTML 报告导出器（阶段 2）：
    /// 把 Assets/StreamingAssets/echarts.min.js 与全部结果 JSON 内联进单个 HTML，
    /// 零外部依赖、双击离线可看。图表（ECharts）：
    /// ①同操作 C# vs Native 柱状对比（ns/op，按规模分组）②耗时随规模对数折线
    /// ③GC 分配散点 ④哈希碰撞三档专项 ⑤预热 vs 稳态对照 ⑥原始数据表（可排序、校验失败标红）。
    /// 输出路径：persistentDataPath/ContainerBenchmarkReport.html。
    /// </summary>
    public static class HtmlReportExporter
    {
        /// <summary>ECharts 库文件名（StreamingAssets 内）。</summary>
        public const string EchartsFileName = "echarts.min.js";

        /// <summary>默认输出路径。</summary>
        public static string DefaultOutputPath =>
            Path.Combine(Application.persistentDataPath, BenchmarkConfig.HtmlReportFileName);

        /// <summary>导出到默认路径，返回文件路径。</summary>
        public static string Export(BenchmarkSuiteResult suite)
        {
            return Export(suite, DefaultOutputPath);
        }

        /// <summary>导出双通道 envelope 到默认路径。</summary>
        public static string Export(BenchmarkReportEnvelope envelope)
        {
            return Export(envelope, DefaultOutputPath);
        }

        /// <summary>导出到指定路径，返回文件路径。</summary>
        public static string Export(BenchmarkSuiteResult suite, string outputPath)
        {
            if (suite == null)
            {
                throw new ArgumentNullException(nameof(suite));
            }
            EnsureNotRunning(suite);
            return ExportJson(UnityEngine.JsonUtility.ToJson(suite), outputPath);
        }

        /// <summary>导出已通过严格门禁的双通道 envelope。</summary>
        public static string Export(BenchmarkReportEnvelope envelope, string outputPath)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }
            if (envelope.report == null)
            {
                throw new ArgumentException("envelope 缺少 report", nameof(envelope));
            }
            if (envelope.diagnostics == null || !envelope.diagnostics.passed)
            {
                throw new InvalidOperationException("双通道 envelope 未通过正式汇总门禁，拒绝导出");
            }
            EnsureNotRunning(envelope.report);
            return ExportJson(UnityEngine.JsonUtility.ToJson(envelope), outputPath);
        }

        private static string ExportJson(string json, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("输出路径不能为空", nameof(outputPath));
            }

            string echarts = LoadEchartsScript();
            string html = BuildHtml(json, echarts);

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(outputPath, html, new UTF8Encoding(false));
            return outputPath;
        }

        private static void EnsureNotRunning(BenchmarkSuiteResult suite)
        {
            if (string.Equals(suite.runStatus, "Running", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("runStatus=Running 的中间结果不能导出正式 HTML");
            }
        }

        /// <summary>读取 StreamingAssets 内的 ECharts；缺失或空文件属于导出失败。</summary>
        private static string LoadEchartsScript()
        {
            string path = Path.Combine(Application.streamingAssetsPath, EchartsFileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "离线报告必须内联 ECharts，但 StreamingAssets 中缺少依赖", path);
            }

            string content = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException("ECharts 文件为空，无法生成可用的离线图表: " + path);
            }
            return content;
        }

        private static string BuildHtml(string json, string echarts)
        {
            // 防止 </script> 提前闭合：JSON/JS 内 </ 统一转义（JS 语义不变）
            string safeJson = json.Replace("</", "<\\/");
            string echartsBlock = echarts.Replace("</", "<\\/");

            return Template
                .Replace("@@ECHARTS@@", echartsBlock)
                .Replace("@@JSON@@", safeJson);
        }

        // =====================================================================
        // HTML 模板：@@ECHARTS@@ 与 @@JSON@@ 由导出时替换。
        // 注意：模板内 JS 字符串统一使用单引号，避免与 C# verbatim 字符串冲突。
        // =====================================================================
        private const string Template = @"<!DOCTYPE html>
<html lang='zh-CN'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Container 容器性能测试报告</title>
<style>
body { background:#17171f; color:#d8d8e0; font-family:'Segoe UI','Microsoft YaHei',sans-serif; margin:0; padding:16px; }
header { border-bottom:1px solid #3a3a48; padding-bottom:10px; margin-bottom:6px; }
h1 { font-size:22px; margin:4px 0; color:#fff; }
h2 { font-size:16px; margin:4px 0 8px; color:#cfe0ff; }
#metaInfo { font-size:13px; color:#9a9aa8; }
#provenanceInfo { font-size:12px; color:#9eb9d8; margin-top:7px; white-space:pre-wrap; }
section { background:#20202c; border:1px solid #33333f; border-radius:8px; padding:14px; margin:14px 0; }
.chart { width:100%; height:420px; }
.controls { margin:8px 0; font-size:13px; color:#b8b8c4; }
.controls label { margin-right:14px; }
select, button, input { background:#2c2c3a; color:#e0e0e8; border:1px solid #44444f; border-radius:4px; padding:4px 8px; font-size:13px; }
button:hover { background:#3a3a4a; cursor:pointer; }
table { border-collapse:collapse; width:100%; font-size:12px; }
th, td { border:1px solid #3a3a48; padding:4px 6px; text-align:right; white-space:nowrap; }
th { background:#2a2a38; cursor:pointer; position:sticky; top:0; user-select:none; }
td.l { text-align:left; }
tr.fail td { color:#ff6b6b; }
tr.skip td { color:#8a8a98; font-style:italic; }
tr.warm td { color:#f5c542; }
.notice { background:#4a2a2a; color:#ffb0b0; border:1px solid #7a3a3a; padding:10px; border-radius:6px; margin:10px 0; }
.warning { background:#493d20; color:#ffe29a; border:1px solid #806b31; padding:10px; border-radius:6px; margin:10px 0; }
.semantic { background:#1d3141; color:#b9dcff; border:1px solid #31536d; padding:9px; border-radius:6px; margin:8px 0; white-space:normal; }
footer { color:#7a7a8a; font-size:12px; margin-top:20px; }
</style>
</head>
<body>
<header>
  <h1>Container 容器性能测试报告</h1>
  <div id='metaInfo'></div>
  <div id='provenanceInfo'></div>
</header>
<div id='echartsNotice' class='notice' style='display:none'></div>
<div id='environmentErrorNotice' class='notice' style='display:none'></div>
<div id='environmentWarningNotice' class='warning' style='display:none'></div>

<section>
  <h2>1. C# vs Native 耗时对比（ns/op，按规模分组）</h2>
  <div class='controls'>
    操作: <select id='selBarOp'></select>
    碰撞: <select id='selBarCol'></select>
    类型: <select id='selBarType'><option value=''>全部类型</option><option value='IntVector3'>int+Vector3</option><option value='StringKey'>字符串Key</option></select>
  </div>
  <div id='chartBar' class='chart'></div>
</section>

<section>
  <h2>2. 耗时随规模变化趋势（ns/op，x 对数轴，1-2-5 序列）</h2>
  <div class='controls'>
    操作: <select id='selLineOp'></select>
    碰撞: <select id='selLineCol'></select>
    类型: <select id='selLineType'><option value=''>全部类型</option><option value='IntVector3'>int+Vector3</option><option value='StringKey'>字符串Key</option></select>
  </div>
  <div id='chartLine' class='chart'></div>
</section>

<section>
  <h2 id='gcSectionTitle'>3. GC 分配对比（散点：x=ns/op 对数轴，y=单次采样分配字节）</h2>
  <div class='controls'>
    类型: <select id='selGcType'><option value=''>全部类型</option><option value='IntVector3'>int+Vector3</option><option value='StringKey'>字符串Key</option></select>
  </div>
  <div id='chartGc' class='chart'></div>
</section>

<section>
  <h2>4. 哈希碰撞三档专项（B/C 组 int-key）</h2>
  <div class='controls'>
    操作: <select id='selColOp'></select>
    规模: <select id='selColScale'></select>
  </div>
  <div id='chartCol' class='chart'></div>
</section>

<section>
  <h2>5. 预热 vs 稳态对照（ms）</h2>
  <div class='controls'>
    操作: <select id='selWarmOp'></select>
    规模: <select id='selWarmScale'></select>
    碰撞: <select id='selWarmCol'></select>
  </div>
  <div id='chartWarm' class='chart'></div>
</section>

<section>
  <h2>6. 字符串 Key 专项（独立表）</h2>
  <div id='stringSemantic' class='semantic'></div>
  <div style='overflow:auto; max-height:420px'>
    <table id='tblString'>
      <thead><tr><th class='l'>操作</th><th class='l'>容器</th><th>规模</th><th>中位(ms)</th><th>均值(ms)</th><th>p95(ms)</th><th>总耗时(ms)</th><th>ns/op</th><th id='stringGcHeader'>GC(B)</th><th class='l'>校验</th><th class='l'>语义说明</th></tr></thead>
      <tbody></tbody>
    </table>
  </div>
</section>

<section>
  <h2>7. 原始数据表（点击列头排序；校验失败标红；预热黄色；跳过灰色）</h2>
  <div class='controls'>
    搜索: <input id='txtSearch' type='text' placeholder='容器/操作/用例ID' style='width:220px'>
    <label><input type='checkbox' id='chkWarmup' checked> 含预热样本</label>
    <label><input type='checkbox' id='chkSkipped'> 含跳过记录</label>
    <label><input type='checkbox' id='chkJobOnly'> 仅Job</label>
    每页
    <select id='selPage'><option>50</option><option>100</option><option>200</option></select>
    条
    <button id='btnPrev'>上一页</button>
    <button id='btnNext'>下一页</button>
    <span id='pageInfo'></span>
  </div>
  <div style='overflow:auto; max-height:600px'>
    <table id='tbl'><thead></thead><tbody></tbody></table>
  </div>
</section>

<footer><span id='genInfo'></span></footer>

<script>
@@ECHARTS@@
</script>
<script>
(function () {
  var ROOT = @@JSON@@;
  var ENVELOPE = ROOT && ROOT.report ? ROOT : null;
  var DATA = ENVELOPE ? ENVELOPE.report : ROOT;
  var HAS_ECHARTS = (typeof echarts !== 'undefined');
  var rows = DATA.results || [];

  // ---------------- 工具函数 ----------------
  function steady() { return rows.filter(function (r) { return !r.isWarmup && !r.skipped; }); }
  function warmups() { return rows.filter(function (r) { return r.isWarmup && !r.skipped; }); }
  function skippedRows() { return rows.filter(function (r) { return r.skipped; }); }
  function hasSamples(r) { return Array.isArray(r.sampleMs) && r.sampleMs.length > 0; }
  function hasGc(r) {
    var explicitlyReported = Object.prototype.hasOwnProperty.call(r, 'gcBytesAvailable');
    var available = explicitlyReported ? r.gcBytesAvailable === true : DATA.gcMetricCalibrated === true;
    return available && isFinite(r.gcBytes) && r.gcBytes >= 0;
  }
  function timingChartableSteady() {
    return steady().filter(function (r) {
      return r.validated && hasSamples(r) && isFinite(r.medianMs) && r.medianMs > 0 &&
        isFinite(r.nsPerOp) && r.nsPerOp > 0;
    });
  }
  function gcChartableSteady() { return timingChartableSteady().filter(hasGc); }
  function chartableWarmups() {
    return warmups().filter(function (r) {
      return r.validated && hasSamples(r) && isFinite(r.medianMs) && r.medianMs > 0;
    });
  }
  function uniq(arr) { return arr.filter(function (v, i) { return arr.indexOf(v) === i; }); }
  function uniqueMessages(values) {
    var result = [];
    values.forEach(function (value) {
      var message = String(value || '').trim();
      if (!message) { return; }
      var duplicateIndex = result.findIndex(function (existing) {
        return existing === message || existing.indexOf(message) >= 0 || message.indexOf(existing) >= 0;
      });
      if (duplicateIndex < 0) { result.push(message); }
      else if (message.length > result[duplicateIndex].length) { result[duplicateIndex] = message; }
    });
    return result;
  }
  function num(a, b) { return a - b; }
  function fmtMs(v) { return v >= 100 ? v.toFixed(1) : (v >= 1 ? v.toFixed(2) : v.toFixed(3)); }
  function fmtNsOp(v) {
    if (v >= 1e6) { return (v / 1e6).toFixed(2) + ' ms'; }
    if (v >= 1e3) { return (v / 1e3).toFixed(1) + ' us'; }
    return v.toFixed(1) + ' ns';
  }
  function fmtBytes(v) {
    if (!isFinite(v) || v < 0) { return 'N/A'; }
    if (v >= 1e6) { return (v / 1e6).toFixed(2) + ' MB'; }
    if (v >= 1e3) { return (v / 1e3).toFixed(1) + ' KB'; }
    return v + ' B';
  }
  function fmtSamples(values) {
    return Array.isArray(values) ? values.map(fmtMs).join(', ') : '';
  }
  function fmtScale(s) {
    if (s >= 1e6) { return (s / 1e6) + 'M'; }
    if (s >= 1e3) { return (s / 1e3) + 'K'; }
    return '' + s;
  }
  var COL_LABEL = { Normal: '正常散列', LimitedDomain: '有限域(N/16哈希值,约16 key/hash)', AllCollision: '全碰撞' };
  function colLabel(c) { return COL_LABEL[c] || c; }
  function typeLabel(t) { return t === 'IntVector3' ? 'int+Vector3' : (t === 'StringKey' ? '字符串Key' : t); }
  function comparisonOp(r) { return r.family === 'StackApprox' ? 'F1/F2' : r.operationCode; }
  function matchesComparisonOp(r, op) { return comparisonOp(r) === op; }
  function esc(s) {
    return (s === null || s === undefined ? '' : String(s))
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/""/g, '&quot;').replace(/'/g, '&#39;');
  }

  // ---------------- 元信息 ----------------
  function fillMeta() {
    var s = steady(), w = warmups(), sk = skippedRows();
    var failCount = uniq(rows.filter(function (r) { return !r.skipped && !r.validated; })
      .map(function (r) { return r.id; })).length;
    var inferredCases = s.length + sk.length;
    var totalCases = DATA.totalCases > 0 ? DATA.totalCases : inferredCases;
    var completedCases = DATA.totalCases > 0 ? DATA.completedCases : inferredCases;
    var environmentFailed = DATA.runStatus === 'EnvironmentFailed';
    var runFailed = !!DATA.runStatus && DATA.runStatus !== 'Completed';
    var statusText = esc(DATA.runStatus || '未记录') + (runFailed ? '（失败）' : '');
    var reportedFailures = DATA.failedCases || failCount || (environmentFailed ? 1 : 0);
    var gcState = DATA.gcMetricCalibrated
      ? esc(DATA.gcCalibrationBytes + ' B')
      : ((DATA.measurementChannel === 'TimingOnly' || DATA.timingOnlyRequested || DATA.gcUnavailableReason) ? 'N/A' : '失败');
    var packageText = 'Collections manifest ' + esc(DATA.collectionsManifestRequest || '未记录') +
      ' → resolved ' + esc(DATA.collectionsResolvedVersion || '未记录') +
      ' | Burst ' + esc(DATA.burstResolvedVersion || '未记录') +
      ' | Mathematics ' + esc(DATA.mathematicsResolvedVersion || '未记录') +
      ' | packages-lock SHA256 ' + esc(DATA.packagesLockSha256 ? DATA.packagesLockSha256.substring(0, 12) + '…' : '未记录');
    document.getElementById('metaInfo').innerHTML =
      '套件: ' + esc(DATA.suiteName) +
      ' | Unity: ' + esc(DATA.unityVersion) +
      ' | 平台: ' + esc(DATA.platform || '未记录') +
      ' | 后端: ' + esc(DATA.scriptingBackend || '未记录') +
      ' | 构建: ' + esc(DATA.buildKind || '未记录') +
      ' | 范围: ' + esc(DATA.runMode || '未记录') +
      ' | 分片: ' + esc(DATA.benchmarkShard || 'full') +
      ' | 选择策略: ' + esc(DATA.selectionPolicy || '未记录') +
      ' | 测量通道: ' + esc(DATA.measurementChannel || '未记录') +
      ' | 运行状态: ' + statusText +
      ' | 进度: ' + completedCases + '/' + totalCases +
      '（失败 ' + reportedFailures + '，跳过 ' + (DATA.skippedCases || sk.length) + '）' +
      ' | Job维度: ' + (DATA.jobEnabled ? '开' : '关') +
      ' | GC校准: ' + gcState +
      ' | 稳态记录: ' + s.length +
      ' | 预热记录: ' + w.length +
      ' | 校验失败: ' + failCount +
      ' | 跳过: ' + sk.length +
      ' | 开始: ' + esc(DATA.startedUtc) +
      ' | 结束: ' + esc(DATA.endedUtc) +
      '<br>' + packageText;
    if (DATA.environmentError || environmentFailed) {
      var envNotice = document.getElementById('environmentErrorNotice');
      envNotice.style.display = 'block';
      envNotice.textContent = environmentFailed
        ? '运行失败（EnvironmentFailed）：' + (DATA.environmentError || '环境门未通过')
        : DATA.environmentError;
    }
    var warnings = uniqueMessages([DATA.environmentWarning, DATA.gcUnavailableReason])
      .filter(function (x) {
        if (!DATA.environmentError) { return true; }
        return x !== DATA.environmentError && x.indexOf(DATA.environmentError) < 0 && DATA.environmentError.indexOf(x) < 0;
      });
    if (warnings.length > 0) {
      var warningNotice = document.getElementById('environmentWarningNotice');
      warningNotice.style.display = 'block';
      warningNotice.textContent = warnings.join(' | ');
    }
    if (ENVELOPE) {
      var d = ENVELOPE.diagnostics || {};
      var sources = (ENVELOPE.provenance && ENVELOPE.provenance.sources) || [];
      document.getElementById('provenanceInfo').textContent =
        '双通道来源：耗时 = IL2CPP Release Player 稳态 10 次采样中位数；' +
        'GC = IL2CPP Development Player 稳态 10 次采样分配中位数。\n' +
        '严格门禁：sources ' + (d.sourceCount || 0) + '/' + (d.expectedSources || 6) +
        '，IDs ' + (d.uniqueCases || 0) + '/' + (d.expectedCases || 928) +
        '，rows ' + (d.mergedRows || 0) + '/' + (d.expectedRows || 1856) +
        '，missing ' + (d.missingCases || 0) + '，duplicate ' + (d.duplicateCases || 0) +
        '，mismatch ' + (d.mismatchCases || 0) +
        '；Release build ' + String((ENVELOPE.provenance || {}).timingBuildGuid || '-') +
        '，Development build ' + String((ENVELOPE.provenance || {}).gcBuildGuid || '-') +
        '；输入/Checkpoint SHA256 ' +
        sources.map(function (x) {
          return x.role + '/' + x.shard + '=' + String(x.sha256 || '').substring(0, 12) +
            '/' + String(x.checkpointSha256 || '').substring(0, 12);
        }).join('，');
      document.getElementById('gcSectionTitle').textContent =
        '3. GC 分配对比（IL2CPP Development Player 稳态 10 次采样中位数）';
      document.getElementById('stringGcHeader').textContent = 'GC(B，Dev 10次中位)';
    }
    document.getElementById('genInfo').textContent =
      '环境: ' + esc(DATA.operatingSystem || '-') + ' | CPU: ' + esc(DATA.processorType || '-') +
      ' (' + (DATA.processorCount || '-') + ' logical) | RAM: ' + (DATA.systemMemoryMB || '-') +
      ' MB | GPU: ' + esc(DATA.graphicsDeviceName || '-') + ' / ' + esc(DATA.graphicsDeviceType || '-') +
      ' / ' + (DATA.graphicsMemoryMB || '-') + ' MB | Build GUID: ' + esc(DATA.buildGuid || '-') +
      ' | 本文件生成于 ' + new Date().toLocaleString() + '（echarts 与数据均已内联，可离线查看）';
  }

  if (!HAS_ECHARTS) {
    var n = document.getElementById('echartsNotice');
    n.style.display = 'block';
    n.innerHTML = '警告：echarts.min.js 未内联（StreamingAssets 缺少该文件），图表不可用，原始数据表仍可查看。<br>' +
      '下载命令（PowerShell）: Invoke-WebRequest -Uri \'https://cdn.jsdelivr.net/npm/echarts@5.5.1/dist/echarts.min.js\' -OutFile \'Assets/StreamingAssets/echarts.min.js\'';
  }

  // ---------------- ECharts 基础 ----------------
  var charts = {};
  function getChart(id) {
    if (!HAS_ECHARTS) { return null; }
    if (!charts[id]) { charts[id] = echarts.init(document.getElementById(id)); }
    return charts[id];
  }
  function baseOpt() {
    return {
      backgroundColor: 'transparent',
      textStyle: { color: '#d8d8e0' },
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { textStyle: { color: '#b8b8c4' }, type: 'scroll', top: 4 },
      grid: { left: 90, right: 30, top: 64, bottom: 56 }
    };
  }
  var PALETTE = ['#5470c6', '#91cc75', '#fac858', '#ee6666', '#73c0de', '#3ba272', '#fc8452', '#9a60b4', '#ea7ccc'];
  function colorOf(i) { return PALETTE[i % PALETTE.length]; }
  function logAxis(name) {
    return { type: 'log', name: name, nameTextStyle: { color: '#9a9aa8' }, axisLabel: { color: '#9a9aa8' } };
  }
  function catAxis(data) {
    return { type: 'category', data: data, axisLabel: { color: '#9a9aa8', interval: 0, rotate: data.length > 6 ? 30 : 0 } };
  }
  function noDataTitle(chartId, opt, message) {
    var c = getChart(chartId);
    if (!c) { return; }
    if (!opt.series || opt.series.length === 0) {
      opt.title = { text: message, left: 'center', top: 'middle', textStyle: { color: '#8a8a98', fontSize: 15 } };
    }
    c.setOption(opt, true);
  }

  // ---------------- 1. C# vs Native 柱状对比 ----------------
  function buildBarOptions(op, type, col) {
    var rs = timingChartableSteady().filter(function (r) {
      return matchesComparisonOp(r, op) && r.collision === col && (type === '' || r.valueType === type);
    });
    var scales = uniq(rs.map(function (r) { return r.scale; })).sort(num);
    var managed = uniq(rs.filter(function (r) { return !r.isNative; }).map(function (r) { return r.container; }));
    var native = uniq(rs.filter(function (r) { return r.isNative; }).map(function (r) { return r.container; }));
    var containers = managed.concat(native);
    var opt = baseOpt();
    opt.title = { text: op + ' C# vs Native 耗时对比（' + colLabel(col) + '，ns/op）', left: 10, top: 2, textStyle: { color: '#eee', fontSize: 15 } };
    opt.xAxis = catAxis(scales.map(fmtScale));
    opt.yAxis = logAxis('ns/op');
    opt.series = containers.map(function (c, i) {
      return {
        name: c, type: 'bar', barGap: '15%', itemStyle: { color: colorOf(i) },
        data: scales.map(function (s) {
          var r = rs.find(function (x) { return x.scale === s && x.container === c; });
          return r ? Number(r.nsPerOp.toFixed(1)) : null;
        })
      };
    });
    return opt;
  }

  // ---------------- 2. 趋势折线（x 对数轴） ----------------
  function buildLineOptions(op, col, type) {
    var rs = timingChartableSteady().filter(function (r) {
      return matchesComparisonOp(r, op) && r.collision === col && (type === '' || r.valueType === type);
    });
    var containers = uniq(rs.map(function (r) { return r.container; }));
    var opt = baseOpt();
    opt.title = { text: op + ' 耗时随规模变化趋势（x 对数轴）', left: 10, top: 2, textStyle: { color: '#eee', fontSize: 15 } };
    opt.xAxis = logAxis('规模 N');
    opt.yAxis = logAxis('ns/op');
    opt.series = containers.map(function (c, i) {
      var pts = rs.filter(function (r) { return r.container === c; })
        .sort(function (a, b) { return a.scale - b.scale; })
        .map(function (r) { return [r.scale, Number(r.nsPerOp.toFixed(1))]; });
      return { name: c, type: 'line', smooth: false, symbolSize: 7, itemStyle: { color: colorOf(i) }, data: pts };
    });
    return opt;
  }

  // ---------------- 3. GC 分配散点 ----------------
  function buildGcOptions(type) {
    var rs = gcChartableSteady().filter(function (r) { return type === '' || r.valueType === type; });
    var containers = uniq(rs.map(function (r) { return r.container; }));
    var opt = baseOpt();
    opt.title = {
      text: ENVELOPE
        ? 'GC 分配对比（Development Player 稳态 10 次采样中位数）'
        : 'GC 分配对比（x=ns/op，y=单次采样分配字节）',
      left: 10, top: 2, textStyle: { color: '#eee', fontSize: 15 }
    };
    opt.tooltip = {
      trigger: 'item',
      formatter: function (p) {
        var d = p.data;
        return esc(d.id) + '<br>容器: ' + esc(d.c) + ' | 操作: ' + esc(d.op) + '<br>规模: ' + fmtScale(d.scale) +
          ' | 碰撞: ' + esc(colLabel(d.col)) + '<br>ns/op: ' + fmtNsOp(d.value[0]) +
          ' | GC: ' + fmtBytes(d.value[1]) + '<br>校验: ' + (d.valid ? '通过' : '失败');
      }
    };
    opt.xAxis = logAxis('ns/op');
    opt.yAxis = { type: 'value', name: 'GC 分配', nameTextStyle: { color: '#9a9aa8' }, axisLabel: { color: '#9a9aa8' } };
    opt.series = containers.map(function (c, i) {
      return {
        name: c, type: 'scatter', symbolSize: 9, itemStyle: { color: colorOf(i), opacity: 0.8 },
        data: rs.filter(function (r) { return r.container === c; }).map(function (r) {
          return {
            value: [Number(r.nsPerOp.toFixed(1)), r.gcBytes],
            id: r.id, c: r.container, op: r.operation, scale: r.scale, col: r.collision, valid: r.validated
          };
        })
      };
    });
    return opt;
  }

  // ---------------- 4. 碰撞三档专项 ----------------
  function buildCollisionOptions(op, scale) {
    var rs = timingChartableSteady().filter(function (r) {
      return r.operationCode === op && r.scale === scale && r.valueType === 'IntVector3' &&
        (r.family === 'Dictionary' || r.family === 'HashSet');
    });
    var profiles = ['Normal', 'LimitedDomain', 'AllCollision'];
    var containers = uniq(rs.map(function (r) { return r.container; }));
    var opt = baseOpt();
    opt.title = { text: op + ' 哈希碰撞三档专项（规模 ' + fmtScale(scale) + '，ns/op）', left: 10, top: 2, textStyle: { color: '#eee', fontSize: 15 } };
    opt.xAxis = catAxis(profiles.map(colLabel));
    opt.yAxis = logAxis('ns/op');
    opt.series = containers.map(function (c, i) {
      return {
        name: c, type: 'bar', barGap: '15%', itemStyle: { color: colorOf(i) },
        data: profiles.map(function (p) {
          var r = rs.find(function (x) { return x.container === c && x.collision === p; });
          return r ? Number(r.nsPerOp.toFixed(1)) : null;
        })
      };
    });
    return opt;
  }

  // ---------------- 5. 预热 vs 稳态 ----------------
  function buildWarmOptions(op, scale, col) {
    var srows = timingChartableSteady().filter(function (r) { return matchesComparisonOp(r, op) && r.scale === scale && r.collision === col; });
    var wrows = chartableWarmups().filter(function (r) { return matchesComparisonOp(r, op) && r.scale === scale && r.collision === col; });
    var containers = uniq(srows.map(function (r) { return r.container; }));
    var opt = baseOpt();
    opt.title = { text: op + ' 预热 vs 稳态（规模 ' + fmtScale(scale) + '，ms）', left: 10, top: 2, textStyle: { color: '#eee', fontSize: 15 } };
    opt.xAxis = catAxis(containers);
    opt.yAxis = logAxis('ms');
    opt.series = [
      {
        name: '预热(1次)', type: 'bar', itemStyle: { color: '#ee6666' },
        data: containers.map(function (c) {
          var r = wrows.find(function (x) { return x.container === c; });
          return r ? Number(r.medianMs.toFixed(3)) : null;
        })
      },
      {
        name: '稳态(中位数)', type: 'bar', itemStyle: { color: '#5470c6' },
        data: containers.map(function (c) {
          var r = srows.find(function (x) { return x.container === c; });
          return r ? Number(r.medianMs.toFixed(3)) : null;
        })
      }
    ];
    return opt;
  }

  // ---------------- 图表刷新 ----------------
  function refreshBar() {
    var c = getChart('chartBar');
    if (c) { c.setOption(buildBarOptions(selBarOp.value, selBarType.value, selBarCol.value), true); }
  }
  function refreshLine() {
    var c = getChart('chartLine');
    if (c) { c.setOption(buildLineOptions(selLineOp.value, selLineCol.value, selLineType.value), true); }
  }
  function refreshGc() {
    noDataTitle('chartGc', buildGcOptions(selGcType.value),
      DATA.gcUnavailableReason ? 'GC 指标不可用（N/A）：' + DATA.gcUnavailableReason : 'GC 指标不可用（N/A）');
  }
  function refreshCol() {
    var c = getChart('chartCol');
    if (c) { c.setOption(buildCollisionOptions(selColOp.value, Number(selColScale.value)), true); }
  }
  function refreshWarm() {
    var c = getChart('chartWarm');
    if (c) { c.setOption(buildWarmOptions(selWarmOp.value, Number(selWarmScale.value), selWarmCol.value), true); }
  }

  // ---------------- 下拉框填充 ----------------
  var selBarOp = document.getElementById('selBarOp');
  var selBarCol = document.getElementById('selBarCol');
  var selBarType = document.getElementById('selBarType');
  var selLineOp = document.getElementById('selLineOp');
  var selLineCol = document.getElementById('selLineCol');
  var selLineType = document.getElementById('selLineType');
  var selGcType = document.getElementById('selGcType');
  var selColOp = document.getElementById('selColOp');
  var selColScale = document.getElementById('selColScale');
  var selWarmOp = document.getElementById('selWarmOp');
  var selWarmScale = document.getElementById('selWarmScale');
  var selWarmCol = document.getElementById('selWarmCol');

  function fillSelect(sel, items) {
    sel.innerHTML = items.map(function (it) {
      return '<option value=\'' + esc(it.v) + '\'>' + esc(it.l) + '</option>';
    }).join('');
  }
  function opItems() {
    var map = {};
    timingChartableSteady().forEach(function (r) {
      var key = comparisonOp(r);
      if (!map[key]) { map[key] = key === 'F1/F2' ? 'F1/F2 栈Push/Pop（List vs NativeList）' : r.operationCode + ' ' + r.operation; }
    });
    return Object.keys(map).sort().map(function (k) { return { v: k, l: map[k] }; });
  }
  function colItems() {
    return ['Normal', 'LimitedDomain', 'AllCollision'].map(function (c) { return { v: c, l: colLabel(c) }; });
  }
  function hashOpItems() {
    var map = {};
    timingChartableSteady().forEach(function (r) {
      if (r.valueType === 'IntVector3' && (r.family === 'Dictionary' || r.family === 'HashSet')) {
        if (!map[r.operationCode]) { map[r.operationCode] = r.operationCode + ' ' + r.operation; }
      }
    });
    return Object.keys(map).sort().map(function (k) { return { v: k, l: map[k] }; });
  }
  function scaleItemsOf(rs) {
    return uniq(rs.map(function (r) { return r.scale; })).sort(num).map(function (s) { return { v: s, l: fmtScale(s) }; });
  }

  function refreshWarmScales() {
    var rs = timingChartableSteady().filter(function (r) {
      return matchesComparisonOp(r, selWarmOp.value) && r.collision === selWarmCol.value;
    });
    fillSelect(selWarmScale, scaleItemsOf(rs));
    refreshWarm();
  }
  function refreshColScales() {
    var rs = timingChartableSteady().filter(function (r) { return r.operationCode === selColOp.value && r.valueType === 'IntVector3'; });
    fillSelect(selColScale, scaleItemsOf(rs));
    refreshCol();
  }

  // ---------------- 6. 字符串专项独立表 ----------------
  function renderStringTable() {
    var rs = steady().filter(function (r) { return r.valueType === 'StringKey'; })
      .sort(function (a, b) {
        return a.operationCode.localeCompare(b.operationCode) || a.scale - b.scale || a.container.localeCompare(b.container);
      });
    var notes = uniq(rs.map(function (r) { return r.semanticNote; }).filter(function (n) { return !!n; }));
    document.getElementById('stringSemantic').textContent = notes.length > 0
      ? notes.join('；')
      : 'Dictionary<string,int> 使用引用类型字符串；NativeHashMap 使用 FixedString64Bytes，存储布局与哈希算法不同，结果不可视为完全同语义替换。';
    document.getElementById('tblString').querySelector('tbody').innerHTML = rs.map(function (r) {
      var cls = r.validated ? '' : 'fail';
      return '<tr class=\'' + cls + '\'>' +
        '<td class=\'l\'>' + esc(r.operationCode + ' ' + r.operation) + '</td>' +
        '<td class=\'l\'>' + esc(r.container) + '</td>' +
        '<td>' + fmtScale(r.scale) + '</td>' +
        '<td>' + (hasSamples(r) ? fmtMs(r.medianMs) : '-') + '</td>' +
        '<td>' + (hasSamples(r) ? fmtMs(r.meanMs) : '-') + '</td>' +
        '<td>' + (hasSamples(r) ? fmtMs(r.p95Ms) : '-') + '</td>' +
        '<td>' + (hasSamples(r) ? fmtMs(r.totalMs) : '-') + '</td>' +
        '<td>' + (hasSamples(r) ? fmtNsOp(r.nsPerOp) : '-') + '</td>' +
        '<td>' + (hasSamples(r) ? (hasGc(r) ? fmtBytes(r.gcBytes) : 'N/A') : '-') + '</td>' +
        '<td class=\'l\'>' + esc(r.validated ? '通过' : r.validateDesc) + '</td>' +
        '<td class=\'l\'>' + esc(r.semanticNote) + '</td></tr>';
    }).join('');
  }

  // ---------------- 7. 原始数据表 ----------------
  var tableState = { sortKey: 'nsPerOp', asc: true, page: 0, pageSize: 50 };
  var COLUMNS = [
    { key: 'container', label: '容器', left: true },
    { key: 'operationCode', label: '操作', left: true },
    { key: 'scale', label: '规模', left: false },
    { key: 'valueType', label: '类型', left: true },
    { key: 'collision', label: '碰撞', left: true },
    { key: 'useJob', label: 'Job', left: false },
    { key: 'medianMs', label: '中位(ms)', left: false },
    { key: 'meanMs', label: '均值(ms)', left: false },
    { key: 'p95Ms', label: 'p95(ms)', left: false },
    { key: 'totalMs', label: '总耗时(ms)', left: false },
    { key: 'timedOperationCount', label: '计时操作数', left: false },
    { key: 'nsPerOp', label: 'ns/op', left: false },
    { key: 'gcBytes', label: ENVELOPE ? 'GC(B，Dev 10次中位)' : 'GC(B)', left: false },
    { key: 'validated', label: '校验', left: true },
    { key: 'sampleMs', label: '原始样本(ms)', left: true }
  ];
  function tableRows() {
    var list = steady().concat(skippedRows());
    if (document.getElementById('chkWarmup').checked) { list = list.concat(warmups()); }
    if (!document.getElementById('chkSkipped').checked) { list = list.filter(function (r) { return !r.skipped; }); }
    if (document.getElementById('chkJobOnly').checked) { list = list.filter(function (r) { return r.useJob; }); }
    var q = document.getElementById('txtSearch').value.trim().toLowerCase();
    if (q) {
      list = list.filter(function (r) {
        return (r.container + ' ' + r.operation + ' ' + r.id + ' ' + r.operationCode).toLowerCase().indexOf(q) >= 0;
      });
    }
    var key = tableState.sortKey, asc = tableState.asc;
    list.sort(function (a, b) {
      if (key === 'gcBytes') {
        var ag = hasGc(a), bg = hasGc(b);
        if (ag !== bg) { return ag ? -1 : 1; }
      }
      var va = a[key], vb = b[key];
      if (typeof va === 'number' && typeof vb === 'number') { return asc ? va - vb : vb - va; }
      va = String(va === null || va === undefined ? '' : va);
      vb = String(vb === null || vb === undefined ? '' : vb);
      var cmp = va.localeCompare(vb);
      return asc ? cmp : -cmp;
    });
    return list;
  }
  function renderTable() {
    var list = tableRows();
    var head = document.getElementById('tbl').querySelector('thead');
    var body = document.getElementById('tbl').querySelector('tbody');
    var headHtml = '<tr>' + COLUMNS.map(function (col) {
      var arrow = '';
      if (tableState.sortKey === col.key) { arrow = tableState.asc ? ' ▲' : ' ▼'; }
      return '<th data-key=\'' + col.key + '\'>' + col.label + arrow + '</th>';
    }).join('') + '<th class=\'l\'>说明</th></tr>';
    head.innerHTML = headHtml;
    head.querySelectorAll('th[data-key]').forEach(function (th) {
      th.addEventListener('click', function () {
        var k = th.getAttribute('data-key');
        if (tableState.sortKey === k) { tableState.asc = !tableState.asc; }
        else { tableState.sortKey = k; tableState.asc = (k === 'container' || k === 'operationCode' || k === 'validated'); }
        tableState.page = 0;
        renderTable();
      });
    });

    var pageSize = tableState.pageSize;
    var pageCount = Math.max(1, Math.ceil(list.length / pageSize));
    if (tableState.page >= pageCount) { tableState.page = pageCount - 1; }
    var start = tableState.page * pageSize;
    var slice = list.slice(start, start + pageSize);
    var bodyHtml = slice.map(function (r) {
      var cls = r.skipped ? 'skip' : (!r.validated ? 'fail' : (r.isWarmup ? 'warm' : ''));
      var opCell = r.operationCode + ' ' + r.operation;
      var desc = r.skipped ? r.skipReason : r.validateDesc;
      var jobCell = r.useJob ? '是' : '否';
      var validCell = r.skipped ? '跳过' : (r.validated ? (r.isWarmup ? '预热通过' : '通过') : (r.isWarmup ? '预热失败' : '失败'));
      var hasMetrics = hasSamples(r);
      var cells = [
        '<td class=\'l\'>' + esc(r.container) + '</td>',
        '<td class=\'l\'>' + esc(opCell) + '</td>',
        '<td>' + fmtScale(r.scale) + '</td>',
        '<td class=\'l\'>' + esc(typeLabel(r.valueType)) + '</td>',
        '<td class=\'l\'>' + esc(colLabel(r.collision)) + '</td>',
        '<td>' + jobCell + '</td>',
        '<td>' + (hasMetrics ? fmtMs(r.medianMs) : '-') + '</td>',
        '<td>' + (hasMetrics ? fmtMs(r.meanMs) : '-') + '</td>',
        '<td>' + (hasMetrics ? fmtMs(r.p95Ms) : '-') + '</td>',
        '<td>' + (hasMetrics ? fmtMs(r.totalMs) : '-') + '</td>',
        '<td>' + (r.timedOperationCount || r.scale) + '</td>',
        '<td>' + (hasMetrics ? fmtNsOp(r.nsPerOp) : '-') + '</td>',
        '<td>' + (hasMetrics ? (hasGc(r) ? fmtBytes(r.gcBytes) : 'N/A') : '-') + '</td>',
        '<td class=\'l\'>' + validCell + '</td>',
        '<td class=\'l\'>' + esc(fmtSamples(r.sampleMs)) + '</td>',
        '<td class=\'l\'>' + esc(desc) + '</td>'
      ];
      return '<tr class=\'' + cls + '\'>' + cells.join('') + '</tr>';
    }).join('');
    body.innerHTML = bodyHtml;
    document.getElementById('pageInfo').textContent =
      ' 第 ' + (tableState.page + 1) + '/' + pageCount + ' 页，共 ' + list.length + ' 条';
  }

  // ---------------- 初始化 ----------------
  fillSelect(selBarOp, opItems());
  fillSelect(selBarCol, colItems());
  fillSelect(selLineOp, opItems());
  fillSelect(selLineCol, colItems());
  fillSelect(selWarmOp, opItems());
  fillSelect(selWarmCol, colItems());
  fillSelect(selColOp, hashOpItems());
  refreshWarmScales();
  refreshColScales();

  selBarOp.addEventListener('change', refreshBar);
  selBarCol.addEventListener('change', refreshBar);
  selBarType.addEventListener('change', function () {
    if (selBarType.value === 'StringKey') { selBarCol.value = 'Normal'; }
    refreshBar();
  });
  selLineOp.addEventListener('change', refreshLine);
  selLineCol.addEventListener('change', refreshLine);
  selLineType.addEventListener('change', refreshLine);
  selGcType.addEventListener('change', refreshGc);
  selColOp.addEventListener('change', refreshColScales);
  selColScale.addEventListener('change', refreshCol);
  selWarmOp.addEventListener('change', refreshWarmScales);
  selWarmCol.addEventListener('change', refreshWarmScales);
  selWarmScale.addEventListener('change', refreshWarm);
  document.getElementById('btnPrev').addEventListener('click', function () {
    if (tableState.page > 0) { tableState.page--; renderTable(); }
  });
  document.getElementById('btnNext').addEventListener('click', function () {
    var pageCount = Math.max(1, Math.ceil(tableRows().length / tableState.pageSize));
    if (tableState.page < pageCount - 1) { tableState.page++; renderTable(); }
  });
  document.getElementById('selPage').addEventListener('change', function () {
    tableState.pageSize = Number(this.value);
    tableState.page = 0;
    renderTable();
  });
  document.getElementById('chkWarmup').addEventListener('change', function () { tableState.page = 0; renderTable(); });
  document.getElementById('chkSkipped').addEventListener('change', function () { tableState.page = 0; renderTable(); });
  document.getElementById('chkJobOnly').addEventListener('change', function () { tableState.page = 0; renderTable(); });
  document.getElementById('txtSearch').addEventListener('input', function () { tableState.page = 0; renderTable(); });

  fillMeta();
  renderStringTable();
  renderTable();
  refreshBar();
  refreshLine();
  refreshGc();
  refreshCol();
  refreshWarm();

  window.addEventListener('resize', function () {
    Object.keys(charts).forEach(function (k) { charts[k].resize(); });
  });
})();
</script>
</body>
</html>
";
    }
}
