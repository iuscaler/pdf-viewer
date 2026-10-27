using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFViewer.App.Controls;
using PDFViewer.Core.Annotations;

namespace PDFViewer.App.ViewModels;

/// <summary>주석: 도구 선택, 펜/형광펜 색과 굵기, 실행 취소/다시 실행.</summary>
public sealed partial class MainViewModel
{
    public static IReadOnlyList<Color> PenColors { get; } =
    [
        Colors.Black,
        Color.FromRgb(0xE8, 0x11, 0x23), // 빨강
        Color.FromRgb(0x00, 0x63, 0xB1), // 파랑
        Color.FromRgb(0x10, 0x7C, 0x10), // 초록
        Color.FromRgb(0xF7, 0x63, 0x0C), // 주황
        Color.FromRgb(0x88, 0x17, 0x98), // 보라
    ];

    // 굵기 슬라이더 범위 (포인트). 텍스트 형광펜은 글자 높이를 따르므로 자유 형광펜에만 적용된다.
    public const double PenMinWidth = 0.5;
    public const double PenMaxWidth = 12;
    public const double HighlighterMinWidth = 4;
    public const double HighlighterMaxWidth = 30;

    public static IReadOnlyList<Color> HighlighterColors { get; } =
    [
        Color.FromRgb(0xFF, 0xEB, 0x3B), // 노랑
        Color.FromRgb(0x8B, 0xE0, 0x4E), // 연두
        Color.FromRgb(0x4F, 0xC3, 0xF7), // 하늘
        Color.FromRgb(0xFF, 0x80, 0xAB), // 분홍
        Color.FromRgb(0xFF, 0xB7, 0x4D), // 주황
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(RedoCommand))]
    private AnnotationSet? _annotations;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectTool), nameof(IsPenTool), nameof(IsHighlighterTool),
        nameof(IsEraserTool))]
    private AnnotationTool _activeTool = AnnotationTool.Select;

    [ObservableProperty]
    private Color _penColor = Colors.Black;

    [ObservableProperty]
    private double _penWidth = 1.5;

    [ObservableProperty]
    private Color _highlighterColor = HighlighterColors[0];

    [ObservableProperty]
    private double _highlighterWidth = 12;

    public bool IsSelectTool
    {
        get => ActiveTool == AnnotationTool.Select;
        set => SetFromToggle(value, () => ActiveTool = AnnotationTool.Select, ToolProperties);
    }

    public bool IsPenTool
    {
        get => ActiveTool == AnnotationTool.Pen;
        set => SetFromToggle(value, () => ActiveTool = AnnotationTool.Pen, ToolProperties);
    }

    public bool IsHighlighterTool
    {
        get => ActiveTool == AnnotationTool.Highlighter;
        set => SetFromToggle(value, () => ActiveTool = AnnotationTool.Highlighter, ToolProperties);
    }

    public bool IsEraserTool
    {
        get => ActiveTool == AnnotationTool.Eraser;
        set => SetFromToggle(value, () => ActiveTool = AnnotationTool.Eraser, ToolProperties);
    }

    private static readonly string[] ToolProperties =
        [nameof(IsSelectTool), nameof(IsPenTool), nameof(IsHighlighterTool), nameof(IsEraserTool)];

    // 색이나 굵기를 고르면 바로 그 도구로 쓸 수 있게 한다.
    partial void OnPenColorChanged(Color value) => ActiveTool = AnnotationTool.Pen;

    partial void OnPenWidthChanged(double value) => ActiveTool = AnnotationTool.Pen;

    partial void OnHighlighterColorChanged(Color value) => ActiveTool = AnnotationTool.Highlighter;

    partial void OnHighlighterWidthChanged(double value) => ActiveTool = AnnotationTool.Highlighter;

    partial void OnAnnotationsChanged(AnnotationSet? oldValue, AnnotationSet? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.HistoryChanged -= OnAnnotationHistoryChanged;
        }

        if (newValue is not null)
        {
            newValue.HistoryChanged += OnAnnotationHistoryChanged;
        }

        HookThumbnailUpdates(oldValue, newValue);
    }

    private void OnAnnotationHistoryChanged()
    {
        OnPropertyChanged(nameof(Title));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void SelectTool() => ActiveTool = AnnotationTool.Select;

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void PenTool() => ActiveTool = AnnotationTool.Pen;

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void HighlighterTool() => ActiveTool = AnnotationTool.Highlighter;

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void EraserTool() => ActiveTool = AnnotationTool.Eraser;

    private bool CanUndo => Annotations?.CanUndo == true;

    private bool CanRedo => Annotations?.CanRedo == true;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Annotations?.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Annotations?.Redo();
}
