using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFViewer.Core.Annotations;
using PDFViewer.Core.Pdf;

namespace PDFViewer.App.ViewModels;

/// <summary>썸네일 사이드바.</summary>
public sealed partial class MainViewModel
{
    // 썸네일 폭 (DIP)
    public const double ThumbnailWidth = 120;

    [ObservableProperty]
    private bool _isThumbnailsOpen;

    /// <summary>페이지마다 하나씩. 문서가 바뀌면 다시 만든다.</summary>
    public ObservableCollection<PageThumbnailItem> Thumbnails { get; } = [];

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ToggleThumbnails() => IsThumbnailsOpen = !IsThumbnailsOpen;

    private void RebuildThumbnails(PdfDocument? document)
    {
        Thumbnails.Clear();
        if (document is null)
        {
            return;
        }

        for (var i = 0; i < document.PageCount; i++)
        {
            var size = document.PageSizes[i];
            Thumbnails.Add(new PageThumbnailItem(i, ThumbnailWidth * size.Height / size.Width));
        }
    }

    // 주석이 바뀐 페이지의 썸네일을 다시 그리게 한다.
    private void OnAnnotationPageChangedForThumbnails(int pageIndex)
    {
        if (pageIndex >= 0 && pageIndex < Thumbnails.Count)
        {
            Thumbnails[pageIndex].Revision++;
        }
    }

    partial void OnDocumentChanged(PdfDocument? value) => RebuildThumbnails(value);

    private void HookThumbnailUpdates(AnnotationSet? oldSet, AnnotationSet? newSet)
    {
        if (oldSet is not null)
        {
            oldSet.PageChanged -= OnAnnotationPageChangedForThumbnails;
        }

        if (newSet is not null)
        {
            newSet.PageChanged += OnAnnotationPageChangedForThumbnails;
        }
    }
}

/// <summary>썸네일 목록의 페이지 하나.</summary>
public sealed partial class PageThumbnailItem(int pageIndex, double height) : ObservableObject
{
    public int PageIndex { get; } = pageIndex;

    public int PageNumber => PageIndex + 1;

    /// <summary>썸네일 높이 (DIP). 그리기 전에도 자리를 잡아 두어서 목록이 흔들리지 않게 한다.</summary>
    public double Height { get; } = height;

    /// <summary>주석이 바뀔 때마다 올라간다. 썸네일이 다시 그려진다.</summary>
    [ObservableProperty]
    private int _revision;
}
