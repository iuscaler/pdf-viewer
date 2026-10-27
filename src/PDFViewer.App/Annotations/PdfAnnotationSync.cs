using System.Diagnostics;
using PDFViewer.Core.Annotations;
using PDFViewer.Core.Pdf;

namespace PDFViewer.App.Annotations;

/// <summary>
/// 앱의 주석 목록(<see cref="AnnotationSet"/>)이 바뀔 때마다 같은 변경을 PDF 문서(메모리)의 실제 PDF 주석에 반영한다.
/// 실행 취소, 다시 실행, 지우개도 모두 여기를 거친다.
/// </summary>
/// <remarks>
/// PDF 작업은 PDFium 스레드의 작업 큐에 순서대로 들어간다. 주석 변경 직후 뷰가 요청하는 페이지 렌더링은
/// 이 작업들 뒤에 실행되므로, 새로 렌더링한 비트맵에는 항상 변경된 주석이 들어 있다.
/// </remarks>
internal sealed class PdfAnnotationSync : IDisposable
{
    private readonly PdfDocument _document;
    private readonly AnnotationSet _annotations;

    public PdfAnnotationSync(PdfDocument document, AnnotationSet annotations)
    {
        _document = document;
        _annotations = annotations;
        _annotations.Changed += OnChanged;
    }

    public void Dispose() => _annotations.Changed -= OnChanged;

    private void OnChanged(IReadOnlyList<Annotation> added, IReadOnlyList<Annotation> removed)
    {
        foreach (var annotation in removed)
        {
            Run(_document.RemoveAnnotationAsync(annotation.PageIndex, annotation.Name));
        }

        foreach (var annotation in added)
        {
            switch (annotation)
            {
                case InkStroke stroke:
                    // 윤곽선 계산은 WPF 객체를 쓰므로 UI 스레드에서 미리 해서 넘긴다.
                    Run(_document.AddInkAnnotationAsync(stroke, InkGeometry.GetOutline(stroke)));
                    break;
                case TextHighlight highlight:
                    Run(_document.AddTextHighlightAsync(highlight));
                    break;
            }
        }
    }

    private static async void Run(Task task)
    {
        try
        {
            await task;
        }
        catch (ObjectDisposedException)
        {
            // 문서를 닫는 중이면 무시한다.
        }
        catch (Exception ex)
        {
            // 주석 하나를 PDF에 넣지 못해도 앱은 계속 동작해야 한다. 그 주석은 다시 렌더링한 화면과 저장 파일에서 빠진다.
            Debug.WriteLine($"[PdfAnnotationSync] {ex}");
        }
    }
}
