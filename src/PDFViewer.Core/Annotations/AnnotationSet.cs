namespace PDFViewer.Core.Annotations;

/// <summary>
/// 문서 하나의 주석 목록과 실행 취소/다시 실행 기록. UI 스레드 한 곳에서만 사용한다.
/// </summary>
public sealed class AnnotationSet
{
    private readonly Dictionary<int, List<Annotation>> _annotationsByPage = new();
    private readonly Stack<Change> _undo = new();
    private readonly Stack<Change> _redo = new();
    private List<Change>? _group;
    private int _groupDepth;

    // 마지막으로 저장한 시점의 실행 취소 기록 개수. 아직 저장한 적이 없으면 0.
    private int _savedUndoCount;
    private bool _savedStateLost;

    /// <summary>
    /// 주석이 추가되거나 지워졌을 때 (추가된 주석, 지워진 주석). 실행 취소와 다시 실행도 포함한다.
    /// <see cref="PageChanged"/>보다 먼저 발생한다.
    /// </summary>
    public event Action<IReadOnlyList<Annotation>, IReadOnlyList<Annotation>>? Changed;

    /// <summary>페이지의 주석이 바뀌었을 때 (페이지 번호).</summary>
    public event Action<int>? PageChanged;

    /// <summary>실행 취소/다시 실행 가능 여부나 수정 여부가 바뀌었을 때.</summary>
    public event Action? HistoryChanged;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>마지막으로 저장한 뒤 바뀐 내용이 있는지 여부.</summary>
    public bool IsModified => _savedStateLost || _undo.Count != _savedUndoCount;

    public bool HasAnnotations => _annotationsByPage.Values.Any(list => list.Count > 0);

    public IReadOnlyList<Annotation> GetAnnotations(int pageIndex) =>
        _annotationsByPage.TryGetValue(pageIndex, out var list) ? list : [];

    public IEnumerable<int> PagesWithAnnotations =>
        _annotationsByPage.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key).Order();

    public void Add(Annotation annotation) => Execute(new Change([annotation], []));

    /// <summary>
    /// 주석들을 지운다. 같은 페이지에 이름이 같은 주석(한 PDF 주석에서 나온 여러 획)이 있으면 함께 지운다.
    /// </summary>
    public void Remove(IReadOnlyCollection<Annotation> annotations)
    {
        if (annotations.Count == 0)
        {
            return;
        }

        var names = annotations.Select(a => (a.PageIndex, a.Name)).ToHashSet();
        var all = annotations
            .Concat(names.SelectMany(n => GetAnnotations(n.PageIndex).Where(a => a.Name == n.Name)))
            .Distinct()
            .ToArray();
        Execute(new Change([], all));
    }

    /// <summary>
    /// 파일에 이미 들어 있던 주석을 목록에 넣는다. 실행 취소 기록에 남지 않고, 수정한 것으로 보지 않으며,
    /// 이벤트도 내지 않는다(이미 PDF와 화면에 있으므로). 이후로는 지우개와 실행 취소의 대상이 된다.
    /// 같은 페이지에 이름이 같은 주석이 이미 있으면(읽기 전에 사용자가 그린 주석) 건너뛴다.
    /// </summary>
    public void Load(IEnumerable<Annotation> annotations)
    {
        var existing = new HashSet<(int, string)>();
        foreach (var group in annotations.GroupBy(a => a.PageIndex))
        {
            if (!_annotationsByPage.TryGetValue(group.Key, out var list))
            {
                _annotationsByPage[group.Key] = list = [];
            }

            existing.Clear();
            existing.UnionWith(list.Select(a => (a.PageIndex, a.Name)));
            list.AddRange(group.Where(a => !existing.Contains((a.PageIndex, a.Name))));
        }
    }

    /// <summary>
    /// 페이지의 점 (x, y)에서 반지름 radius 안에 닿는 주석들 (포인트 단위).
    /// 나중에 추가한 주석(위에 보이는 주석)이 앞에 온다.
    /// </summary>
    public IReadOnlyList<Annotation> HitTest(int pageIndex, double x, double y, double radius) =>
        GetAnnotations(pageIndex).Where(a => a.HitTest(x, y, radius)).Reverse().ToList();

    /// <summary>
    /// 이 범위 안에서 일어난 변경을 실행 취소 한 번으로 되돌릴 수 있게 묶는다.
    /// 지우개를 한 번 끌어서 여러 주석을 지운 경우에 쓴다.
    /// </summary>
    public IDisposable BeginGroup()
    {
        if (_groupDepth++ == 0)
        {
            _group = [];
        }

        return new GroupScope(this);
    }

    public void Undo()
    {
        if (_undo.TryPop(out var change))
        {
            Apply(change.Inverse());
            _redo.Push(change);
            HistoryChanged?.Invoke();
        }
    }

    public void Redo()
    {
        if (_redo.TryPop(out var change))
        {
            Apply(change);
            _undo.Push(change);
            HistoryChanged?.Invoke();
        }
    }

    /// <summary>현재 상태를 저장된 상태로 표시한다.</summary>
    public void MarkSaved()
    {
        _savedUndoCount = _undo.Count;
        _savedStateLost = false;
        HistoryChanged?.Invoke();
    }

    private void Execute(Change change)
    {
        Apply(change);
        if (_group is not null)
        {
            _group.Add(change);
            return;
        }

        Record(change);
    }

    private void Record(Change change)
    {
        // 저장한 시점보다 앞으로 되돌린 뒤 새로 고치면 저장된 상태로는 다시 돌아갈 수 없다.
        if (_undo.Count < _savedUndoCount)
        {
            _savedStateLost = true;
        }

        _undo.Push(change);
        _redo.Clear();
        HistoryChanged?.Invoke();
    }

    private void EndGroup()
    {
        if (--_groupDepth > 0 || _group is not { } group)
        {
            return;
        }

        _group = null;
        if (group.Count > 0)
        {
            Record(Change.Combine(group));
        }
    }

    private void Apply(Change change)
    {
        var pages = new HashSet<int>();
        var removed = new List<Annotation>();
        foreach (var annotation in change.Removed)
        {
            if (_annotationsByPage.TryGetValue(annotation.PageIndex, out var list) && list.Remove(annotation))
            {
                pages.Add(annotation.PageIndex);
                removed.Add(annotation);
            }
        }

        foreach (var annotation in change.Added)
        {
            if (!_annotationsByPage.TryGetValue(annotation.PageIndex, out var list))
            {
                _annotationsByPage[annotation.PageIndex] = list = [];
            }

            list.Add(annotation);
            pages.Add(annotation.PageIndex);
        }

        if (removed.Count > 0 || change.Added.Length > 0)
        {
            Changed?.Invoke(change.Added, removed);
        }

        foreach (var page in pages)
        {
            PageChanged?.Invoke(page);
        }
    }

    /// <summary>한 번의 실행 취소 단위. 추가한 주석과 지운 주석.</summary>
    private sealed record Change(Annotation[] Added, Annotation[] Removed)
    {
        public Change Inverse() => new(Removed, Added);

        public static Change Combine(IReadOnlyList<Change> changes)
        {
            // 묶음 안에서 추가했다가 지운 주석은 결과에 남기지 않는다.
            var added = changes.SelectMany(c => c.Added).ToList();
            var removed = new List<Annotation>();
            foreach (var annotation in changes.SelectMany(c => c.Removed))
            {
                if (!added.Remove(annotation))
                {
                    removed.Add(annotation);
                }
            }

            return new Change(added.ToArray(), removed.ToArray());
        }
    }

    private sealed class GroupScope(AnnotationSet owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.EndGroup();
            }
        }
    }
}
