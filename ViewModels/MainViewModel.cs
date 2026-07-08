using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MajesticParser.Models;
using MajesticParser.Services;

namespace MajesticParser.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly ConfigService _config = new();
    private readonly AppConfig _cfg;
    private readonly Dictionary<string, ForumCacheEntry> _threadCache;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private ParserEngine? _engine;
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _logBuffer = new();
    private readonly Dictionary<string, NodeCacheEntry> _nodeCache;
    private readonly HashSet<string> _hidden;

    public MainViewModel()
    {
        _cfg = _config.LoadConfig();
        _threadCache = _config.LoadThreadCache();
        _nodeCache = _config.LoadNodeCache();
        _hidden = new HashSet<string>(
            _cfg.HiddenUrls.Select(UrlHelper.NormalizeForCompare));
        OutputBaseDir = _cfg.BaseOutputDir;

        ImageModes = new ObservableCollection<string>
        {
            "1. Только текст",
            "2. Текст + все изображения",
            "3. Только изображения (все сообщения)",
            "4. Только изображения по post ID",
            "5. Только изображения по авторам",
            "6. Текст + изображения по post ID",
            "7. Текст + изображения по авторам"
        };
        SelectedImageModeIndex = 1;

        BuildRootNodes();
        RefreshResumableRuns();

        // Восстанавливаем ранее загруженные серверы из кэша
        foreach (var s in _config.LoadServers())
            Servers.Add(s);

        // Восстанавливаем последний открытый раздел (мгновенно из кэша)
        RestoreLastSection();

        OpenAddSourceCommand = new RelayCommand(OpenAddSource);
        OpenRemoveSourceCommand = new RelayCommand(OpenRemoveSource);
        ConfirmAddSourceCommand = new RelayCommand(ConfirmAddSource);
        RemoveSelectedSourceCommand = new RelayCommand(RemoveSelectedSource, () => SelectedCustomSource != null);
        CloseOverlayCommand = new RelayCommand(CloseOverlays);

        BrowseFolderCommand = new RelayCommand(BrowseFolder, () => !IsBusy);
        RefreshTreeCommand = new RelayCommand(RefreshTree, () => !IsBusy);
        UncheckAllCommand = new RelayCommand(UncheckAll);
        CollapseAllCommand = new RelayCommand(CollapseAll);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        DeleteNodeCommand = new RelayCommand<TreeNodeViewModel>(DeleteNode);
        LoadServersCommand = new AsyncRelayCommand(LoadServersAsync, () => !IsBusy);
        RefreshNodeCommand = new RelayCommand<TreeNodeViewModel>(n => _ = RefreshNodeFromNetworkAsync(n));
        UpdateNowCommand = new AsyncRelayCommand(UpdateNowAsync);
        DismissUpdateCommand = new RelayCommand(DismissUpdate);

        // Проверка обновлений при запуске (авто-установка если есть)
        _ = CheckForUpdateAsync();
    }

    // ===================== АВТООБНОВЛЕНИЕ =====================

    private readonly UpdateService _updater = new();
    private string? _updateUrl;
    private CancellationTokenSource? _updateCts;
    public AsyncRelayCommand UpdateNowCommand { get; private set; } = null!;
    public RelayCommand DismissUpdateCommand { get; private set; } = null!;

    // Проверка при запуске → автоматическая установка обновления
    private async Task CheckForUpdateAsync()
    {
        var result = await _updater.CheckAsync();
        if (result == null)
            return;

        _updateUrl = result.Value.url;
        UpdateText = $"Обновление до версии {result.Value.version} " +
                     $"(у вас {UpdateService.CurrentVersion.ToString(3)}). Приложение перезапустится.";
        ShowUpdate = true;
        Log($"⬆ Найдено обновление v{result.Value.version} — устанавливаю автоматически…");

        // Автообновление при заходе
        await UpdateNowAsync();
    }

    private async Task UpdateNowAsync()
    {
        if (string.IsNullOrEmpty(_updateUrl))
            return;

        _updateCts = new CancellationTokenSource();
        var ct = _updateCts.Token;

        UpdateStatus = "Скачиваю обновление…";
        var path = await _updater.DownloadAsync(_updateUrl,
            p => UpdateStatus = $"Скачиваю обновление… {p}%", ct);

        if (ct.IsCancellationRequested)
        {
            UpdateStatus = "";
            ShowUpdate = false;
            return;
        }

        if (path == null)
        {
            UpdateStatus = "⚠ Не удалось скачать обновление. Можно продолжить работу.";
            return;
        }

        UpdateStatus = "Устанавливаю и перезапускаю…";
        _updater.RunInstallerAndExit(path, silent: true); // тихо ставит и перезапускает
    }

    // «Позже» — отменить автообновление и продолжить работу
    private void DismissUpdate()
    {
        _updateCts?.Cancel();
        ShowUpdate = false;
        UpdateStatus = "";
        Log("⏭ Обновление отложено.");
    }

    // ===================== ДЕРЕВО =====================

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();

    private void BuildRootNodes()
    {
        RootNodes.Clear();
        foreach (var s in _config.GetAllSources(_cfg))
        {
            if (s.Type == "thread")
            {
                RootNodes.Add(new TreeNodeViewModel(NodeKind.Thread, s.Name, s.Url,
                    UrlHelper.ExtractIdFromUrl(s.Url)));
            }
            else
            {
                RootNodes.Add(new TreeNodeViewModel(NodeKind.Forum, s.Name, s.Url,
                    UrlHelper.ExtractIdFromUrl(s.Url), false, LoadForumChildrenAsync));
            }
        }
    }

    // Ленивая подгрузка (как loader дерева) — сначала из кэша, потом из сети
    private Task LoadForumChildrenAsync(TreeNodeViewModel node)
        => LoadForumChildrenAsync(node, forceRefresh: false);

    // Принудительное обновление узла из сети (контекстное меню)
    private async Task RefreshNodeFromNetworkAsync(TreeNodeViewModel node)
    {
        if (node == null || node.Kind != NodeKind.Forum)
            return;
        Log($"\n🔄 Обновляю из сети: {node.Title}");
        await LoadForumChildrenAsync(node, forceRefresh: true);
    }

    private async Task LoadForumChildrenAsync(TreeNodeViewModel node, bool forceRefresh)
    {
        var key = UrlHelper.NormalizeForCompare(node.Url);

        // 1) Мгновенно из кэша
        if (!forceRefresh && _nodeCache.TryGetValue(key, out var cachedEntry))
        {
            BuildChildrenFromCache(node, cachedEntry);
            Log($"📦 Из кэша: {node.Title} (подфорумов {cachedEntry.Subforums.Count}, тем {cachedEntry.Threads.Count})");
            return;
        }

        // 2) Из сети (браузер)
        var headless = Headless;
        var source = node.ToSource();
        var token = EnsureToken();

        List<ForumNode> subs = new();
        List<ThreadInfo> threads = new();
        string? error = null;

        await _browserLock.WaitAsync();
        IsBusy = true;
        Status = $"Загрузка раздела: {node.Title}";
        try
        {
            (subs, threads) = await Task.Run(() =>
            {
                _engine ??= new ParserEngine(Log);
                var res = _engine.LoadForumNode(source, _threadCache, headless, token);
                _config.SaveThreadCache(_threadCache);
                return res;
            }, token);
        }
        catch (OperationCanceledException) { error = "загрузка отменена"; }
        catch (Exception e) { error = e.Message; }
        finally
        {
            IsBusy = false;
            Status = "Готов";
            _browserLock.Release();
        }

        if (error != null)
        {
            node.Children.Clear();
            node.Children.Add(TreeNodeViewModel.Placeholder("⚠ " + error));
            return;
        }

        // сохраняем снимок узла в кэш
        var entry = new NodeCacheEntry
        {
            Subforums = subs,
            Threads = threads,
            LastSync = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        _nodeCache[key] = entry;
        _config.SaveNodeCache(_nodeCache);

        BuildChildrenFromCache(node, entry);
    }

    // Построить детей узла из снимка кэша (скрытые пользователем — пропускаем)
    private void BuildChildrenFromCache(TreeNodeViewModel node, NodeCacheEntry entry)
    {
        var built = new List<TreeNodeViewModel>();
        foreach (var sf in entry.Subforums)
        {
            if (IsHidden(sf.Url)) continue;
            built.Add(new TreeNodeViewModel(NodeKind.Forum, sf.Name, sf.Url, sf.Id,
                false, LoadForumChildrenAsync));
        }
        foreach (var th in entry.Threads)
        {
            if (IsHidden(th.Url)) continue;
            built.Add(new TreeNodeViewModel(NodeKind.Thread, th.Title, th.Url, th.Id, th.Missing));
        }

        // AddChild проставит Parent и унаследует галочку родителя (каскад вниз)
        node.SetChildrenLoaded(built);
    }

    private bool IsHidden(string url)
        => _hidden.Contains(UrlHelper.NormalizeForCompare(url));

    private static void WalkTree(IEnumerable<TreeNodeViewModel> nodes, Action<TreeNodeViewModel> visit)
    {
        foreach (var n in nodes)
        {
            visit(n);
            if (n.Children.Count > 0)
                WalkTree(n.Children, visit);
        }
    }

    // Есть ли ПОЛНОСТЬЮ отмеченный (не частично) родитель-раздел выше по дереву
    private static bool HasFullyCheckedForumAncestor(TreeNodeViewModel node)
    {
        var p = node.Parent;
        while (p != null)
        {
            if (p.IsForum && p.IsFullyChecked)
                return true;
            p = p.Parent;
        }
        return false;
    }

    private void UncheckAll()
        => WalkTree(RootNodes, n => { if (n.HasCheckbox) n.IsChecked = false; });

    private void CollapseAll()
        => WalkTree(RootNodes, n => { if (n.IsForum) n.IsExpanded = false; });

    // ===================== КОЛЛЕКЦИИ / ВВОД =====================

    public ObservableCollection<string> ImageModes { get; }
    public ObservableCollection<string> ResumableRuns { get; } = new();
    public ObservableCollection<Source> CustomSources { get; } = new();

    private Source? _selectedCustomSource;
    public Source? SelectedCustomSource
    {
        get => _selectedCustomSource;
        set { SetField(ref _selectedCustomSource, value); RemoveSelectedSourceCommand.RaiseCanExecuteChanged(); }
    }

    private string? _selectedResumeRun;
    public string? SelectedResumeRun
    {
        get => _selectedResumeRun;
        set => SetField(ref _selectedResumeRun, value);
    }

    private int _selectedImageModeIndex;
    public int SelectedImageModeIndex
    {
        get => _selectedImageModeIndex;
        set
        {
            if (SetField(ref _selectedImageModeIndex, value))
            {
                OnPropertyChanged(nameof(PostIdsEnabled));
                OnPropertyChanged(nameof(AuthorsEnabled));
            }
        }
    }

    public bool PostIdsEnabled => SelectedImageModeIndex is 3 or 5;
    public bool AuthorsEnabled => SelectedImageModeIndex is 4 or 6;

    private string _newSourceUrl = "";
    public string NewSourceUrl
    {
        get => _newSourceUrl;
        set { if (SetField(ref _newSourceUrl, value)) OnPropertyChanged(nameof(NewSourceTypeLabel)); }
    }

    private string _newSourceName = "";
    public string NewSourceName { get => _newSourceName; set => SetField(ref _newSourceName, value); }

    public string NewSourceTypeLabel => string.IsNullOrWhiteSpace(NewSourceUrl)
        ? "введите URL раздела выше…"
        : ConfigService.DetectSourceType(NewSourceUrl) == "thread"
            ? "⚠ Это ссылка на тему. Добавлять можно только разделы (папки-форумы)."
            : "📁 Форум — раздел с темами и подфорумами";

    private string _addSourceError = "";
    public string AddSourceError { get => _addSourceError; set => SetField(ref _addSourceError, value); }

    private string _postIdsText = "";
    public string PostIdsText { get => _postIdsText; set => SetField(ref _postIdsText, value); }

    private string _authorsText = "";
    public string AuthorsText { get => _authorsText; set => SetField(ref _authorsText, value); }

    private bool _headless = true;
    public bool Headless { get => _headless; set => SetField(ref _headless, value); }

    private string _outputBaseDir = "";
    public string OutputBaseDir { get => _outputBaseDir; set => SetField(ref _outputBaseDir, value); }

    private bool _resumeEnabled;
    public bool ResumeEnabled { get => _resumeEnabled; set => SetField(ref _resumeEnabled, value); }

    private string _logText = "";
    public string LogText { get => _logText; set => SetField(ref _logText, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { SetField(ref _isBusy, value); RaiseCommands(); } }

    private string _status = "Готов";
    public string Status { get => _status; set => SetField(ref _status, value); }

    // overlays
    private bool _showAddSource;
    public bool ShowAddSource { get => _showAddSource; set { SetField(ref _showAddSource, value); OnPropertyChanged(nameof(AnyOverlay)); } }

    private bool _showRemoveSource;
    public bool ShowRemoveSource { get => _showRemoveSource; set { SetField(ref _showRemoveSource, value); OnPropertyChanged(nameof(AnyOverlay)); } }

    private bool _showUpdate;
    public bool ShowUpdate { get => _showUpdate; set { SetField(ref _showUpdate, value); OnPropertyChanged(nameof(AnyOverlay)); } }

    private string _updateText = "";
    public string UpdateText { get => _updateText; set => SetField(ref _updateText, value); }

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; set => SetField(ref _updateStatus, value); }

    public bool AnyOverlay => ShowAddSource || ShowRemoveSource || ShowUpdate;

    // ===================== КОМАНДЫ =====================

    public RelayCommand OpenAddSourceCommand { get; }
    public RelayCommand OpenRemoveSourceCommand { get; }
    public RelayCommand ConfirmAddSourceCommand { get; }
    public RelayCommand RemoveSelectedSourceCommand { get; }
    public RelayCommand CloseOverlayCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand RefreshTreeCommand { get; }
    public RelayCommand UncheckAllCommand { get; }
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand<TreeNodeViewModel> DeleteNodeCommand { get; }
    public RelayCommand<TreeNodeViewModel> RefreshNodeCommand { get; }
    public AsyncRelayCommand LoadServersCommand { get; }

    private void RaiseCommands()
    {
        BrowseFolderCommand.RaiseCanExecuteChanged();
        RefreshTreeCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        LoadServersCommand.RaiseCanExecuteChanged();
    }

    // ===================== УДАЛЕНИЕ УЗЛОВ ДЕРЕВА =====================

    private void DeleteNode(TreeNodeViewModel node)
    {
        if (node.Kind == NodeKind.Placeholder)
            return;

        // Корневой узел?
        if (RootNodes.Contains(node))
        {
            // Пользовательский источник — удаляем насовсем (из конфигурации)
            var custom = _cfg.CustomSources.FirstOrDefault(s =>
                s.Url.Trim() == node.Url.Trim());
            if (custom != null)
            {
                _config.RemoveCustomSource(_cfg, custom);
                RootNodes.Remove(node);
                Log($"✓ Источник удалён: {node.Title}");
            }
            else
            {
                // Открытый раздел сервера — просто убираем из вида (вернётся выбором сервера)
                RootNodes.Remove(node);
                Log($"✓ Убрано из дерева: {node.Title}");
            }
            return;
        }

        // Вложенный узел — убираем из родителя И запоминаем навсегда
        node.Parent?.Children.Remove(node);
        HideUrl(node.Url);
        node.Parent?.RecomputeAfterRemoval();
        Log($"✓ Удалено: {node.Display}  (вернуть — кнопка «↩ Вернуть удалённые»)");
    }

    // Пометить URL скрытым и сохранить в конфигурации
    private void HideUrl(string url)
    {
        var key = UrlHelper.NormalizeForCompare(url);
        if (string.IsNullOrEmpty(key) || !_hidden.Add(key))
            return;
        _cfg.HiddenUrls.Add(url.Trim());
        _config.SaveConfig(_cfg);
    }

    // «Обновить» = вернуть все удалённые узлы + перестроить всё дерево из кэша
    private void RefreshTree()
    {
        var hadHidden = _hidden.Count > 0;
        if (hadHidden)
        {
            _hidden.Clear();
            _cfg.HiddenUrls.Clear();
            _config.SaveConfig(_cfg);
        }

        // Запоминаем, какие разделы были раскрыты (по URL), чтобы восстановить состояние
        var expanded = new HashSet<string>();
        WalkTree(RootNodes, n =>
        {
            if (n.IsForum && n.IsExpanded)
                expanded.Add(UrlHelper.NormalizeForCompare(n.Url));
        });

        if (RootNodes.Count == 0)
            BuildRootNodes();
        else
            foreach (var root in RootNodes.ToList())
                DeepRebuildFromCache(root, expanded);

        Log(hadHidden
            ? "🔄 Дерево обновлено, все удалённые узлы возвращены."
            : "🔄 Дерево обновлено.");
    }

    // Рекурсивно перестроить узел и всё раскрытое поддерево из кэша (без сети, без фильтра скрытых)
    private void DeepRebuildFromCache(TreeNodeViewModel node, HashSet<string> expanded)
    {
        if (node.Kind != NodeKind.Forum)
            return;

        var key = UrlHelper.NormalizeForCompare(node.Url);
        if (!_nodeCache.TryGetValue(key, out var entry))
            return; // нет в кэше — подтянется из сети при раскрытии

        BuildChildrenFromCache(node, entry);

        foreach (var child in node.Children)
        {
            if (child.Kind == NodeKind.Forum &&
                expanded.Contains(UrlHelper.NormalizeForCompare(child.Url)))
            {
                child.SetExpandedRaw(true);
                DeepRebuildFromCache(child, expanded);
            }
        }
    }

    // ===================== МУЛЬТИВЫДЕЛЕНИЕ (Ctrl/Shift) =====================

    // Плоский список видимых (развёрнутых) узлов — для диапазона по Shift
    public List<TreeNodeViewModel> FlattenVisible()
    {
        var result = new List<TreeNodeViewModel>();
        void Walk(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Kind == NodeKind.Placeholder)
                    continue;
                result.Add(n);
                if (n.IsExpanded && n.Children.Count > 0)
                    Walk(n.Children);
            }
        }
        Walk(RootNodes);
        return result;
    }

    public void ClearSelection()
        => WalkTree(RootNodes, n => { if (n.IsSelected) n.IsSelected = false; });

    public void ToggleSelect(TreeNodeViewModel node)
    {
        if (node.Kind != NodeKind.Placeholder)
            node.IsSelected = !node.IsSelected;
    }

    public void SelectRange(TreeNodeViewModel anchor, TreeNodeViewModel target)
    {
        var flat = FlattenVisible();
        var a = flat.IndexOf(anchor);
        var b = flat.IndexOf(target);
        if (a < 0 || b < 0)
        {
            ToggleSelect(target);
            return;
        }
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        ClearSelection();
        for (var i = lo; i <= hi; i++)
            flat[i].IsSelected = true;
    }

    // Удалить все выделенные узлы
    private void DeleteSelected()
    {
        var selected = new List<TreeNodeViewModel>();
        WalkTree(RootNodes, n => { if (n.IsSelected && n.Kind != NodeKind.Placeholder) selected.Add(n); });

        if (selected.Count == 0)
        {
            Log("ℹ Ничего не выделено. Кликните по узлам с Ctrl или Shift, затем удалите.");
            return;
        }

        foreach (var n in selected)
            DeleteNode(n);

        Log($"🗑 Удалено выделенных: {selected.Count}");
    }

    // ===================== ВЫБОР СЕРВЕРА → РАЗДЕЛА =====================

    // Серверы = категории верхнего уровня форума (Majestic РП | New York, …)
    public ObservableCollection<ServerCategory> Servers { get; } = new();

    // Разделы выбранного сервера (Организации, Жалобы и заявления, …)
    public ObservableCollection<ForumNode> Sections { get; } = new();

    private ServerCategory? _selectedServer;
    public ServerCategory? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (!SetField(ref _selectedServer, value))
                return;

            // Обновляем список разделов выбранного сервера
            Sections.Clear();
            if (value != null)
                foreach (var s in value.Sections)
                    Sections.Add(s);
            SelectedSection = null;
        }
    }

    private ForumNode? _selectedSection;
    public ForumNode? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (SetField(ref _selectedSection, value) && value != null)
                _ = OpenSectionAsync(value);
        }
    }

    private async Task LoadServersAsync()
    {
        var headless = Headless;
        var token = EnsureToken();

        await _browserLock.WaitAsync();
        IsBusy = true;
        Status = "Загрузка списка серверов...";
        List<ServerCategory> servers = new();
        string? error = null;
        try
        {
            servers = await Task.Run(() =>
            {
                _engine ??= new ParserEngine(Log);
                return _engine.LoadServerCategories(headless, token);
            }, token);
        }
        catch (OperationCanceledException) { error = "отменено"; }
        catch (Exception e) { error = e.Message; }
        finally { IsBusy = false; Status = "Готов"; _browserLock.Release(); }

        if (error != null) { Log("⚠ Не удалось загрузить серверы: " + error); return; }

        Servers.Clear();
        foreach (var s in servers)
            Servers.Add(s);
        _config.SaveServers(servers); // сохраняем, чтобы не загружать заново при следующем запуске
        Log($"✓ Найдено серверов: {Servers.Count} (сохранено). Выберите сервер, затем раздел.");
    }

    // Выбрали раздел сервера → открываем его в дереве (сначала из кэша)
    private async Task OpenSectionAsync(ForumNode section)
    {
        var serverName = SelectedServer?.Name ?? "";
        var rootName = string.IsNullOrEmpty(serverName) ? section.Name : $"{serverName} → {section.Name}";

        var sectionNode = new TreeNodeViewModel(NodeKind.Forum, rootName, section.Url, section.Id,
            false, LoadForumChildrenAsync);

        RootNodes.Clear();
        RootNodes.Add(sectionNode);
        SaveLastSection(serverName, section);

        var key = UrlHelper.NormalizeForCompare(section.Url);

        // 1) Мгновенно из кэша
        if (_nodeCache.TryGetValue(key, out var cached))
        {
            BuildChildrenFromCache(sectionNode, cached);
            sectionNode.IsExpanded = true;
            Log($"📦 Раздел «{section.Name}» открыт из кэша. Отметьте нужное и нажмите Старт.");
            return;
        }

        // 2) Из сети
        await LoadForumChildrenAsync(sectionNode, forceRefresh: false);
        sectionNode.IsExpanded = true;
        Log($"✓ Открыт раздел «{section.Name}» сервера «{serverName}». Отметьте нужное и нажмите Старт.");
    }

    private void SaveLastSection(string serverName, ForumNode section)
    {
        _cfg.LastServerName = serverName;
        _cfg.LastSectionUrl = section.Url;
        _cfg.LastSectionName = section.Name;
        _cfg.LastSectionId = section.Id;
        _config.SaveConfig(_cfg);
    }

    // Восстановить последний открытый раздел при запуске (мгновенно, если есть в кэше)
    private void RestoreLastSection()
    {
        if (string.IsNullOrEmpty(_cfg.LastSectionUrl))
            return;

        var rootName = string.IsNullOrEmpty(_cfg.LastServerName)
            ? _cfg.LastSectionName
            : $"{_cfg.LastServerName} → {_cfg.LastSectionName}";

        var node = new TreeNodeViewModel(NodeKind.Forum, rootName, _cfg.LastSectionUrl, _cfg.LastSectionId,
            false, LoadForumChildrenAsync);

        var key = UrlHelper.NormalizeForCompare(_cfg.LastSectionUrl);
        if (_nodeCache.TryGetValue(key, out var cached))
        {
            BuildChildrenFromCache(node, cached);
            node.IsExpanded = true; // уже загружен из кэша — браузер не запускается
        }
        // если в кэше нет — узел останется свёрнутым, раскрытие подгрузит из сети

        RootNodes.Add(node);
    }

    // ===================== ЛОГ =====================

    public void Log(string message)
    {
        void Append()
        {
            _logBuffer.AppendLine(message);
            LogText = _logBuffer.ToString();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Append);
        else
            Append();
    }

    // ===================== ИСТОЧНИКИ (overlays) =====================

    private void OpenAddSource()
    {
        NewSourceUrl = "";
        NewSourceName = "";
        AddSourceError = "";
        ShowRemoveSource = false;
        ShowAddSource = true;
    }

    private void OpenRemoveSource()
    {
        CustomSources.Clear();
        foreach (var s in _cfg.CustomSources)
            CustomSources.Add(s);
        ShowAddSource = false;
        ShowRemoveSource = true;
    }

    private void ConfirmAddSource()
    {
        AddSourceError = "";

        if (string.IsNullOrWhiteSpace(NewSourceUrl))
        {
            AddSourceError = "Укажите URL раздела.";
            return;
        }

        // Добавлять можно только разделы (папки-форумы), не отдельные темы
        if (ConfigService.DetectSourceType(NewSourceUrl) == "thread")
        {
            AddSourceError = "Это ссылка на тему. Добавляйте только разделы (папки) — ссылки вида /forums/...";
            return;
        }

        var (ok, message) = _config.AddSource(_cfg, NewSourceUrl, NewSourceName);
        Log((ok ? "✓ " : "⚠ ") + message);
        if (ok)
        {
            BuildRootNodes();
            ShowAddSource = false;
        }
        else
        {
            AddSourceError = message;
        }
    }

    private void RemoveSelectedSource()
    {
        if (SelectedCustomSource == null)
            return;

        var (ok, message) = _config.RemoveCustomSource(_cfg, SelectedCustomSource);
        Log((ok ? "✓ " : "⚠ ") + message);
        if (ok)
        {
            CustomSources.Remove(SelectedCustomSource);
            BuildRootNodes();
            if (CustomSources.Count == 0)
                ShowRemoveSource = false;
        }
    }

    private void CloseOverlays()
    {
        ShowAddSource = false;
        ShowRemoveSource = false;
        ShowUpdate = false;
    }

    // ===================== ПАПКА =====================

    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите родительскую папку для результатов",
            InitialDirectory = Directory.Exists(OutputBaseDir) ? OutputBaseDir : ""
        };
        if (dialog.ShowDialog() == true)
        {
            OutputBaseDir = dialog.FolderName;
            _cfg.BaseOutputDir = OutputBaseDir;
            _cfg.IsFirstRunCompleted = true;
            _config.SaveConfig(_cfg);
            RefreshResumableRuns();
            Log($"✓ Папка сохранена: {OutputBaseDir}");
        }
    }

    // ===================== СТАРТ =====================

    private ParseSettings BuildSettings()
    {
        var s = new ParseSettings { Headless = Headless };
        switch (SelectedImageModeIndex)
        {
            case 0:
                s.SaveImages = false; s.ParseText = true; s.DownloadOnlyImages = false;
                s.ImageMode = ImageDownloadMode.None; break;
            case 1:
                s.SaveImages = true; s.ParseText = true; s.DownloadOnlyImages = false;
                s.ImageMode = ImageDownloadMode.All; break;
            case 2:
                s.SaveImages = true; s.ParseText = false; s.DownloadOnlyImages = true;
                s.ImageMode = ImageDownloadMode.All; break;
            case 3:
                s.SaveImages = true; s.ParseText = false; s.DownloadOnlyImages = true;
                s.ImageMode = ImageDownloadMode.SelectedPosts;
                s.SelectedPostIds = ParseNumberList(PostIdsText); break;
            case 4:
                s.SaveImages = true; s.ParseText = false; s.DownloadOnlyImages = true;
                s.ImageMode = ImageDownloadMode.SelectedAuthors;
                s.SelectedAuthors = ParseStringList(AuthorsText); break;
            case 5:
                s.SaveImages = true; s.ParseText = true; s.DownloadOnlyImages = false;
                s.ImageMode = ImageDownloadMode.SelectedPosts;
                s.SelectedPostIds = ParseNumberList(PostIdsText); break;
            case 6:
                s.SaveImages = true; s.ParseText = true; s.DownloadOnlyImages = false;
                s.ImageMode = ImageDownloadMode.SelectedAuthors;
                s.SelectedAuthors = ParseStringList(AuthorsText); break;
        }
        return s;
    }

    private static List<int> ParseNumberList(string raw)
    {
        var nums = new List<int>();
        foreach (var part in (raw ?? "").Split(','))
            if (int.TryParse(part.Trim(), out var n))
                nums.Add(n);
        return nums;
    }

    private static List<string> ParseStringList(string raw)
        => (raw ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private async Task StartAsync()
    {
        var checkedThreads = new List<TreeNodeViewModel>();
        var checkedForums = new List<TreeNodeViewModel>();
        WalkTree(RootNodes, n =>
        {
            // Если выше есть ПОЛНОСТЬЮ отмеченный раздел — он берётся рекурсивно,
            // потомков отдельно не считаем (иначе двойная работа).
            if (HasFullyCheckedForumAncestor(n))
                return;

            // Полностью отмеченный раздел → берём рекурсивно все его темы.
            if (n.IsForum && n.IsFullyChecked)
                checkedForums.Add(n);
            // Отмеченная тема (в т.ч. под частично-отмеченным разделом) → берём точечно.
            else if (n.IsThread && n.IsChecked == true && n.CheckEnabled)
                checkedThreads.Add(n);
            // Частично отмеченный раздел (null) — не добавляем; WalkTree спускается
            // внутрь и подберёт конкретные отмеченные темы/подразделы.
        });

        if (checkedThreads.Count == 0 && checkedForums.Count == 0)
        {
            Log("❌ Ничего не выбрано. Отметьте галочками темы 📄 или целые разделы 📁 в дереве слева.");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputBaseDir))
        {
            Log("❌ Не указана папка для результатов");
            return;
        }
        Directory.CreateDirectory(OutputBaseDir);
        _cfg.BaseOutputDir = OutputBaseDir;
        _cfg.IsFirstRunCompleted = true;
        _config.SaveConfig(_cfg);

        var settings = BuildSettings();
        var headless = Headless;
        var forumSources = checkedForums.Select(f => f.ToSource()).ToList();
        var threadInfos = checkedThreads.Select(t => t.ToThreadInfo()).ToList();

        bool isResume = ResumeEnabled && !string.IsNullOrEmpty(SelectedResumeRun)
                        && Directory.Exists(SelectedResumeRun);
        var outputDir = isResume ? SelectedResumeRun! : OutputWriter.BuildOutputDir(OutputBaseDir);

        await _browserLock.WaitAsync();
        IsBusy = true;
        Status = "Парсинг...";
        var token = EnsureToken();

        try
        {
            var (parsed, images, done) = await Task.Run(async () =>
            {
                _engine ??= new ParserEngine(Log);

                var all = new List<ThreadInfo>();
                foreach (var f in forumSources)
                {
                    token.ThrowIfCancellationRequested();
                    Log($"\n📁 Собираю все темы раздела: {f.Name}");
                    all.AddRange(_engine.GatherForumThreads(f, _threadCache, headless, token, _hidden));
                }
                _config.SaveThreadCache(_threadCache);
                all.AddRange(threadInfos);

                var unique = ForumScraper.UniqueThreads(all);
                Log($"\n✅ Итого уникальных тем к парсингу: {unique.Count}");

                return await _engine.RunParsingAsync(unique, settings, outputDir, isResume, token);
            }, token);

            Log("\n✅ ГОТОВО");
            Log($"📊 Успешно обработано тем: {parsed}");
            if (settings.DownloadOnlyImages)
                Log("📝 Режим: только изображения, без парсинга текста");
            if (settings.SaveImages)
                Log($"🖼 Всего скачано изображений: {images}");
            Log($"📂 Все файлы здесь: {outputDir}");
            Status = done ? "Готово" : "Завершено с пропусками";
        }
        catch (OperationCanceledException)
        {
            Log("\n⛔ Остановлено пользователем (запуск можно возобновить)");
            Status = "Остановлено";
        }
        catch (Exception e)
        {
            Log($"\n❌ Критическая ошибка: {e.Message}");
            Status = "Ошибка";
        }
        finally
        {
            IsBusy = false;
            _browserLock.Release();
            RefreshResumableRuns();
            RaiseCommands();
        }
    }

    private void Cancel()
    {
        if (_cts is { IsCancellationRequested: false })
        {
            Log("\n⏹ Останавливаю...");
            _cts.Cancel();
        }
    }

    private CancellationToken EnsureToken()
    {
        if (_cts == null || _cts.IsCancellationRequested)
            _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private void RefreshResumableRuns()
    {
        ResumableRuns.Clear();
        if (string.IsNullOrWhiteSpace(OutputBaseDir) || !Directory.Exists(OutputBaseDir))
            return;
        foreach (var run in OutputWriter.FindResumableRuns(OutputBaseDir))
            ResumableRuns.Add(run);
    }

    public void Shutdown()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _engine?.Dispose();
    }
}
