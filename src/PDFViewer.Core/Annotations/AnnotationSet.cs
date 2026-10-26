namespace PDFViewer.Core.Annotations;

/// <summary>
/// 문서 하나의 주석 목록과 실행 취소/다시 실행 기록. UI 스레드 한 곳에서만 사용한다.
/// </summary>
public sealed class AnnotationSet
{
    private readonly Dictionary<int, List<InkStroke>> _strokesByPage = new();
    private readonly Stack<Change> _undo = new();
    private readonly Stack<Change> _redo = new();
    private List<Change>? _group;
    private int _groupDepth;

    // 마지막으로 저장한 시점의 실행 취소 기록 개수. 아직 저장한 적이 없으면 0.
    private int _savedUndoCount;
    private bool _savedStateLost;

    /// <summary>페이지의 주석이 바뀌었을 때 (페이지 번호).</summary>
    public event Action<int>? PageChanged;

    /// <summary>실행 취소/다시 실행 가능 여부나 수정 여부가 바뀌었을 때.</summary>
    public event Action? HistoryChanged;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>마지막으로 저장한 뒤 바뀐 내용이 있는지 여부.</summary>
    public bool IsModified => _savedStateLost || _undo.Count != _savedUndoCount;

    public bool HasAnnotations => _strokesByPage.Values.Any(list => list.Count > 0);

    public IReadOnlyList<InkStroke> GetStrokes(int pageIndex) =>
        _strokesByPage.TryGetValue(pageIndex, out var list) ? list : [];

    public IEnumerable<int> PagesWithAnnotations =>
        _strokesByPage.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key).Order();

    public void AddStroke(InkStroke stroke) => Execute(new Change([stroke], []));

    public void RemoveStrokes(IReadOnlyCollection<InkStroke> strokes)
    {
        if (strokes.Count > 0)
        {
            Execute(new Change([], strokes.ToArray()));
        }
    }

    /// <summary>
    /// 페이지의 점 (x, y)에서 반지름 radius 안에 지나가는 획들 (포인트 단위).
    /// 나중에 그린 획(위에 보이는 획)이 앞에 온다.
    /// </summary>
    public IReadOnlyList<InkStroke> HitTest(int pageIndex, double x, double y, double radius) =>
        GetStrokes(pageIndex).Where(s => s.HitTest(x, y, radius)).Reverse().ToList();

    /// <summary>
    /// 이 범위 안에서 일어난 변경을 실행 취소 한 번으로 되돌릴 수 있게 묶는다.
    /// 지우개를 한 번 끌어서 여러 획을 지운 경우에 쓴다.
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
        foreach (var stroke in change.Removed)
        {
            if (_strokesByPage.TryGetValue(stroke.PageIndex, out var list) && list.Remove(stroke))
            {
                pages.Add(stroke.PageIndex);
            }
        }

        foreach (var stroke in change.Added)
        {
            if (!_strokesByPage.TryGetValue(stroke.PageIndex, out var list))
            {
                _strokesByPage[stroke.PageIndex] = list = [];
            }

            list.Add(stroke);
            pages.Add(stroke.PageIndex);
        }

        foreach (var page in pages)
        {
            PageChanged?.Invoke(page);
        }
    }

    /// <summary>한 번의 실행 취소 단위. 추가한 획과 지운 획.</summary>
    private sealed record Change(InkStroke[] Added, InkStroke[] Removed)
    {
        public Change Inverse() => new(Removed, Added);

        public static Change Combine(IReadOnlyList<Change> changes)
        {
            // 묶음 안에서 추가했다가 지운 획은 결과에 남기지 않는다.
            var added = changes.SelectMany(c => c.Added).ToList();
            var removed = new List<InkStroke>();
            foreach (var stroke in changes.SelectMany(c => c.Removed))
            {
                if (!added.Remove(stroke))
                {
                    removed.Add(stroke);
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
