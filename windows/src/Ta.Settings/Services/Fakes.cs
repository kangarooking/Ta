using System.IO;
using System.Text.Json;
using Dom = Ta.Settings.Core;

namespace Ta.Settings.Services;

/// <summary>
/// 全部内存假实现。用于设置 UI 的独立开发与单元测试 —— 在捕获 / OCR / AI /
/// 快捷键 / 桥这几路的真实实现落地前，设置界面不依赖它们。
/// </summary>
public static class Fakes
{
    /// <summary>密钥的内存假实现（不落盘，不加密）。</summary>
    public sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public void Save(string secret, string account) => _values[account] = secret;

        /// <inheritdoc />
        public string? Read(string account) => _values.TryGetValue(account, out var value) ? value : null;

        /// <inheritdoc />
        public bool Contains(string account) => _values.ContainsKey(account);

        /// <inheritdoc />
        public void Delete(string account) => _values.Remove(account);
    }

    /// <summary>DPAPI 密钥实现（等价 Keychain 的 ThisDeviceOnly 语义）。</summary>
    /// <remarks>
    /// 真实实现走 <c>ProtectedData.Protect(..., DataProtectionScope.CurrentUser)</c>，
    /// 文件落在 <c>%LOCALAPPDATA%\Ta\secrets</c>。这是设置页「仅保存在本机」承诺的落点。
    /// </remarks>
    public sealed class DpapiSecretStore : ISecretStore
    {
        private const string DirectoryName = "Ta";

        private const string FileName = "secrets.json";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly object _gate = new();
        private readonly string _path;

        /// <summary>用指定文件构造。</summary>
        public DpapiSecretStore(string? path = null)
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _path = path ?? Path.Combine(root, DirectoryName, FileName);
        }

        /// <inheritdoc />
        public void Save(string secret, string account)
        {
            lock (_gate)
            {
                var map = Load();
                map[account] = Protect(secret);
                Persist(map);
            }
        }

        /// <inheritdoc />
        public string? Read(string account)
        {
            lock (_gate)
            {
                var map = Load();
                return map.TryGetValue(account, out var protectedValue)
                    ? Unprotect(protectedValue)
                    : null;
            }
        }

        /// <inheritdoc />
        public bool Contains(string account)
        {
            lock (_gate)
            {
                return Load().ContainsKey(account);
            }
        }

        /// <inheritdoc />
        public void Delete(string account)
        {
            lock (_gate)
            {
                var map = Load();
                if (map.Remove(account))
                {
                    Persist(map);
                }
            }
        }

        private static string Protect(string plain)
        {
            var bytes = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(plain),
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string cipher)
        {
            var bytes = Convert.FromBase64String(cipher);
            return System.Text.Encoding.UTF8.GetString(
                System.Security.Cryptography.ProtectedData.Unprotect(
                    bytes,
                    null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }

        private Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }

                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), SerializerOptions)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private void Persist(Dictionary<string, string> map)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_path, JsonSerializer.Serialize(map, SerializerOptions));
            }
            catch (Exception)
            {
                // 磁盘不可写时保持进程内可用即可
            }
        }
    }

    /// <summary>
    /// 多模态识别的假实现：永不真的发网络请求，按「视觉模型名是否可识别」给出确定结果，
    /// 便于把 <c>saveAndTest</c> 的两种分支都跑通。
    /// </summary>
    public sealed class FakeRecognitionService : IRecognitionService
    {
        /// <summary>强制失败时返回的错误说明。</summary>
        public string? FailureMessage { get; init; }

        /// <summary>成功时返回的响应文本。</summary>
        public string ResponseText { get; init; } = "截图里是一段中文说明文字，视觉模型已正常返回。";

        /// <inheritdoc />
        public Task<ConnectionTestResult> TestConnectionAsync(
            Dom.AiProviderProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailureMessage is { } failure)
            {
                return Task.FromResult(new ConnectionTestResult(false, failure));
            }

            if (string.IsNullOrWhiteSpace(profile.VisionModel))
            {
                return Task.FromResult(
                    new ConnectionTestResult(false, "请填写一个支持图片输入的视觉模型。"));
            }

            return Task.FromResult(new ConnectionTestResult(true, ResponseText));
        }
    }

    /// <summary>截图翻译的假实现。</summary>
    public sealed class FakeTranslationService : ITranslationService
    {
        /// <summary>强制失败时返回的错误说明。</summary>
        public string? FailureMessage { get; init; }

        /// <summary>文字模型成功响应。</summary>
        public string TextResponse { get; init; } = "文字模型连通正常。";

        /// <summary>视觉模型成功响应。</summary>
        public string VisionResponse { get; init; } = "视觉模型连通正常。";

        /// <inheritdoc />
        public Task<ConnectionTestResult> TestTextModelAsync(
            Dom.AiProviderProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailureMessage is { } failure)
            {
                return Task.FromResult(new ConnectionTestResult(false, failure));
            }

            if (string.IsNullOrWhiteSpace(profile.TextModel))
            {
                return Task.FromResult(
                    new ConnectionTestResult(false, "请填写文字模型，截图翻译需要同时使用文字与视觉模型。"));
            }

            return Task.FromResult(new ConnectionTestResult(true, TextResponse));
        }

        /// <inheritdoc />
        public Task<ConnectionTestResult> TestVisionModelAsync(
            Dom.AiProviderProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailureMessage is { } failure)
            {
                return Task.FromResult(new ConnectionTestResult(false, failure));
            }

            if (string.IsNullOrWhiteSpace(profile.VisionModel))
            {
                return Task.FromResult(
                    new ConnectionTestResult(false, "请填写一个支持图片输入的视觉模型。"));
            }

            return Task.FromResult(new ConnectionTestResult(true, VisionResponse));
        }
    }

    /// <summary>本地 OCR 的假实现。</summary>
    public sealed class FakeOcrService : IOcrService
    {
        /// <inheritdoc />
        public Task<string> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("（本地 OCR 假实现返回的示例文本）");
        }
    }

    /// <summary>OCR 增强包安装的假实现（只在内存里记账，不真的下载）。</summary>
    public sealed class FakeOcrPackService : IOcrPackService
    {
        private readonly Dictionary<Dom.OcrEngine, string> _installed = new();

        /// <summary>预置已安装版本（测试用）。</summary>
        public void MarkInstalled(Dom.OcrEngine engine, string version) => _installed[engine] = version;

        /// <summary>可下载版本；默认 1.1.0。</summary>
        public string AvailableVersion { get; init; } = "1.1.0";

        /// <inheritdoc />
        public OcrPackInstalledInfo? InstalledInfo(Dom.OcrEngine engine)
            => _installed.TryGetValue(engine, out var version) ? new OcrPackInstalledInfo(version) : null;

        /// <inheritdoc />
        public Task<OcrPackAvailability?> AvailablePackageAsync(Dom.OcrEngine engine)
            => Task.FromResult<OcrPackAvailability?>(
                engine == Dom.OcrEngine.PaddleOcr
                    ? new OcrPackAvailability(AvailableVersion, "312 MB", false)
                    : null);

        /// <inheritdoc />
        public Task<OcrPackInstalledInfo> InstallRecommendedAsync(
            Dom.OcrEngine engine,
            IProgress<OcrPackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new OcrPackProgress("正在下载…", 0.4));
            progress?.Report(new OcrPackProgress("正在校验…", 0.9));
            var info = new OcrPackInstalledInfo(AvailableVersion);
            _installed[engine] = AvailableVersion;
            return Task.FromResult(info);
        }

        /// <inheritdoc />
        public void Remove(Dom.OcrEngine engine) => _installed.Remove(engine);

        /// <inheritdoc />
        public string? ChooseAndImport(Dom.OcrEngine engine) => null;

        /// <inheritdoc />
        public void Prewarm(Dom.OcrEngine engine)
        {
            // 假实现不做任何事
        }
    }

    /// <summary>
    /// 全局快捷键的假实现：持久化到注入的 <see cref="ISettingsStore"/>，
    /// 键名 <c>globalHotKey.&lt;rawValue&gt;</c> 与参考文档 §10.6 一致；
    /// 真正的 <c>RegisterHotKey</c> 注册留给快捷键那一路实现。
    /// </summary>
    public sealed class FakeHotKeyService : IHotKeyService
    {
        private readonly ISettingsStore _store;
        private readonly object _gate = new();

        /// <summary>构造。</summary>
        public FakeHotKeyService(ISettingsStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <inheritdoc />
        public event EventHandler? Changed;

        /// <summary>
        /// 注册失败并已回滚。属于 <see cref="IHotKeyService"/> 契约的一部分，
        /// 真正的实现由 <c>GlobalHotKeyManager</c> 在回滚后触发；假实现只暴露一个
        /// 手动触发的入口，方便测试快捷键页的错误提示。
        /// </summary>
#pragma warning disable CS0067 // 假实现里没人自动触发，仅供测试手动调用
        public event EventHandler<string>? RegistrationFailed;
#pragma warning restore CS0067

        /// <summary>手动触发一次「注册失败并已回滚」事件（测试用）。</summary>
        public void RaiseRegistrationFailed(string message) => RegistrationFailed?.Invoke(this, message);

        /// <inheritdoc />
        public Dom.HotKeyShortcut ShortcutFor(Dom.GlobalHotKeyAction action)
        {
            lock (_gate)
            {
                var raw = _store.GetString(Dom.SettingsKeys.GlobalHotKey(Dom.GlobalHotKeyActions.Raw(action)));
                return HotKeyShortcutJson.TryParse(raw) ?? Dom.GlobalHotKeyActions.DefaultShortcut(action);
            }
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<Dom.GlobalHotKeyAction, Dom.HotKeyShortcut> AllShortcuts()
        {
            var result = new Dictionary<Dom.GlobalHotKeyAction, Dom.HotKeyShortcut>();
            foreach (var action in Dom.GlobalHotKeyActions.All)
            {
                result[action] = ShortcutFor(action);
            }

            return result;
        }

        /// <inheritdoc />
        public void Save(Dom.HotKeyShortcut shortcut, Dom.GlobalHotKeyAction action)
        {
            lock (_gate)
            {
                foreach (var other in Dom.GlobalHotKeyActions.All)
                {
                    if (other == action)
                    {
                        continue;
                    }

                    if (ShortcutFor(other) == shortcut)
                    {
                        throw new InvalidOperationException(
                            $"这个组合已用于“{Dom.GlobalHotKeyActions.DisplayName(other)}”，请换一个快捷键。");
                    }
                }

                _store.SetString(
                    Dom.SettingsKeys.GlobalHotKey(Dom.GlobalHotKeyActions.Raw(action)),
                    HotKeyShortcutJson.Serialize(shortcut));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <inheritdoc />
        public void ResetAll()
        {
            lock (_gate)
            {
                foreach (var action in Dom.GlobalHotKeyActions.All)
                {
                    _store.Remove(Dom.SettingsKeys.GlobalHotKey(Dom.GlobalHotKeyActions.Raw(action)));
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>快捷键的 JSON 读写（字段名与 Mac 版 UserDefaults 里的 JSON 一致）。</summary>
    internal static class HotKeyShortcutJson
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Serialize(Dom.HotKeyShortcut shortcut)
            => JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["keyCode"] = shortcut.VirtualKey,
                    ["modifiers"] = (uint)shortcut.Modifiers,
                    ["keyLabel"] = shortcut.KeyLabel,
                },
                Options);

        public static Dom.HotKeyShortcut? TryParse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (!root.TryGetProperty("keyCode", out var keyCodeElement)
                    || !root.TryGetProperty("keyLabel", out var labelElement))
                {
                    return null;
                }

                var keyCode = keyCodeElement.ValueKind == JsonValueKind.Number
                    ? (ushort)keyCodeElement.GetUInt32()
                    : (ushort)0;
                var modifiers = Dom.HotKeyModifiers.None;
                if (root.TryGetProperty("modifiers", out var modifiersElement) && modifiersElement.ValueKind == JsonValueKind.Number)
                {
                    modifiers = (Dom.HotKeyModifiers)modifiersElement.GetUInt32();
                }

                return new Dom.HotKeyShortcut(keyCode, modifiers, labelElement.GetString() ?? string.Empty);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>屏幕录制权限假实现：默认已授权，可强制改成未授权。</summary>
    public sealed class FakeScreenCapturePermissionService : IScreenCapturePermissionService
    {
        /// <summary>构造。</summary>
        public FakeScreenCapturePermissionService(bool isGranted = true) => IsGranted = isGranted;

        /// <summary>请求次数（测试断言用）。</summary>
        public int RequestCount { get; private set; }

        /// <summary>打开系统设置次数。</summary>
        public int OpenSettingsCount { get; private set; }

        /// <inheritdoc />
        public bool IsGranted { get; set; }

        /// <inheritdoc />
        public bool Request()
        {
            RequestCount++;
            IsGranted = true;
            return true;
        }

        /// <inheritdoc />
        public void OpenSystemSettings() => OpenSettingsCount++;
    }

    /// <summary>辅助功能权限假实现。</summary>
    public sealed class FakeAccessibilityPermissionService : IAccessibilityPermissionService
    {
        /// <summary>构造。</summary>
        public FakeAccessibilityPermissionService(bool isGranted = false) => IsGranted = isGranted;

        /// <inheritdoc />
        public bool IsGranted { get; set; }

        /// <inheritdoc />
        public bool Request()
        {
            IsGranted = true;
            return true;
        }

        /// <inheritdoc />
        public void OpenSystemSettings()
        {
            // Windows 上没有等价入口，留给实现方决定
        }
    }

    /// <summary>Agent 桥假实现：内存审计 + 内存工件计数。</summary>
    public sealed class FakeAgentBridge : IAgentBridge
    {
        private readonly List<AgentAuditEntry> _entries = new();
        private int _artifactCount;

        /// <summary>审计上限，与 Mac 版 <c>defaultMaximumEntries = 100</c> 一致。</summary>
        public const int MaximumEntries = 100;

        /// <summary>构造，可选预置若干条审计。</summary>
        public FakeAgentBridge(IEnumerable<AgentAuditEntry>? seed = null)
        {
            if (seed is not null)
            {
                _entries.AddRange(seed);
            }
        }

        /// <summary>预置工件组数。</summary>
        public int ArtifactCount
        {
            get => _artifactCount;
            set => _artifactCount = value;
        }

        /// <summary>CLI 是否已安装。</summary>
        public bool CliInstalled { get; init; } = true;

        /// <summary>已安装的 Skill 目录。</summary>
        public IReadOnlyList<string> SkillLocations { get; init; } = new[] { @"%USERPROFILE%\.codex\skills\ta" };

        /// <inheritdoc />
        public Task<IReadOnlyList<AgentAuditEntry>> RecentAsync(int limit)
        {
            var result = _entries
                .OrderByDescending(e => e.OccurredAt)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<AgentAuditEntry>>(result);
        }

        /// <inheritdoc />
        public Task ClearAuditAsync()
        {
            _entries.Clear();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<int> ClearArtifactsAsync()
        {
            var count = _artifactCount;
            _artifactCount = 0;
            return Task.FromResult(count);
        }

        /// <inheritdoc />
        public AgentInstallationStatus DetectInstallation()
            => new(CliInstalled, SkillLocations);

        /// <summary>追加一条审计（供测试与调试用）。</summary>
        public void Append(AgentAuditEntry entry)
        {
            _entries.Add(entry);
            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaximumEntries);
            }
        }
    }

    /// <summary>剪贴板假实现（只在内存里记住最后一次写入）。</summary>
    public sealed class InMemoryClipboardService : IClipboardService
    {
        /// <summary>最后一次写入的文本。</summary>
        public string? LastText { get; private set; }

        /// <inheritdoc />
        public bool SetText(string text)
        {
            LastText = text;
            return true;
        }
    }

    /// <summary>系统剪贴板实现。</summary>
    public sealed class WindowsClipboardService : IClipboardService
    {
        /// <inheritdoc />
        public bool SetText(string text)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 拉起截图的假实现：记录被请求的模式，供欢迎页在真实捕获落地前可用。
    /// </summary>
    public sealed class FakeCaptureLauncher : ICaptureLauncher
    {
        /// <summary>最近一次请求的模式。</summary>
        public Dom.CaptureMode? LastMode { get; private set; }

        /// <summary>调用次数。</summary>
        public int StartCount { get; private set; }

        /// <summary>钉剪贴板调用次数。</summary>
        public int PinClipboardCount { get; private set; }

        /// <inheritdoc />
        public bool IsCapturing => false;

        /// <inheritdoc />
        public string StatusText => "准备好了，选择一个操作开始拓取";

        /// <inheritdoc />
        public void StartCapture(Dom.CaptureMode mode)
        {
            LastMode = mode;
            StartCount++;
        }

        /// <inheritdoc />
        public void PinClipboardContent() => PinClipboardCount++;
    }
}
