using System;using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ContainerBenchmark
{
    /// <summary>
    /// Player UI（阶段 2）：UGUI + TMP 四段式，运行时用代码构建（场景搭建由
    /// Editor/ContainerBenchmarkSetup 菜单创建 Canvas + 本组件）。
    /// ①配置页：容器/操作/规模档/类型/碰撞档/Job 开关，全部默认勾选、可反选，附「开始」按钮；
    /// ②运行监控：进度条 + 当前测试项 + 已完成/剩余用例数 + 预计剩余时间（=已完成平均耗时×剩余数）+ 实时日志滚动区；
    /// ③结果表格：动态 TMP 网格，点列头排序（升/降），按容器/操作/规模筛选，行对象池 + 分页，避免每帧重建；
    /// ④导出：按钮触发 HTML 导出，弹提示显示文件路径。
    /// -autoRun 模式：完全禁用本组件并跳过 UI 构建，不阻塞主流程。
    /// </summary>
    public sealed class ContainerBenchmarkUI : MonoBehaviour
    {
        private const int TablePageSize = 40;
        private const int MaxLogLines = 600;
        private const float LogFlushInterval = 0.15f;

        private static readonly Color ColorText = new Color(0.88f, 0.88f, 0.92f);
        private static readonly Color ColorTextDim = new Color(0.62f, 0.62f, 0.7f);
        private static readonly Color ColorBg = new Color(0.14f, 0.14f, 0.19f, 0.96f);
        private static readonly Color ColorPanelBg = new Color(0.1f, 0.1f, 0.14f, 0.98f);
        private static readonly Color ColorOnBg = new Color(0.13f, 0.3f, 0.2f);
        private static readonly Color ColorOnText = new Color(0.55f, 0.95f, 0.68f);
        private static readonly Color ColorOffBg = new Color(0.2f, 0.2f, 0.26f);
        private static readonly Color ColorFill = new Color(0.35f, 0.65f, 0.95f);
        private static readonly Color ColorFail = new Color(0.95f, 0.4f, 0.4f);
        private static readonly Color ColorSkip = new Color(0.5f, 0.5f, 0.55f);

        private ContainerBenchmark _benchmark;
        private TMP_FontAsset _font;

        // 面板
        private GameObject _configPanel;
        private GameObject _monitorPanel;
        private GameObject _resultsPanel;
        private GameObject _exportPanel;
        private Button _startButton;
        private Button _exportButton;
        private TextMeshProUGUI _configCountText;

        // 监控
        private Image _progressFill;
        private TextMeshProUGUI _currentCaseText;
        private TextMeshProUGUI _progressText;
        private TextMeshProUGUI _etaText;
        private TextMeshProUGUI _logText;
        private ScrollRect _logScroll;
        private readonly List<string> _logLines = new List<string>();
        private bool _logDirty;
        private float _logFlushTimer;

        // 配置页 toggle 组
        private readonly List<ToggleOption> _containerOptions = new List<ToggleOption>();
        private readonly List<ToggleOption> _operationOptions = new List<ToggleOption>();
        private readonly List<ToggleOption> _scaleOptions = new List<ToggleOption>();
        private readonly List<ToggleOption> _typeOptions = new List<ToggleOption>();
        private readonly List<ToggleOption> _collisionOptions = new List<ToggleOption>();
        private ToggleOption _jobOption;

        // 结果表格
        private readonly List<BenchmarkCaseResult> _tableRows = new List<BenchmarkCaseResult>();
        private readonly List<GameObject> _rowPool = new List<GameObject>();
        private readonly List<TextMeshProUGUI[]> _rowCells = new List<TextMeshProUGUI[]>();
        private readonly TextMeshProUGUI[] _headerTexts = new TextMeshProUGUI[HeaderLabels.Length];
        private int _sortColumn;
        private bool _sortAsc;
        private int _filterContainerIndex;
        private int _filterOperationIndex;
        private int _filterScaleIndex;
        private int _tablePage;
        private TextMeshProUGUI _tablePageText;
        private readonly List<string> _filterContainers = new List<string>();
        private readonly List<string> _filterOperations = new List<string>();
        private readonly List<int> _filterScales = new List<int>();
        private Button _filterContainerButton;
        private Button _filterOperationButton;
        private Button _filterScaleButton;

        // 导出提示弹窗
        private GameObject _toast;
        private TextMeshProUGUI _toastTitleText;
        private TextMeshProUGUI _toastPathText;
        private Button _toastCopyButton;

        private static readonly string[] HeaderLabels =
        {
            "容器", "操作", "规模", "碰撞", "中位(ms)", "均值(ms)", "p95(ms)", "总耗时(ms)", "ns/op", "GC", "校验",
        };

        // ==================== 生命周期 ====================

        private void Awake()
        {
            _benchmark = GetComponent<ContainerBenchmark>();
            if (_benchmark == null)
            {
                _benchmark = gameObject.AddComponent<ContainerBenchmark>();
            }
            _font = UiFontProvider.GetFont();
        }

        private void OnEnable()
        {
            _benchmark.LogEmitted += OnLog;
            _benchmark.ProgressChanged += OnProgress;
            _benchmark.RunStarted += OnRunStarted;
            _benchmark.SuiteCompleted += OnSuiteCompleted;
        }

        private void OnDisable()
        {
            _benchmark.LogEmitted -= OnLog;
            _benchmark.ProgressChanged -= OnProgress;
            _benchmark.RunStarted -= OnRunStarted;
            _benchmark.SuiteCompleted -= OnSuiteCompleted;
        }

        private void Start()
        {
            if (_benchmark.IsAutoRun)
            {
                // 无人值守模式完全跳过 Player UI；禁用会触发 OnDisable 并退订所有事件。
                enabled = false;
                return;
            }

            _benchmark.EnsureUniverse(); // 保证勾选选项可枚举
            EnsureEventSystem();
            Canvas canvas = EnsureCanvas();
            BuildUi(canvas.transform);
            RefreshConfigToggles();

            _monitorPanel.SetActive(false);
            _resultsPanel.SetActive(false);
        }

        private void Update()
        {
            if (_logDirty && _logFlushTimer >= LogFlushInterval)
            {
                FlushLog();
            }
            else
            {
                _logFlushTimer += Time.unscaledDeltaTime;
            }
        }

        // ==================== 字体（运行时兜底：TMP Essentials 未导入也能显示中文） ====================

        /// <summary>
        /// 字体选择（中文 UI 优先）：
        /// 1) TMP DynamicOS 系统字体（微软雅黑等，覆盖中文+拉丁）→ 2) TMP Settings 默认字体（LiberationSans SDF，无中文）→ 3) 内置 LegacyRuntime。
        /// 项目已导入 TMP Essentials（LiberationSans SDF 无中文字形），故中文优先走 OS 字体动态资产。
        /// </summary>
        public static class UiFontProvider
        {
            private static TMP_FontAsset _font;

            public static TMP_FontAsset GetFont()
            {
                if (_font != null)
                {
                    return _font;
                }

                // 1) 直接按系统字体 family/style 创建 TMP DynamicOS 资产。
                // 不先构造 UnityEngine.Font：那种运行时 Font 没有可供 FontEngine 读取的字体数据，
                // TMP_FontAsset.CreateFontAsset(Font) 会回退失败并导致中文显示成方框。
                try
                {
                    var installed = new HashSet<string>(Font.GetOSInstalledFontNames(), StringComparer.OrdinalIgnoreCase);
                    string[] candidates = { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "DengXian" };
                    foreach (string family in candidates)
                    {
                        if (!installed.Contains(family))
                            continue;

                        TMP_FontAsset candidate = TMP_FontAsset.CreateFontAsset(family, "Regular", 64);
                        if (candidate != null && candidate.HasCharacter('容', false, true))
                        {
                            candidate.name = "RuntimeOS-" + family;
                            _font = candidate;
                            return _font;
                        }

                        if (candidate != null)
                            UnityEngine.Object.Destroy(candidate);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ContainerBenchmarkUI] TMP DynamicOS 中文字体创建失败: {e.Message}");
                }

                Debug.LogWarning("[ContainerBenchmarkUI] 未找到可用的 TMP DynamicOS 中文字体，回退 SDF 字体");

                // 2) TMP Settings 默认字体（LiberationSans SDF，仅拉丁字符）
                try
                {
                    if (TMP_Settings.defaultFontAsset != null)
                    {
                        _font = TMP_Settings.defaultFontAsset;
                        return _font;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ContainerBenchmarkUI] TMP Settings 读取失败: {e.Message}");
                }

                // 3) 内置字体（仅拉丁字符）
                Font legacy = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (legacy != null)
                {
                    _font = TMP_FontAsset.CreateFontAsset(legacy);
                }

                if (_font == null)
                {
                    Debug.LogError("[ContainerBenchmarkUI] 无法创建任何 TMP 字体资产，UI 文字将不可见");
                }
                return _font;
            }
        }

        // ==================== 环境 ====================

        private static Canvas EnsureCanvas()
        {
            Canvas canvas = FindAnyObjectByType<Canvas>();
            if (canvas != null)
            {
                return canvas;
            }

            var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        // ==================== UI 构建 ====================

        private void BuildUi(Transform canvasRoot)
        {
            RectTransform root = CreateRect("ContainerBenchmarkUI", canvasRoot,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            BuildHeader(root);
            BuildConfigPanel(root);
            BuildMonitorPanel(root);
            BuildResultsPanel(root);
            BuildExportPanel(root);
            BuildToast(root);
        }

        private void BuildHeader(RectTransform root)
        {
            RectTransform bar = CreateRect("Header", root,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, 0), new Vector2(0, 44));
            Image bg = bar.gameObject.AddComponent<Image>();
            bg.color = ColorBg;

            CreateText("Title", bar, "Container 容器性能测试", 22, TextAnchor.MiddleLeft, Color.white,
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(420, 40));

            // 面板切换（交互模式；无人值守只显示监控）
            if (!_benchmark.IsAutoRun)
            {
                Button navConfig = CreateButton("NavConfig", bar, "配置", 14,
                    new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(450, 0), new Vector2(80, 28));
                navConfig.onClick.AddListener(ShowConfigPanel);
                Button navLog = CreateButton("NavLog", bar, "日志", 14,
                    new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(538, 0), new Vector2(80, 28));
                navLog.onClick.AddListener(ShowMonitorPanel);
                Button navResults = CreateButton("NavResults", bar, "结果", 14,
                    new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(626, 0), new Vector2(80, 28));
                navResults.onClick.AddListener(ShowResultsPanel);
            }

            // 右侧：导出按钮 + 模式标签
            Button export = CreateButton("ExportButton", bar, "导出 HTML 报告", 16,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-16, 0), new Vector2(190, 32));
            _exportButton = export;
            export.onClick.AddListener(OnExportClicked);
            SetButtonEnabled(export, false);

            CreateText("ModeLabel", bar, string.Empty, 14, TextAnchor.MiddleRight, ColorTextDim,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-230, 0), new Vector2(280, 32))
                .text = _benchmark.IsAutoRun ? "无人值守模式（-autoRun）" : "交互模式";
        }

        // ---------------- ① 配置页 ----------------

        private void BuildConfigPanel(RectTransform root)
        {
            RectTransform panel = CreateRect("ConfigPanel", root,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -44), new Vector2(0, 640));
            _configPanel = panel.gameObject;
            Image bg = panel.gameObject.AddComponent<Image>();
            bg.color = ColorPanelBg;

            // 标题行 + 开始按钮 + 全选/全不选
            RectTransform titleRow = CreateRect("TitleRow", panel,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -8), new Vector2(0, 36));
            CreateText("ConfigTitle", titleRow, "① 配置（默认全选，点击可反选）", 18, TextAnchor.MiddleLeft, Color.white,
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(560, 32));

            Button all = CreateButton("SelectAll", titleRow, "全选", 14,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-330, 0), new Vector2(90, 28));
            all.onClick.AddListener(() => SetAllToggles(true));

            Button none = CreateButton("SelectNone", titleRow, "全不选", 14,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-232, 0), new Vector2(90, 28));
            none.onClick.AddListener(() => SetAllToggles(false));

            _startButton = CreateButton("StartButton", titleRow, "开始", 18,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-126, 0), new Vector2(110, 32));
            _startButton.onClick.AddListener(OnStartClicked);

            _configCountText = CreateText("CountText", titleRow, string.Empty, 14, TextAnchor.MiddleRight, ColorTextDim,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-470, 0), new Vector2(130, 28));

            // 滚动区：6 组勾选（上边距 52，贴底）
            (ScrollRect scroll, RectTransform content) = CreateScrollView("ConfigScroll", panel,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, -26), new Vector2(0, -52));
            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 10;
            vlg.padding = new RectOffset(12, 12, 8, 8);
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            scroll.verticalNormalizedPosition = 1f;

            // 组：容器 / 操作 / 规模 / 类型 / 碰撞 / Job
            BuildToggleGroup(content, "容器", _benchmark.Universe, c => c.ContainerName, c => c.ContainerName,
                _containerOptions, OnAnyToggleChanged);
            BuildToggleGroup(content, "操作", _benchmark.Universe, c => c.OperationCode,
                c => c.OperationCode + " " + c.OperationName, _operationOptions, OnAnyToggleChanged);
            BuildToggleGroup(content, "规模", _benchmark.Universe, c => c.Scale, c => ScaleLabel(c.Scale),
                _scaleOptions, OnAnyToggleChanged);
            BuildToggleGroup(content, "类型", _benchmark.Universe, c => c.ValueType, c => TypeLabel(c.ValueType),
                _typeOptions, OnAnyToggleChanged);
            BuildToggleGroup(content, "碰撞档", _benchmark.Universe, c => c.Collision, c => CollisionLabel(c.Collision),
                _collisionOptions, OnAnyToggleChanged);
            _jobOption = CreateSingleToggle(content, "Job/Burst 维度", _benchmark.Selection.EnableJob, OnAnyToggleChanged);
        }

        // ---------------- ② 运行监控 ----------------

        private void BuildMonitorPanel(RectTransform root)
        {
            RectTransform panel = CreateRect("MonitorPanel", root,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -44), new Vector2(0, 640));
            _monitorPanel = panel.gameObject;
            Image bg = panel.gameObject.AddComponent<Image>();
            bg.color = ColorPanelBg;

            _currentCaseText = CreateText("CurrentCase", panel, "② 运行监控（未开始）", 18, TextAnchor.MiddleLeft, Color.white,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -12), new Vector2(0, 32));

            // 进度条
            RectTransform barBg = CreateRect("ProgressBg", panel,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -52), new Vector2(0, 18));
            Image barBgImg = barBg.gameObject.AddComponent<Image>();
            barBgImg.color = new Color(0.05f, 0.05f, 0.08f);
            RectTransform fill = CreateRect("ProgressFill", barBg,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0.5f), new Vector2(0, 0), new Vector2(0, 14));
            _progressFill = fill.gameObject.AddComponent<Image>();
            _progressFill.color = ColorFill;
            _progressFill.type = Image.Type.Filled;
            _progressFill.fillMethod = Image.FillMethod.Horizontal;
            _progressFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _progressFill.fillAmount = 0f;

            _progressText = CreateText("ProgressText", panel, string.Empty, 15, TextAnchor.MiddleLeft, ColorText,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -78), new Vector2(0, 26));
            _etaText = CreateText("EtaText", panel, string.Empty, 15, TextAnchor.MiddleLeft, ColorTextDim,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -104), new Vector2(0, 24));

            // 日志滚动区（占剩余空间：上边距 136，贴底）
            (ScrollRect scroll, RectTransform content) = CreateScrollView("LogScroll", panel,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, -68), new Vector2(0, -136));
            _logScroll = scroll;
            var logVlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            logVlg.childControlWidth = true;
            logVlg.childControlHeight = true;
            logVlg.childForceExpandWidth = true;
            logVlg.childForceExpandHeight = false;
            var logFitter = content.gameObject.AddComponent<ContentSizeFitter>();
            logFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            _logText = CreateText("LogText", content, string.Empty, 14, TextAnchor.UpperLeft, ColorText,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 0), new Vector2(0, 0));
            _logText.textWrappingMode = TextWrappingModes.Normal;
            _logText.raycastTarget = false;
        }

        // ---------------- ③ 结果表格 ----------------

        private void BuildResultsPanel(RectTransform root)
        {
            RectTransform panel = CreateRect("ResultsPanel", root,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -44), new Vector2(0, 640));
            _resultsPanel = panel.gameObject;
            Image bg = panel.gameObject.AddComponent<Image>();
            bg.color = ColorPanelBg;

            // 标题
            CreateText("ResultsTitle", panel, "③ 结果表格（点列头排序，可筛选）", 18, TextAnchor.MiddleLeft, Color.white,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(16, -10), new Vector2(0, 30));

            // 筛选行（循环按钮：全部 → 各选项）
            float filterY = -48;
            _filterContainerButton = CreateButton("FilterContainer", panel, "容器: 全部", 14,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(16, filterY), new Vector2(230, 28));
            _filterContainerButton.onClick.AddListener(CycleContainerFilter);
            _filterOperationButton = CreateButton("FilterOperation", panel, "操作: 全部", 14,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(256, filterY), new Vector2(230, 28));
            _filterOperationButton.onClick.AddListener(CycleOperationFilter);
            _filterScaleButton = CreateButton("FilterScale", panel, "规模: 全部", 14,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(496, filterY), new Vector2(160, 28));
            _filterScaleButton.onClick.AddListener(CycleScaleFilter);

            TextMeshProUGUI stringNote = CreateText("StringSemanticNote", panel,
                "字符串专项：Dictionary<string,int> 与 NativeHashMap<FixedString64Bytes,int> 的 key 存储和哈希算法不同，仅在同一语义范围内对照。",
                12, TextAnchor.MiddleLeft, ColorTextDim,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(680, filterY), new Vector2(-696, 28));
            stringNote.textWrappingMode = TextWrappingModes.NoWrap;
            stringNote.overflowMode = TextOverflowModes.Ellipsis;

            // 表头（11 列，可点击排序）
            float[] widths = { 170, 205, 75, 155, 90, 90, 90, 100, 100, 90, 70 };
            float x = 16;
            for (int i = 0; i < HeaderLabels.Length; i++)
            {
                int col = i;
                Button headerBtn = CreateButton("Header" + i, panel, HeaderLabels[i], 14,
                    new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, filterY - 34), new Vector2(widths[i], 26),
                    out TextMeshProUGUI headerText);
                headerBtn.onClick.AddListener(() => OnSortClicked(col));
                _headerTexts[i] = headerText;
                x += widths[i] + 4;
            }

            // 表格滚动区（底部锚定，高 480）
            (ScrollRect scroll, RectTransform content) = CreateScrollView("TableScroll", panel,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 46), new Vector2(0, 480));
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            // 固定行高 + 对象池：内容高度按行数计算，滚动时按页刷新文本、不新建对象
            content.sizeDelta = new Vector2(0, TablePageSize * 28 + 8);
            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 2;
            vlg.padding = new RectOffset(8, 8, 4, 4);
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            for (int r = 0; r < TablePageSize; r++)
            {
                RectTransform row = CreateRect("Row" + r, content,
                    new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, 0), new Vector2(0, 26));
                var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                hlg.spacing = 4;
                hlg.padding = new RectOffset(4, 4, 2, 2);
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.childForceExpandWidth = false;
                hlg.childForceExpandHeight = true;
                var cells = new TextMeshProUGUI[HeaderLabels.Length];
                for (int c = 0; c < HeaderLabels.Length; c++)
                {
                    RectTransform cellRt = CreateRect("Cell" + c, row,
                        new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
                    var le = cellRt.gameObject.AddComponent<LayoutElement>();
                    le.preferredWidth = widths[c];
                    cells[c] = CreateText("Text", cellRt, string.Empty, 13,
                        c <= 1 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight, ColorText,
                        new Vector2(0, 0), new Vector2(1, 1), Vector2.zero, Vector2.zero);
                    cells[c].textWrappingMode = TextWrappingModes.NoWrap;
                    cells[c].overflowMode = TextOverflowModes.Ellipsis;
                }
                _rowPool.Add(row.gameObject);
                _rowCells.Add(cells);
                row.gameObject.SetActive(false);
            }

            // 分页行
            RectTransform pagerRow = CreateRect("PagerRow", panel,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0), new Vector2(0, 8), new Vector2(0, 30));
            Button prev = CreateButton("PrevPage", pagerRow, "上一页", 14,
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(90, 26));
            prev.onClick.AddListener(() => { if (_tablePage > 0) { _tablePage--; RefreshTable(); } });
            Button next = CreateButton("NextPage", pagerRow, "下一页", 14,
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(112, 0), new Vector2(90, 26));
            next.onClick.AddListener(() => { _tablePage++; RefreshTable(); });
            _tablePageText = CreateText("PageInfo", pagerRow, string.Empty, 14, TextAnchor.MiddleLeft, ColorTextDim,
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(212, 0), new Vector2(600, 26));
        }

        // ---------------- ④ 导出 ----------------

        private void BuildExportPanel(RectTransform root)
        {
            RectTransform panel = CreateRect("ExportPanel", root,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0), new Vector2(0, 0), new Vector2(0, 40));
            _exportPanel = panel.gameObject;
            Image bg = panel.gameObject.AddComponent<Image>();
            bg.color = ColorBg;

            CreateText("ExportTitle", panel, "④ 导出：生成单文件 HTML（ECharts 内联，双击离线可看）", 15,
                TextAnchor.MiddleLeft, ColorText,
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(700, 32));
            CreateText("ExportPath", panel, "输出: " + HtmlReportExporter.DefaultOutputPath, 13,
                TextAnchor.MiddleLeft, ColorTextDim,
                new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(740, 0), new Vector2(1000, 32));
        }

        // ---------------- 弹窗 ----------------

        private void BuildToast(RectTransform root)
        {
            RectTransform mask = CreateRect("Toast", root,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            _toast = mask.gameObject;
            Image maskImg = mask.gameObject.AddComponent<Image>();
            maskImg.color = new Color(0, 0, 0, 0.55f);

            RectTransform box = CreateRect("ToastBox", mask,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(900, 190));
            Image boxImg = box.gameObject.AddComponent<Image>();
            boxImg.color = ColorPanelBg;

            _toastTitleText = CreateText("ToastTitle", box, "HTML 报告导出成功", 20, TextAnchor.MiddleLeft, Color.white,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(20, -16), new Vector2(0, 34));
            _toastPathText = CreateText("ToastPath", box, string.Empty, 15, TextAnchor.UpperLeft, ColorText,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(20, -58), new Vector2(0, 60));
            _toastPathText.textWrappingMode = TextWrappingModes.Normal;

            _toastCopyButton = CreateButton("ToastCopy", box, "复制路径", 15,
                new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-70, 16), new Vector2(120, 32));
            _toastCopyButton.onClick.AddListener(OnCopyPathClicked);
            Button close = CreateButton("ToastClose", box, "关闭", 15,
                new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(70, 16), new Vector2(120, 32));
            close.onClick.AddListener(() => _toast.SetActive(false));

            _toast.SetActive(false);
        }

        // ==================== 基础构建辅助 ====================

        private RectTransform CreateRect(string name, Transform parent,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        private TextMeshProUGUI CreateText(string name, Transform parent, string content, int size,
            TextAnchor align, Color color,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 sizeDelta)
        {
            RectTransform rt = CreateRect(name, parent, anchorMin, anchorMax,
                new Vector2(anchorMin.x == anchorMax.x ? anchorMin.x : 0.5f,
                    anchorMin.y == anchorMax.y ? anchorMin.y : 0.5f),
                pos, sizeDelta);
            var text = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null)
            {
                text.font = _font;
            }
            text.text = content;
            text.fontSize = size;
            text.color = color;
            text.alignment = ToTmpAlignment(align);
            text.raycastTarget = false;
            return text;
        }

        private static TextAlignmentOptions ToTmpAlignment(TextAnchor align)
        {
            switch (align)
            {
                case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
                case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft: return TextAlignmentOptions.Left;
                case TextAnchor.MiddleCenter: return TextAlignmentOptions.Center;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
                case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
                case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
                default: return TextAlignmentOptions.Center;
            }
        }

        private Button CreateButton(string name, Transform parent, string label, int fontSize,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
        {
            return CreateButton(name, parent, label, fontSize, anchorMin, anchorMax, pos, size, out _);
        }

        private Button CreateButton(string name, Transform parent, string label, int fontSize,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size,
            out TextMeshProUGUI labelText)
        {
            RectTransform rt = CreateRect(name, parent, anchorMin, anchorMax,
                new Vector2(anchorMin.x == anchorMax.x ? anchorMin.x : 0.5f,
                    anchorMin.y == anchorMax.y ? anchorMin.y : 0.5f),
                pos, size);
            Image img = rt.gameObject.AddComponent<Image>();
            img.color = ColorOffBg;
            Button btn = rt.gameObject.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.85f, 0.85f, 0.9f);
            colors.pressedColor = new Color(0.6f, 0.6f, 0.7f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.5f);
            btn.colors = colors;

            labelText = CreateText("Label", rt, label, fontSize, TextAnchor.MiddleCenter, ColorText,
                new Vector2(0, 0), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            return btn;
        }

        private static void SetButtonEnabled(Button button, bool enabled)
        {
            button.interactable = enabled;
            Image img = button.GetComponent<Image>();
            if (img != null)
            {
                img.color = enabled ? ColorOffBg : new Color(0.12f, 0.12f, 0.16f, 0.7f);
            }
        }

        private (ScrollRect scroll, RectTransform content) CreateScrollView(string name, Transform parent,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
        {
            RectTransform rt = CreateRect(name, parent, anchorMin, anchorMax,
                new Vector2(0.5f, 0.5f), pos, size);
            Image img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.08f, 0.08f, 0.11f, 0.95f);
            ScrollRect scroll = rt.gameObject.AddComponent<ScrollRect>();

            RectTransform viewport = CreateRect("Viewport", rt,
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            viewport.gameObject.AddComponent<RectMask2D>();

            RectTransform content = CreateRect("Content", viewport,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), Vector2.zero, Vector2.zero);

            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            return (scroll, content);
        }

        // ==================== 勾选组构建 ====================

        private sealed class ToggleOption
        {
            public string Key;
            public string Label;
            public bool IsOn;
            public Image Bg;
            public TextMeshProUGUI LabelText;
        }

        private void BuildToggleGroup<T>(RectTransform parent, string title, IReadOnlyList<IBenchmarkCase> universe,
            Func<IBenchmarkCase, T> keySelector, Func<IBenchmarkCase, string> labelSelector,
            List<ToggleOption> options, Action onChanged)
        {
            // 组容器：手动高度（标题 22 + 网格行高）；标题不参与 GridLayoutGroup 布局
            RectTransform group = CreateRect("Group-" + title, parent,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(0, 30));
            CreateText("GroupTitle", group, title, 15, TextAnchor.MiddleLeft, new Color(0.65f, 0.78f, 1f),
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 0), new Vector2(400, 22));

            RectTransform grid = CreateRect("Grid", group,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -24), new Vector2(0, 0));
            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(278, 26);
            layout.spacing = new Vector2(8, 4);
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = 5;
            layout.childAlignment = TextAnchor.UpperLeft;

            var keys = new List<string>();
            foreach (IBenchmarkCase c in universe)
            {
                string key = keySelector(c).ToString();
                if (!keys.Contains(key))
                {
                    keys.Add(key);
                    var opt = CreateToggleOption(key, labelSelector(c), grid, onChanged);
                    options.Add(opt);
                }
            }
            // 行高自适应：外层 VerticalLayoutGroup 按本组 sizeDelta 排布
            int rows = Mathf.CeilToInt(options.Count / 5f);
            group.sizeDelta = new Vector2(0, 24 + rows * 30);
            grid.sizeDelta = new Vector2(0, rows * 30);
        }

        private ToggleOption CreateSingleToggle(Transform parent, string label, bool isOn, Action onChanged)
        {
            RectTransform group = CreateRect("Group-Job", parent,
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(0, 54));
            CreateText("GroupTitle", group, label, 15, TextAnchor.MiddleLeft, new Color(0.65f, 0.78f, 1f),
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 0), new Vector2(400, 22));
            var opt = CreateToggleOption("job", "开（默认）/ 点击关闭", group, onChanged);
            opt.IsOn = isOn;
            RectTransform rt = (RectTransform)opt.LabelText.transform.parent;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(0, -24);
            rt.sizeDelta = new Vector2(278, 26);
            ApplyToggleStyle(opt);
            return opt;
        }

        private ToggleOption CreateToggleOption(string key, string label, Transform parent, Action onChanged)
        {
            RectTransform rt = CreateRect("Toggle-" + key, parent,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, new Vector2(278, 26));
            Image img = rt.gameObject.AddComponent<Image>();
            Button btn = rt.gameObject.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.85f, 0.85f, 0.9f);
            colors.pressedColor = new Color(0.6f, 0.6f, 0.7f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.5f);
            btn.colors = colors;

            TextMeshProUGUI labelText = CreateText("Label", rt, label, 14, TextAnchor.MiddleLeft, ColorText,
                new Vector2(0, 0), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            labelText.margin = new Vector4(8, 2, 4, 2);

            var opt = new ToggleOption { Key = key, Label = label, IsOn = true, Bg = img, LabelText = labelText };
            btn.onClick.AddListener(() =>
            {
                opt.IsOn = !opt.IsOn;
                ApplyToggleStyle(opt);
                onChanged();
            });
            return opt;
        }

        private static void ApplyToggleStyle(ToggleOption opt)
        {
            opt.Bg.color = opt.IsOn ? ColorOnBg : ColorOffBg;
            opt.LabelText.color = opt.IsOn ? ColorOnText : ColorTextDim;
            opt.LabelText.fontStyle = opt.IsOn ? FontStyles.Bold : FontStyles.Normal;
        }

        // ==================== 配置页逻辑 ====================

        private void RefreshConfigToggles()
        {
            ApplySelectionToOptions(_containerOptions, _benchmark.Selection.Containers);
            ApplySelectionToOptions(_operationOptions, _benchmark.Selection.Operations);
            ApplySelectionToOptions(_scaleOptions, _benchmark.Selection.Scales);
            ApplySelectionToOptions(_typeOptions, _benchmark.Selection.ValueTypes);
            ApplySelectionToOptions(_collisionOptions, _benchmark.Selection.Collisions);
            if (_jobOption != null)
            {
                _jobOption.IsOn = _benchmark.Selection.EnableJob;
                ApplyToggleStyle(_jobOption);
            }
            UpdateConfigCount();
        }

        private static void ApplySelectionToOptions(List<ToggleOption> options, HashSet<string> selected)
        {
            foreach (ToggleOption opt in options)
            {
                opt.IsOn = selected.Contains(opt.Key);
                ApplyToggleStyle(opt);
            }
        }

        private static void ApplySelectionToOptions(List<ToggleOption> options, HashSet<int> selected)
        {
            foreach (ToggleOption opt in options)
            {
                opt.IsOn = int.TryParse(opt.Key, out int v) && selected.Contains(v);
                ApplyToggleStyle(opt);
            }
        }

        private static void ApplySelectionToOptions(List<ToggleOption> options, HashSet<BenchmarkValueType> selected)
        {
            foreach (ToggleOption opt in options)
            {
                opt.IsOn = Enum.TryParse(opt.Key, out BenchmarkValueType v) && selected.Contains(v);
                ApplyToggleStyle(opt);
            }
        }

        private static void ApplySelectionToOptions(List<ToggleOption> options, HashSet<CollisionProfile> selected)
        {
            foreach (ToggleOption opt in options)
            {
                opt.IsOn = Enum.TryParse(opt.Key, out CollisionProfile v) && selected.Contains(v);
                ApplyToggleStyle(opt);
            }
        }

        private void OnAnyToggleChanged()
        {
            SyncSelectionFromOptions();
            UpdateConfigCount();
        }

        private void SyncSelectionFromOptions()
        {
            SelectionState s = _benchmark.Selection;
            s.Containers.Clear();
            foreach (ToggleOption opt in _containerOptions)
            {
                if (opt.IsOn) s.Containers.Add(opt.Key);
            }
            s.Operations.Clear();
            foreach (ToggleOption opt in _operationOptions)
            {
                if (opt.IsOn) s.Operations.Add(opt.Key);
            }
            s.Scales.Clear();
            foreach (ToggleOption opt in _scaleOptions)
            {
                if (opt.IsOn && int.TryParse(opt.Key, out int v)) s.Scales.Add(v);
            }
            s.ValueTypes.Clear();
            foreach (ToggleOption opt in _typeOptions)
            {
                if (opt.IsOn && Enum.TryParse(opt.Key, out BenchmarkValueType v)) s.ValueTypes.Add(v);
            }
            s.Collisions.Clear();
            foreach (ToggleOption opt in _collisionOptions)
            {
                if (opt.IsOn && Enum.TryParse(opt.Key, out CollisionProfile v)) s.Collisions.Add(v);
            }
            if (_jobOption != null)
            {
                s.EnableJob = _jobOption.IsOn;
            }
        }

        private void UpdateConfigCount()
        {
            SyncSelectionFromOptions();
            _configCountText.text = "已选用例: " + _benchmark.BuildSelectedCases().Count;
            _startButton.interactable = _benchmark.BuildSelectedCases().Count > 0;
        }

        private void SetAllToggles(bool on)
        {
            foreach (ToggleOption opt in _containerOptions) { opt.IsOn = on; ApplyToggleStyle(opt); }
            foreach (ToggleOption opt in _operationOptions) { opt.IsOn = on; ApplyToggleStyle(opt); }
            foreach (ToggleOption opt in _scaleOptions) { opt.IsOn = on; ApplyToggleStyle(opt); }
            foreach (ToggleOption opt in _typeOptions) { opt.IsOn = on; ApplyToggleStyle(opt); }
            foreach (ToggleOption opt in _collisionOptions) { opt.IsOn = on; ApplyToggleStyle(opt); }
            if (_jobOption != null) { _jobOption.IsOn = on; ApplyToggleStyle(_jobOption); }
            OnAnyToggleChanged();
        }

        private void OnStartClicked()
        {
            SyncSelectionFromOptions();
            _benchmark.StartRun();
        }

        // ==================== 主控事件回调 ====================

        private void OnRunStarted(int totalCases)
        {
            _configPanel.SetActive(false);
            _monitorPanel.SetActive(true);
            _resultsPanel.SetActive(false);
            _currentCaseText.text = $"② 运行监控（共 {totalCases} 个用例）";
            _progressFill.fillAmount = 0f;
            _progressText.text = $"已完成 0/{totalCases}";
            _etaText.text = "预计剩余: --";
            SetButtonEnabled(_startButton, false);
            SetButtonEnabled(_exportButton, false);
        }

        private void OnProgress(BenchmarkProgress p)
        {
            _progressFill.fillAmount = p.Progress01;
            _currentCaseText.text = $"当前: {p.currentCaseName}";
            _progressText.text =
                $"已完成 {p.completedCases}/{p.totalCases} | 失败 {p.failedCases} | 跳过 {p.skippedCases} | 已用时 {FormatDuration(p.elapsedSeconds)}";
            if (p.etaSeconds > 0d)
            {
                _etaText.text = $"预计剩余: {FormatDuration(p.etaSeconds)}" +
                                $"（平均 {p.elapsedSeconds / Math.Max(1, p.completedCases):F2}s/用例 × 剩余 {p.totalCases - p.completedCases}）";
            }
        }

        private void OnSuiteCompleted(BenchmarkSuiteResult suite)
        {
            bool environmentFailed = suite != null
                                     && (string.Equals(suite.runStatus, "EnvironmentFailed", StringComparison.Ordinal)
                                         || !string.IsNullOrWhiteSpace(suite.environmentError));
            if (environmentFailed)
            {
                _progressFill.fillAmount = 0f;
                _currentCaseText.text = "② 环境门失败";
                _progressText.text = $"已完成 {suite.completedCases}/{suite.totalCases} | 失败 0 | 跳过 0";
                _etaText.text = string.IsNullOrWhiteSpace(suite.environmentError)
                    ? "环境不满足正式测量约束"
                    : suite.environmentError;
                SetButtonEnabled(_exportButton, true);
                SetButtonEnabled(_startButton, true);
                _configPanel.SetActive(false);
                _resultsPanel.SetActive(false);
                _monitorPanel.SetActive(true);
                return;
            }

            _progressFill.fillAmount = 1f;
            _currentCaseText.text = string.Equals(suite?.runStatus, "Completed", StringComparison.Ordinal)
                ? "② 运行完成"
                : $"② 运行结束（{suite?.runStatus ?? "状态未知"}）";
            _progressText.text = suite == null
                ? "运行结束"
                : $"已完成 {suite.completedCases}/{suite.totalCases} | 失败 {suite.failedCases} | 跳过 {suite.skippedCases}";
            _etaText.text = suite != null && suite.failedCases == 0
                ? "已完成全部用例"
                : "已结束，请检查失败与跳过项";
            SetButtonEnabled(_exportButton, true);
            SetButtonEnabled(_startButton, true);
            _resultsPanel.SetActive(true);
            _monitorPanel.SetActive(false);
            RebuildTableData();
        }

        // ==================== 面板切换 ====================

        private void ShowConfigPanel()
        {
            if (_benchmark.IsRunning)
            {
                return; // 运行中保持监控页
            }
            _configPanel.SetActive(true);
            _monitorPanel.SetActive(false);
            _resultsPanel.SetActive(false);
        }

        private void ShowMonitorPanel()
        {
            _configPanel.SetActive(false);
            _monitorPanel.SetActive(true);
            _resultsPanel.SetActive(false);
        }

        private void ShowResultsPanel()
        {
            if (_benchmark.IsRunning
                || _benchmark.Suite == null
                || _benchmark.Suite.results == null
                || _benchmark.Suite.results.Count == 0)
            {
                return;
            }
            _configPanel.SetActive(false);
            _monitorPanel.SetActive(false);
            _resultsPanel.SetActive(true);
        }

        private void OnLog(string message)
        {
            _logLines.Add(message);
            if (_logLines.Count > MaxLogLines * 3)
            {
                _logLines.RemoveRange(0, _logLines.Count - MaxLogLines * 3);
            }
            _logDirty = true;
            _logFlushTimer = 0f;
        }

        private void FlushLog()
        {
            _logDirty = false;
            _logFlushTimer = 0f;
            if (_logText == null)
            {
                return;
            }
            var sb = new StringBuilder();
            int start = Math.Max(0, _logLines.Count - MaxLogLines);
            for (int i = start; i < _logLines.Count; i++)
            {
                sb.Append(_logLines[i]).Append('\n');
            }
            _logText.text = sb.ToString();
            Canvas.ForceUpdateCanvases();
            if (_logScroll != null)
            {
                _logScroll.verticalNormalizedPosition = 0f; // 滚到底部
            }
        }

        // ==================== 结果表格逻辑 ====================

        private void RebuildTableData()
        {
            _tableRows.Clear();
            if (_benchmark.Suite == null)
            {
                return;
            }
            foreach (BenchmarkCaseResult r in _benchmark.Suite.results)
            {
                if (r.isWarmup)
                {
                    continue; // 预热样本不入表（图表 5 中有对照）
                }
                _tableRows.Add(r);
            }

            // 筛选选项（按全集去重、保持自然顺序）
            _filterContainers.Clear();
            _filterContainers.Add("全部");
            foreach (IBenchmarkCase c in _benchmark.Universe)
            {
                if (!_filterContainers.Contains(c.ContainerName))
                {
                    _filterContainers.Add(c.ContainerName);
                }
            }
            _filterOperations.Clear();
            _filterOperations.Add("全部");
            foreach (IBenchmarkCase c in _benchmark.Universe)
            {
                if (!_filterOperations.Contains(c.OperationCode))
                {
                    _filterOperations.Add(c.OperationCode);
                }
            }
            _filterScales.Clear();
            _filterScales.Add(0);
            foreach (IBenchmarkCase c in _benchmark.Universe)
            {
                if (!_filterScales.Contains(c.Scale))
                {
                    _filterScales.Add(c.Scale);
                }
            }

            _filterContainerIndex = 0;
            _filterOperationIndex = 0;
            _filterScaleIndex = 0;
            _sortColumn = 8; // ns/op
            _sortAsc = true;
            _tablePage = 0;
            RefreshFilterLabels();
            RefreshTable();
        }

        private void RefreshFilterLabels()
        {
            var containerLabel = _filterContainerButton.GetComponentInChildren<TextMeshProUGUI>();
            containerLabel.text = "容器: " + _filterContainers[_filterContainerIndex];
            var opLabel = _filterOperationButton.GetComponentInChildren<TextMeshProUGUI>();
            opLabel.text = "操作: " + _filterOperations[_filterOperationIndex];
            var scaleLabel = _filterScaleButton.GetComponentInChildren<TextMeshProUGUI>();
            scaleLabel.text = "规模: " + (_filterScales[_filterScaleIndex] == 0
                ? "全部"
                : ScaleLabel(_filterScales[_filterScaleIndex]));
        }

        private void CycleContainerFilter()
        {
            _filterContainerIndex = (_filterContainerIndex + 1) % _filterContainers.Count;
            _tablePage = 0;
            RefreshFilterLabels();
            RefreshTable();
        }

        private void CycleOperationFilter()
        {
            _filterOperationIndex = (_filterOperationIndex + 1) % _filterOperations.Count;
            _tablePage = 0;
            RefreshFilterLabels();
            RefreshTable();
        }

        private void CycleScaleFilter()
        {
            _filterScaleIndex = (_filterScaleIndex + 1) % _filterScales.Count;
            _tablePage = 0;
            RefreshFilterLabels();
            RefreshTable();
        }

        private void OnSortClicked(int column)
        {
            if (_sortColumn == column)
            {
                _sortAsc = !_sortAsc;
            }
            else
            {
                _sortColumn = column;
                _sortAsc = column <= 1 || column == 10; // 字符串列默认升序
            }
            RefreshTable();
        }

        private void RefreshTable()
        {
            // 筛选
            var filtered = new List<BenchmarkCaseResult>(_tableRows.Count);
            string containerFilter = _filterContainers[_filterContainerIndex];
            string operationFilter = _filterOperations[_filterOperationIndex];
            int scaleFilter = _filterScales[_filterScaleIndex];
            foreach (BenchmarkCaseResult r in _tableRows)
            {
                if (containerFilter != "全部" && r.container != containerFilter)
                {
                    continue;
                }
                if (operationFilter != "全部" && r.operationCode != operationFilter)
                {
                    continue;
                }
                if (scaleFilter != 0 && r.scale != scaleFilter)
                {
                    continue;
                }
                filtered.Add(r);
            }

            // 排序
            filtered.Sort((a, b) =>
            {
                if (_sortColumn == 9 && IsGcAvailable(a) != IsGcAvailable(b))
                {
                    // N/A 始终排在可用 GC 指标之后，升降序只影响可用值之间的次序。
                    return IsGcAvailable(a) ? -1 : 1;
                }
                int cmp = CompareRows(a, b, _sortColumn);
                return _sortAsc ? cmp : -cmp;
            });

            // 表头箭头
            for (int i = 0; i < _headerTexts.Length; i++)
            {
                _headerTexts[i].text = HeaderLabels[i] +
                    (_sortColumn == i ? (_sortAsc ? " ▲" : " ▼") : string.Empty);
            }

            // 分页切片 + 行池填充
            int pageCount = Math.Max(1, (filtered.Count + TablePageSize - 1) / TablePageSize);
            if (_tablePage >= pageCount)
            {
                _tablePage = pageCount - 1;
            }
            int start = _tablePage * TablePageSize;
            for (int i = 0; i < TablePageSize; i++)
            {
                int idx = start + i;
                if (idx >= filtered.Count)
                {
                    _rowPool[i].SetActive(false);
                    continue;
                }
                BenchmarkCaseResult r = filtered[idx];
                _rowPool[i].SetActive(true);
                var cells = _rowCells[i];
                cells[0].text = r.container;
                cells[1].text = r.operationCode + " " + r.operation;
                cells[2].text = ScaleLabel(r.scale);
                cells[3].text = CollisionLabel(r.collision);
                bool hasMetrics = !r.skipped && r.sampleMs != null && r.sampleMs.Length > 0;
                cells[4].text = hasMetrics ? FormatMilliseconds(r.medianMs) : "-";
                cells[5].text = hasMetrics ? FormatMilliseconds(r.meanMs) : "-";
                cells[6].text = hasMetrics ? FormatMilliseconds(r.p95Ms) : "-";
                cells[7].text = hasMetrics ? FormatMilliseconds(r.totalMs) : "-";
                cells[8].text = hasMetrics ? FormatNsPerOp(r.nsPerOp) : "-";
                cells[9].text = hasMetrics ? (IsGcAvailable(r) ? FormatBytes(r.gcBytes) : "N/A") : "-";
                cells[10].text = r.skipped ? "跳过" : (r.validated ? "通过" : "失败");

                Color rowColor = r.skipped ? ColorSkip : (!r.validated ? ColorFail : ColorText);
                foreach (TextMeshProUGUI cell in cells)
                {
                    cell.color = rowColor;
                }
            }

            _tablePageText.text =
                $"第 {_tablePage + 1}/{pageCount} 页，共 {filtered.Count} 条（不含预热样本）";
        }

        private static int CompareRows(BenchmarkCaseResult a, BenchmarkCaseResult b, int column)
        {
            switch (column)
            {
                case 0: return string.CompareOrdinal(a.container, b.container);
                case 1: return string.CompareOrdinal(a.operationCode, b.operationCode);
                case 2: return a.scale.CompareTo(b.scale);
                case 3: return string.CompareOrdinal(a.collision, b.collision);
                case 4: return a.medianMs.CompareTo(b.medianMs);
                case 5: return a.meanMs.CompareTo(b.meanMs);
                case 6: return a.p95Ms.CompareTo(b.p95Ms);
                case 7: return a.totalMs.CompareTo(b.totalMs);
                case 8: return a.nsPerOp.CompareTo(b.nsPerOp);
                case 9: return a.gcBytes.CompareTo(b.gcBytes);
                case 10: return a.validated.CompareTo(b.validated);
                default: return 0;
            }
        }

        // ==================== 导出逻辑 ====================

        private void OnExportClicked()
        {
            if (_benchmark.IsRunning)
            {
                _toastTitleText.text = "HTML 报告尚不可导出";
                _toastPathText.text = "运行中禁止导出；请等待终态或查看原子 checkpoint。";
                _toastPathText.color = ColorFail;
                SetButtonEnabled(_toastCopyButton, false);
                _toast.SetActive(true);
                return;
            }
            string path = _benchmark.ExportHtml();
            if (path == null)
            {
                _toastTitleText.text = "HTML 报告导出失败";
                _toastPathText.text = "导出失败，请查看日志。";
                _toastPathText.color = ColorFail;
                SetButtonEnabled(_toastCopyButton, false);
            }
            else
            {
                _toastTitleText.text = "HTML 报告导出成功";
                _toastPathText.text = path;
                _toastPathText.color = ColorText;
                SetButtonEnabled(_toastCopyButton, true);
            }
            _toast.SetActive(true);
        }

        private void OnCopyPathClicked()
        {
            GUIUtility.systemCopyBuffer = _toastPathText.text;
        }

        // ==================== 格式化辅助 ====================

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0d)
            {
                return "--:--:--";
            }
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1d
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }

        private static string ScaleLabel(int scale)
        {
            if (scale >= 1_000_000)
            {
                return scale / 1_000_000 + "M";
            }
            if (scale >= 1_000)
            {
                return scale / 1_000 + "K";
            }
            return scale.ToString();
        }

        private static string TypeLabel(BenchmarkValueType type)
        {
            return type == BenchmarkValueType.IntVector3 ? "int+Vector3" : "字符串Key";
        }

        private static string CollisionLabel(CollisionProfile profile)
        {
            switch (profile)
            {
                case CollisionProfile.Normal: return "正常散列";
                case CollisionProfile.LimitedDomain: return "有限域(N/16哈希值,约16 key/hash)";
                case CollisionProfile.AllCollision: return "全碰撞";
                default: return profile.ToString();
            }
        }

        private static string CollisionLabel(string profile)
        {
            if (Enum.TryParse(profile, out CollisionProfile p))
            {
                return CollisionLabel(p);
            }
            return profile;
        }

        private static string FormatNsPerOp(double nsPerOp)
        {
            if (nsPerOp >= 1e6)
            {
                return (nsPerOp / 1e6).ToString("F2") + " ms";
            }
            if (nsPerOp >= 1e3)
            {
                return (nsPerOp / 1e3).ToString("F1") + " us";
            }
            return nsPerOp.ToString("F1") + " ns";
        }

        private static bool IsGcAvailable(BenchmarkCaseResult result)
        {
            return result.gcBytesAvailable && result.gcBytes >= 0L;
        }

        private static string FormatMilliseconds(double milliseconds)
        {
            if (milliseconds >= 100d)
            {
                return milliseconds.ToString("F1");
            }
            if (milliseconds >= 1d)
            {
                return milliseconds.ToString("F2");
            }
            return milliseconds.ToString("F3");
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0L)
            {
                return "N/A";
            }
            if (bytes >= 1_000_000)
            {
                return (bytes / 1e6).ToString("F2") + " MB";
            }
            if (bytes >= 1_000)
            {
                return (bytes / 1e3).ToString("F1") + " KB";
            }
            return bytes + " B";
        }
    }
}
