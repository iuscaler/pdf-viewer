using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFViewer.Core.Pdf;

namespace PDFViewer.App.ViewModels;

/// <summary>저장, 다른 이름으로 저장, 닫거나 다른 파일을 열 때의 저장 확인.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Ctrl+S. 지금 파일에 저장한다.</summary>
    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private async Task SaveAsync()
    {
        if (Document is { } document)
        {
            await SaveToAsync(document.FilePath);
        }
    }

    /// <summary>Ctrl+Shift+S. 저장할 위치를 물어서 저장하고, 이후에는 그 파일을 편집한다.</summary>
    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private async Task SaveAsAsync() => await SaveAsWithDialogAsync();

    private async Task<bool> SaveAsWithDialogAsync()
    {
        if (Document is not { } document)
        {
            return false;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PDF 파일 (*.pdf)|*.pdf",
            Title = "다른 이름으로 저장",
            FileName = Path.GetFileName(document.FilePath),
            InitialDirectory = Path.GetDirectoryName(document.FilePath),
            DefaultExt = ".pdf",
        };

        return dialog.ShowDialog() == true && await SaveToAsync(dialog.FileName);
    }

    /// <summary>저장하고 성공하면 true. 실패하면 이유를 알리고, 다른 위치에 저장할지 묻는다.</summary>
    private async Task<bool> SaveToAsync(string path)
    {
        if (Document is not { } document)
        {
            return false;
        }

        IsLoading = true;
        try
        {
            await document.SaveAsync(path);
            Annotations?.MarkSaved();
            OnPropertyChanged(nameof(Title));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfException)
        {
            var result = MessageBox.Show(
                $"{Path.GetFileName(path)}에 저장하지 못했습니다.\n\n{ex.Message}\n\n다른 위치에 저장할까요?",
                AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes && await SaveAsWithDialogAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>저장하지 않은 변경이 있는지 여부.</summary>
    public bool HasUnsavedChanges => Annotations?.IsModified == true && Document is not null;

    /// <summary>
    /// 저장하지 않은 변경이 있으면 저장할지 묻는다. 계속 진행해도 되면 true
    /// (저장했거나, 저장하지 않기로 했거나, 바뀐 것이 없을 때). 취소하면 false.
    /// </summary>
    public async Task<bool> ConfirmSaveChangesAsync()
    {
        if (Annotations?.IsModified != true || Document is not { } document)
        {
            return true;
        }

        var result = MessageBox.Show(
            $"{Path.GetFileName(document.FilePath)}의 변경 내용을 저장할까요?",
            AppName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        return result switch
        {
            MessageBoxResult.Yes => await SaveToAsync(document.FilePath),
            MessageBoxResult.No => true,
            _ => false,
        };
    }
}
