using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using MajesticParser.Models;

namespace MajesticParser.ViewModels;

public enum NodeKind
{
    Forum,       // 📁 раздел (папка) — может содержать подфорумы и темы
    Thread,      // 📄 тема/закон — конечный материал для парсинга
    Placeholder  // ⏳ служебный узел ("Загрузка…" / "Пусто" / ошибка)
}

public class TreeNodeViewModel : ObservableObject
{
    private readonly Func<TreeNodeViewModel, Task>? _loader;
    private bool _isExpanded;
    private bool? _isChecked = false;
    private bool _isLoaded;
    private bool _isLoading;
    private bool _updating; // защита от рекурсии при каскаде вверх/вниз

    public NodeKind Kind { get; }
    public string Title { get; }
    public string Url { get; }
    public string Id { get; }
    public bool IsMissing { get; }
    public TreeNodeViewModel? Parent { get; set; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public bool IsLoaded => _isLoaded;

    public TreeNodeViewModel(NodeKind kind, string title, string url, string id,
        bool isMissing = false, Func<TreeNodeViewModel, Task>? loader = null)
    {
        Kind = kind;
        Title = title;
        Url = url;
        Id = id;
        IsMissing = isMissing;
        _loader = loader;

        // У форумов есть «ленивый» загрузчик — добавляем заглушку,
        // чтобы появилась стрелка разворачивания.
        if (kind == NodeKind.Forum && loader != null)
            Children.Add(Placeholder("Загрузка…"));
    }

    // Добавить уже загруженного потомка (с установкой Parent и наследованием галочки)
    public void AddChild(TreeNodeViewModel child)
    {
        child.Parent = this;
        // Если родитель полностью отмечен — новый потомок тоже отмечается
        if (_isChecked == true && child.CheckEnabled)
            child.ApplyDown(true);
        Children.Add(child);
    }

    // Пометить узел загруженным и заменить детей готовым списком
    public void SetChildrenLoaded(IEnumerable<TreeNodeViewModel> children)
    {
        Children.Clear();
        foreach (var c in children)
            AddChild(c);
        if (Children.Count == 0)
            Children.Add(Placeholder("Пусто — нет подфорумов и тем"));
        _isLoaded = true;

        // Пересчитать своё состояние по детям (могли быть частично отмечены)
        RecomputeFromChildren();
    }

    public static TreeNodeViewModel Placeholder(string text)
        => new(NodeKind.Placeholder, text, "", "");

    // ===== отображение =====

    public string Icon => Kind switch
    {
        NodeKind.Forum => "📁",
        NodeKind.Thread => IsMissing ? "🚫" : "📄",
        _ => "⏳"
    };

    public string Display => Kind switch
    {
        NodeKind.Forum => Title,
        NodeKind.Thread => $"[{Id}] {Title}" + (IsMissing ? "   (пропал с форума)" : ""),
        _ => Title
    };

    public bool HasCheckbox => Kind != NodeKind.Placeholder;
    public bool CheckEnabled => !(Kind == NodeKind.Thread && IsMissing);
    public bool IsForum => Kind == NodeKind.Forum;
    public bool IsThread => Kind == NodeKind.Thread;
    public bool IsDimmed => Kind == NodeKind.Thread && IsMissing;

    public string CheckTooltip => Kind == NodeKind.Forum
        ? "Взять ВСЕ темы этого раздела, включая все подфорумы (рекурсивно)"
        : "Парсить эту тему";

    // ===== состояние =====

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value)
                _ = EnsureLoadedAsync();
        }
    }

    // Трёхпозиционная галочка: true (всё), false (ничего), null (частично — только у папок).
    // Пользовательский клик даёт только true/false; null ставится программно по детям.
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            OnPropertyChanged();

            if (_updating)
                return; // изменение пришло от каскада — не распространяем дальше

            if (value.HasValue)
                ApplyDown(value.Value); // отметить/снять всех потомков

            PropagateUp(); // пересчитать состояние родителей
        }
    }

    public bool IsFullyChecked => _isChecked == true;

    // Проставить конкретное значение этому поддереву без штормов уведомлений
    public void ApplyDown(bool value)
    {
        foreach (var child in Children)
        {
            if (child.Kind == NodeKind.Placeholder)
                continue;
            child._updating = true;
            child.IsChecked = child.CheckEnabled ? value : false;
            child._updating = false;
            child.ApplyDown(value);
        }
    }

    // Пересчитать состояние вверх по дереву на основе детей
    private void PropagateUp()
    {
        var p = Parent;
        if (p == null)
            return;

        p.RecomputeFromChildren();
        p.PropagateUp();
    }

    // Пересчитать состояние после удаления ребёнка (вызывается извне)
    public void RecomputeAfterRemoval()
    {
        RecomputeFromChildren();
        PropagateUp();
    }

    private void RecomputeFromChildren()
    {
        var kids = new System.Collections.Generic.List<TreeNodeViewModel>();
        foreach (var c in Children)
            if (c.Kind != NodeKind.Placeholder)
                kids.Add(c);

        if (kids.Count == 0)
            return;

        bool? state;
        var allTrue = true;
        var allFalse = true;
        foreach (var c in kids)
        {
            if (c._isChecked != true) allTrue = false;
            if (c._isChecked != false) allFalse = false;
        }
        state = allTrue ? true : allFalse ? false : (bool?)null;

        if (_isChecked != state)
        {
            _updating = true;
            IsChecked = state;
            _updating = false;
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    private async Task EnsureLoadedAsync()
    {
        if (_isLoaded || _isLoading || _loader == null)
            return;

        IsLoading = true;
        try
        {
            await _loader(this);
            _isLoaded = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Принудительно сбросить состояние загрузки (для «Обновить»)
    public void ResetLoad()
    {
        _isLoaded = false;
        Children.Clear();
        if (Kind == NodeKind.Forum && _loader != null)
            Children.Add(Placeholder("Загрузка…"));
    }

    // ===== конвертация =====

    public ThreadInfo ToThreadInfo() => new()
    {
        Title = Title, Url = Url, Id = Id, Missing = IsMissing
    };

    public Source ToSource() => new()
    {
        Name = Title, Url = Url, Type = Kind == NodeKind.Thread ? "thread" : "forum"
    };
}
