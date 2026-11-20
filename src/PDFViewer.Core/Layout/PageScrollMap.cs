namespace PDFViewer.Core.Layout;

/// <summary>
/// 페이지 단위 보기의 세로 스크롤바 범위. 화면에는 한 줄(1페이지 또는 2페이지)만 보이지만, 스크롤바는 문서 전체에서의
/// 위치를 보여주도록 줄마다 한 칸씩 이어 붙인 가상의 범위를 쓴다. 한 칸의 길이는 그 줄의 높이(위아래 여백 포함)와
/// 화면 높이 중 큰 쪽이다. 그래서 줄이 화면에 다 들어오면 칸 길이는 화면 높이가 되고, 줄이 화면보다 길면
/// 그 줄 안에서 스크롤할 수 있는 만큼 칸이 길어진다.
/// </summary>
public sealed class PageScrollMap
{
    // _starts[i]는 i번째 줄 칸의 시작, _starts[^1]은 전체 길이
    private readonly double[] _starts;

    public PageScrollMap(DocumentLayout layout, double margin, double viewportHeight)
    {
        Layout = layout;
        ViewportHeight = viewportHeight;
        _starts = new double[layout.Rows.Count + 1];
        for (var i = 0; i < layout.Rows.Count; i++)
        {
            _starts[i + 1] = _starts[i] + Math.Max(layout.Rows[i].Height + margin * 2, viewportHeight);
        }
    }

    /// <summary>이 범위를 만든 레이아웃 (레이아웃이나 화면 높이가 바뀌면 새로 만들어야 한다).</summary>
    public DocumentLayout Layout { get; }

    public double ViewportHeight { get; }

    /// <summary>스크롤바 전체 길이.</summary>
    public double Extent => _starts[^1];

    /// <summary>줄 칸의 시작 위치.</summary>
    public double StartOf(int rowIndex) => _starts[rowIndex];

    /// <summary>스크롤바 위치가 가리키는 줄과, 그 줄 안에서의 위치 (0 이상, 칸 길이 - 화면 높이 이하).</summary>
    public (int RowIndex, double Offset) Locate(double offset)
    {
        var rows = _starts.Length - 1;
        if (rows <= 0)
        {
            return (0, 0);
        }

        offset = Math.Clamp(offset, 0, Math.Max(0, Extent - ViewportHeight));
        // offset이 들어 있는 칸: _starts[row] <= offset < _starts[row + 1] 인 가장 큰 row
        var index = Array.BinarySearch(_starts, 0, rows, offset);
        var row = index >= 0 ? index : ~index - 1;
        row = Math.Clamp(row, 0, rows - 1);
        var maxInRow = Math.Max(0, _starts[row + 1] - _starts[row] - ViewportHeight);
        return (row, Math.Clamp(offset - _starts[row], 0, maxInRow));
    }
}
