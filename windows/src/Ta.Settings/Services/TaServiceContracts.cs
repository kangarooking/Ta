namespace Ta.Settings.Services;

/// <summary>
/// 设置存储。设置 UI 只依赖这个接口，不关心底层是 UserDefaults、
/// <c>ApplicationData.LocalSettings</c> 还是 JSON 文件。
/// </summary>
public interface ISettingsStore
{
    /// <summary>读字符串；不存在返回 null。</summary>
    string? GetString(string key);

    /// <summary>写字符串。</summary>
    void SetString(string key, string value);

    /// <summary>读布尔；不存在返回 <paramref name="fallback"/>。</summary>
    bool GetBool(string key, bool fallback);

    /// <summary>写布尔。</summary>
    void SetBool(string key, bool value);

    /// <summary>读浮点；不存在返回 <paramref name="fallback"/>。</summary>
    double GetDouble(string key, double fallback);

    /// <summary>写浮点。</summary>
    void SetDouble(string key, double value);

    /// <summary>删除一个键。</summary>
    void Remove(string key);
}

/// <summary>
/// 密钥存储。对应 macOS <c>KeychainSecretStore</c>（KeychainSecretStore.swift）。
/// Windows 上应该用 DPAPI（<c>DataProtectionScope.CurrentUser</c>，语义对应 Keychain 的
/// <c>ThisDeviceOnly</c>）实现，这里只声明接口。
/// </summary>
public interface ISecretStore
{
    /// <summary>保存密钥。</summary>
    void Save(string secret, string account);

    /// <summary>读密钥；不存在返回 null。</summary>
    string? Read(string account);

    /// <summary>是否已存。</summary>
    bool Contains(string account);

    /// <summary>删除。</summary>
    void Delete(string account);
}

/// <summary>
/// 测试连接的结果。
/// </summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Message">成功时的响应前若干字符，或失败时的错误说明。</param>
public readonly record struct ConnectionTestResult(bool Ok, string Message);

/// <summary>
/// 多模态（视觉）识别服务。设置界面只用它做连通性测试。
/// </summary>
public interface IRecognitionService
{
    /// <summary>用这套配置的视觉模型跑一次测试，返回响应文本（调用方只取前 24 字符）。</summary>
    Task<ConnectionTestResult> TestConnectionAsync(
        Core.AiProviderProfile profile,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 截图翻译服务。设置界面只用它做连通性测试。
/// </summary>
public interface ITranslationService
{
    /// <summary>用这套配置的文字模型跑一次测试。</summary>
    Task<ConnectionTestResult> TestTextModelAsync(
        Core.AiProviderProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>用这套配置的视觉模型跑一次测试。</summary>
    Task<ConnectionTestResult> TestVisionModelAsync(
        Core.AiProviderProfile profile,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// AI 模型配置仓库。对应 macOS <c>AIProviderProfileStore</c>（AIProviderProfileStore.swift:20-…）。
/// </summary>
public interface IProviderStore
{
    /// <summary>读取状态（含 normalize 自愈回写）。</summary>
    Core.AiProviderProfileState LoadState();

    /// <summary>写入状态。</summary>
    void SaveState(Core.AiProviderProfileState state);

    /// <summary>新增或更新一套配置；<paramref name="apiKey"/> 非空时一并存密钥。</summary>
    Core.AiProviderProfileState SaveProfile(Core.AiProviderProfile profile, string? apiKey = null);

    /// <summary>删除一套配置。</summary>
    Core.AiProviderProfileState DeleteProfile(string profileId);

    /// <summary>设置 AI 识图默认配置。</summary>
    Core.AiProviderProfileState SetActiveProfile(string? profileId);

    /// <summary>设置截图翻译使用的配置。</summary>
    Core.AiProviderProfileState SetTranslationProfile(string? profileId);

    /// <summary>这套配置是否已存 API Key。</summary>
    bool HasApiKey(string profileId);

    /// <summary>读这套配置的 API Key（可能为 null）。</summary>
    string? ApiKey(string profileId);

    /// <summary>移除这套配置的 API Key。</summary>
    void RemoveApiKey(string profileId);

    /// <summary>翻译不可用的原因；可用时返回 null。</summary>
    string? TranslationEligibility(Core.AiProviderProfile profile);

    /// <summary>可用于翻译的配置（文字 + 视觉 + API Key 齐备）。</summary>
    IReadOnlyList<Core.AiProviderProfile> EligibleTranslationProfiles(Core.AiProviderProfileState state);
}

/// <summary>屏幕录制权限。对应 macOS <c>ScreenCapturePermissionService</c>。</summary>
public interface IScreenCapturePermissionService
{
    /// <summary>是否已授权。</summary>
    bool IsGranted { get; }

    /// <summary>请求权限（会弹系统对话框）。</summary>
    bool Request();

    /// <summary>打开系统设置对应页面。</summary>
    void OpenSystemSettings();
}

/// <summary>辅助功能（自动滚动）权限。对应 macOS <c>AccessibilityAutoScrollService</c>。</summary>
public interface IAccessibilityPermissionService
{
    /// <summary>是否已授权。</summary>
    bool IsGranted { get; }

    /// <summary>请求权限。</summary>
    bool Request();

    /// <summary>打开系统设置对应页面。</summary>
    void OpenSystemSettings();
}

/// <summary>本地 OCR 服务。</summary>
public interface IOcrService
{
    /// <summary>识别一张图片，返回文本。</summary>
    Task<string> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}

/// <summary>OCR 增强包的安装进度。</summary>
/// <param name="Stage">阶段说明。</param>
/// <param name="Fraction">进度 0..1；未知时为 null。</param>
public readonly record struct OcrPackProgress(string Stage, double? Fraction);

/// <summary>已安装的增强包信息。</summary>
/// <param name="Version">已安装版本号。</param>
public readonly record struct OcrPackInstalledInfo(string Version);

/// <summary>可下载的增强包。</summary>
/// <param name="Version">版本号。</param>
/// <param name="FormattedSize">体积描述。</param>
/// <param name="IsLocal">是否是本机发行包。</param>
public readonly record struct OcrPackAvailability(string Version, string FormattedSize, bool IsLocal);

/// <summary>
/// OCR 增强包安装管理。对应 macOS <c>OptionalOCRPackManager</c>。
/// </summary>
public interface IOcrPackService
{
    /// <summary>已安装信息；未安装为 null。</summary>
    OcrPackInstalledInfo? InstalledInfo(Core.OcrEngine engine);

    /// <summary>查询可安装版本（仅 PaddleOCR 有）。</summary>
    Task<OcrPackAvailability?> AvailablePackageAsync(Core.OcrEngine engine);

    /// <summary>下载并安装。</summary>
    Task<OcrPackInstalledInfo> InstallRecommendedAsync(
        Core.OcrEngine engine,
        IProgress<OcrPackProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>卸载。</summary>
    void Remove(Core.OcrEngine engine);

    /// <summary>让用户手动选择文件导入；返回版本号，取消时返回 null。</summary>
    string? ChooseAndImport(Core.OcrEngine engine);

    /// <summary>后台预热模型。</summary>
    void Prewarm(Core.OcrEngine engine);
}

/// <summary>
/// 全局快捷键服务。对应 macOS <c>HotKeyPreferences</c> + <c>GlobalHotKeyManager</c>。
/// </summary>
public interface IHotKeyService
{
    /// <summary>快捷键已变更。对应 Mac 版 <c>didChangeNotification</c>。</summary>
    event EventHandler? Changed;

    /// <summary>注册失败并已回滚。对应 <c>registrationFailedNotification</c>。</summary>
    event EventHandler<string>? RegistrationFailed;

    /// <summary>读一个动作的快捷键；无持久化值时回落默认键。</summary>
    Core.HotKeyShortcut ShortcutFor(Core.GlobalHotKeyAction action);

    /// <summary>读全部。</summary>
    IReadOnlyDictionary<Core.GlobalHotKeyAction, Core.HotKeyShortcut> AllShortcuts();

    /// <summary>保存（重复组合会抛异常）。</summary>
    void Save(Core.HotKeyShortcut shortcut, Core.GlobalHotKeyAction action);

    /// <summary>恢复全部默认。</summary>
    void ResetAll();
}

/// <summary>一条 Agent 调用审计记录。</summary>
/// <remarks>
/// 对应 macOS <c>TaAgentAuditEntry</c>（TaAgentAuditLog.swift:5-17）。
/// ⚠️ 产品承诺：**不保存请求参数、识别正文或图片数据**，字段集必须保持这样。
/// </remarks>
public readonly record struct AgentAuditEntry(
    string RequestId,
    DateTimeOffset OccurredAt,
    string ClientName,
    string Method,
    bool Succeeded,
    int DurationMs,
    bool CloudUploaded)
{
    /// <summary>行 ID（对应 Mac 版 <c>id</c>）。</summary>
    public string Id => $"{RequestId}-{OccurredAt.ToUnixTimeSeconds()}";
}

/// <summary>CLI / Skill 安装状态。对应 macOS <c>TaAgentToolInstallationStatus</c>（AgentSettingsView.swift:17-21）。</summary>
/// <param name="CliInstalled">ta CLI 是否已安装。</param>
/// <param name="InstalledSkillLocations">已安装 Skill 的目录列表。</param>
public readonly record struct AgentInstallationStatus(bool CliInstalled, IReadOnlyList<string> InstalledSkillLocations)
{
    /// <summary>Skill 是否已安装。</summary>
    public bool SkillInstalled => InstalledSkillLocations.Count > 0;
}

/// <summary>
/// 本机 Agent 桥。设置界面只读审计、清缓存、查安装状态。
/// 对应 macOS <c>TaAgentAuditLog</c> + <c>TaAgentArtifactStore</c>。
/// </summary>
public interface IAgentBridge
{
    /// <summary>读最近若干条审计。</summary>
    Task<IReadOnlyList<AgentAuditEntry>> RecentAsync(int limit);

    /// <summary>清除审计记录。</summary>
    Task ClearAuditAsync();

    /// <summary>清理临时截图缓存，返回清理的组数。</summary>
    Task<int> ClearArtifactsAsync();

    /// <summary>探测 CLI / Skill 安装状态。</summary>
    AgentInstallationStatus DetectInstallation();
}

/// <summary>剪贴板。</summary>
public interface IClipboardService
{
    /// <summary>写入文本；返回是否成功。</summary>
    bool SetText(string text);
}

/// <summary>
/// 拉起一次截图。对应 macOS <c>AppModel.startCapture(_:)</c>。
/// </summary>
public interface ICaptureLauncher
{
    /// <summary>是否正在截图。</summary>
    bool IsCapturing { get; }

    /// <summary>状态文字。</summary>
    string StatusText { get; }

    /// <summary>开始一次截图。</summary>
    void StartCapture(Core.CaptureMode mode);

    /// <summary>从剪贴板内容生成钉图。</summary>
    void PinClipboardContent();
}
