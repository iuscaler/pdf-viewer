using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Text;

namespace PDFViewer.App.ViewModels;

/// <summary>텍스트 검색 (Ctrl+F).</summary>
public sealed partial class MainViewModel
{
    // 입력이 멈춘 뒤 이 시간이 지나면 검색한다.
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    // 검색에 쓰는 페이지 텍스트. 문서가 바뀌면 비운다.
    private readonly Dictionary<int, PageText> _searchTexts = new();
    private CancellationTokenSource? _searchCancel;

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    [NotifyCanExecuteChangedFor(nameof(NextMatchCommand), nameof(PreviousMatchCommand))]
    private IReadOnlyList<SearchMatch> _searchResults = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus), nameof(CurrentSearchMatch))]
    private int _currentMatchIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    private bool _isSearching;

    /// <summary>지금 가리키는 검색 결과. 뷰가 이 결과로 스크롤한다.</summary>
    public SearchMatch? CurrentSearchMatch =>
        CurrentMatchIndex >= 0 && CurrentMatchIndex < SearchResults.Count ? SearchResults[CurrentMatchIndex] : null;

    /// <summary>검색 창에 보여주는 상태 ("3/15", "결과 없음" 등).</summary>
    public string SearchStatus =>
        SearchText.Trim().Length == 0 ? ""
        : SearchResults.Count > 0 ? $"{CurrentMatchIndex + 1}/{SearchResults.Count}{(IsSearching ? "…" : "")}"
        : IsSearching ? "검색 중…"
        : "결과 없음";

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void OpenSearch() => IsSearchOpen = true;

    [RelayCommand]
    private void CloseSearch()
    {
        IsSearchOpen = false;
        CancelSearch();
        SearchResults = [];
        CurrentMatchIndex = -1;
    }

    private bool HasSearchResults => SearchResults.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSearchResults))]
    private void NextMatch() => CurrentMatchIndex = (CurrentMatchIndex + 1) % SearchResults.Count;

    [RelayCommand(CanExecute = nameof(HasSearchResults))]
    private void PreviousMatch() =>
        CurrentMatchIndex = (CurrentMatchIndex - 1 + SearchResults.Count) % SearchResults.Count;

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(value);

    /// <summary>문서가 바뀌면 검색을 초기화한다.</summary>
    private void ResetSearch()
    {
        CancelSearch();
        _searchTexts.Clear();
        SearchResults = [];
        CurrentMatchIndex = -1;
        if (IsSearchOpen && SearchText.Trim().Length > 0)
        {
            _ = SearchAsync(SearchText);
        }
    }

    private void CancelSearch()
    {
        _searchCancel?.Cancel();
        _searchCancel = null;
        IsSearching = false;
    }

    /// <summary>
    /// 모든 페이지를 차례로 검색한다. 결과는 찾는 대로 늘어나고, 현재 페이지 이후의 첫 결과로 바로 이동한다.
    /// </summary>
    private async Task SearchAsync(string query)
    {
        CancelSearch();
        SearchResults = [];
        CurrentMatchIndex = -1;
        if (Document is not { } document || query.Trim().Length == 0)
        {
            return;
        }

        var cancel = _searchCancel = new CancellationTokenSource();
        var token = cancel.Token;
        try
        {
            await Task.Delay(SearchDelay, token);
            IsSearching = true;
            var startPage = CurrentPageIndex;
            var results = new List<SearchMatch>();
            for (var page = 0; page < document.PageCount; page++)
            {
                token.ThrowIfCancellationRequested();
                if (!_searchTexts.TryGetValue(page, out var text))
                {
                    _searchTexts[page] = text = await document.GetTextAsync(page);
                    token.ThrowIfCancellationRequested();
                }

                var matches = TextSearch.Find(text, page, query);
                if (matches.Count == 0)
                {
                    continue;
                }

                results.AddRange(matches);
                SearchResults = results.ToArray();
                // 현재 페이지(또는 그 뒤)에서 처음 찾은 결과로 이동한다.
                if (CurrentMatchIndex < 0 && page >= startPage)
                {
                    CurrentMatchIndex = results.Count - matches.Count;
                }
            }

            // 현재 페이지 뒤로는 결과가 없으면 처음 결과로 간다.
            if (CurrentMatchIndex < 0 && results.Count > 0)
            {
                CurrentMatchIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            Debug.WriteLine($"[Search] {ex.Message}");
        }

        if (_searchCancel == cancel)
        {
            IsSearching = false;
            _searchCancel = null;
        }
    }
}
