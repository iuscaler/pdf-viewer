using PDFViewer.Core.Annotations;

namespace PDFViewer.Core.Tests;

public sealed class AnnotationSetTests
{
    // (0,0) → (100,0) 가로선, 굵기 2pt
    private static InkStroke Line(int page = 0, float y = 0) =>
        new(page, [new InkPoint(0, y, 0.5f), new InkPoint(100, y, 0.5f)], 0xFF000000, 2);

    [Fact]
    public void Add_StoresPerPageAndRaisesEvents()
    {
        var set = new AnnotationSet();
        var changedPages = new List<int>();
        set.PageChanged += changedPages.Add;

        var stroke = Line(page: 3);
        set.Add(stroke);

        Assert.Equal([stroke], set.GetAnnotations(3));
        Assert.Empty(set.GetAnnotations(0));
        Assert.Equal([3], changedPages);
        Assert.Equal([3], set.PagesWithAnnotations);
        Assert.True(set.IsModified);
        Assert.True(set.CanUndo);
    }

    [Fact]
    public void UndoRedo_AddAndRemove()
    {
        var set = new AnnotationSet();
        var a = Line();
        var b = Line(y: 50);
        set.Add(a);
        set.Add(b);
        set.Remove([a]);

        Assert.Equal([b], set.GetAnnotations(0));

        set.Undo(); // 지운 a 되살리기
        Assert.Equal(2, set.GetAnnotations(0).Count);

        set.Undo(); // b 추가 취소
        set.Undo(); // a 추가 취소
        Assert.Empty(set.GetAnnotations(0));
        Assert.False(set.CanUndo);

        set.Redo();
        set.Redo();
        set.Redo();
        Assert.Equal([b], set.GetAnnotations(0));
        Assert.False(set.CanRedo);
    }

    [Fact]
    public void NewChange_ClearsRedo()
    {
        var set = new AnnotationSet();
        set.Add(Line());
        set.Undo();

        set.Add(Line(y: 10));

        Assert.False(set.CanRedo);
    }

    [Fact]
    public void Group_UndoesAsOneStep()
    {
        var set = new AnnotationSet();
        var a = Line();
        var b = Line(y: 50);
        set.Add(a);
        set.Add(b);

        using (set.BeginGroup())
        {
            set.Remove([a]);
            set.Remove([b]);
        }

        Assert.Empty(set.GetAnnotations(0));

        set.Undo();

        Assert.Equal(2, set.GetAnnotations(0).Count);
    }

    [Fact]
    public void EmptyGroup_RecordsNothing()
    {
        var set = new AnnotationSet();
        using (set.BeginGroup())
        {
        }

        Assert.False(set.CanUndo);
        Assert.False(set.IsModified);
    }

    [Fact]
    public void IsModified_TracksSavedPoint()
    {
        var set = new AnnotationSet();
        set.Add(Line());
        set.MarkSaved();
        Assert.False(set.IsModified);

        set.Add(Line(y: 10));
        Assert.True(set.IsModified);

        set.Undo();
        Assert.False(set.IsModified); // 저장한 상태로 돌아옴

        set.Undo();
        Assert.True(set.IsModified);

        // 저장 시점보다 앞으로 돌아간 뒤 새로 고치면 다시 저장 상태가 될 수 없다.
        set.Add(Line(y: 20));
        set.Undo();
        set.Redo();
        Assert.True(set.IsModified);
    }

    [Theory]
    [InlineData(50, 0, 0, true)] // 선 위
    [InlineData(50, 3, 0, false)] // 굵기 2pt → 1pt까지만
    [InlineData(50, 3, 2.5, true)] // 지우개 반지름으로 닿음
    [InlineData(103, 0, 1, false)] // 끝점 밖
    [InlineData(101.5, 0, 1, true)] // 끝점 근처 (반지름 1 + 굵기 절반 1)
    public void Stroke_HitTest(double x, double y, double radius, bool expected)
    {
        Assert.Equal(expected, Line().HitTest(x, y, radius));
    }

    [Fact]
    public void HitTest_ReturnsTopmostFirst()
    {
        var set = new AnnotationSet();
        var below = Line();
        var above = Line();
        set.Add(below);
        set.Add(above);

        Assert.Equal([above, below], set.HitTest(0, 50, 0, 1));
        Assert.Empty(set.HitTest(1, 50, 0, 1));
    }

    [Fact]
    public void Stroke_RequiresPoints()
    {
        Assert.Throws<ArgumentException>(() => new InkStroke(0, [], 0xFF000000, 1));
    }
}
