using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFViewer.App.Controls;
using PDFViewer.Core.Annotations;

namespace PDFViewer.App.ViewModels;

/// <summary>펜 주석: 도구 선택, 펜 색과 굵기, 실행 취소/다시 실행.</summary>
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

    /// <summary>펜 굵기 (포인트).</summary>
    public static IReadOnlyList<double> PenWidths { get; } = [0.75, 1.5, 3, 6];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(RedoCommand))]
    private AnnotationSet? _annotations;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectTool), nameof(IsPenTool), nameof(IsEraserTool))]
    private AnnotationTool _activeTool = AnnotationTool.Select;

    [ObservableProperty]
    private Color _penColor = Colors.Black;

    [ObservableProperty]
    private double _penWidth = 1.5;

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

    public bool IsEraserTool
    {
        get => ActiveTool == AnnotationTool.Eraser;
        set => SetFromToggle(value, () => ActiveTool = AnnotationTool.Eraser, ToolProperties);
    }

    private static readonly string[] ToolProperties = [nameof(IsSelectTool), nameof(IsPenTool), nameof(IsEraserTool)];

    // 색이나 굵기를 고르면 바로 펜으로 쓸 수 있게 한다.
    partial void OnPenColorChanged(Color value) => ActiveTool = AnnotationTool.Pen;

    partial void OnPenWidthChanged(double value) => ActiveTool = AnnotationTool.Pen;

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
    private void EraserTool() => ActiveTool = AnnotationTool.Eraser;

    private bool CanUndo => Annotations?.CanUndo == true;

    private bool CanRedo => Annotations?.CanRedo == true;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Annotations?.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Annotations?.Redo();

    /// <summary>
    /// 저장하지 않은 주석이 있으면 버려도 되는지 묻는다. 계속해도 되면 true.
    /// 주석 저장은 5단계에서 구현하므로 지금은 경고만 한다.
    /// </summary>
    public bool ConfirmDiscardAnnotations()
    {
        if (Annotations?.IsModified != true)
        {
            return true;
        }

        var result = MessageBox.Show(
            "저장하지 않은 주석이 있습니다.\n\n주석 저장 기능은 아직 없어서, 계속하면 그린 주석이 사라집니다. 계속할까요?",
            AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}
