using Microsoft.UI.Xaml;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private bool _resettingFilters;
    private void ResetGalleryForQuery()
    {
        _searchTimer.Stop();
        _currentPage = 0;
        MainScroll.ChangeView(null, 0, null, true);
        RefreshPhotos();
    }
    private void FlushPendingSearch()
    {
        if (_searchTimer.IsEnabled) ResetGalleryForQuery();
    }
    private void CancelImport_Click(object sender, RoutedEventArgs e) => _importCts?.Cancel();
    private void FailPreviewRead(PhotoItem photo, int request)
    {
        if (request != _previewTransitionRequest || _windowClosing) return;
        _previewIsLoading = false;
        ResetPreviewFocusBlur(); PreviewSoftLayer.Opacity = 0; PreviewSoftImage.Source = null;
        PreviewImage.Opacity = PreviewImageOutgoing.Opacity = 0;
        PreviewImageOutgoing.Source = null; _previewLastReady = null;
        ++_exifRequestSeq;
        var message = File.Exists(photo.FilePath)
            ? "文件暂时无法解码。可以重试，或跳过这张照片；原文件不会被修改。"
            : "原文件已移动、删除，或所在磁盘未连接。恢复文件或连接磁盘后可重试。";
        PreviewReadErrorMessage.Text = message; PreviewExif.Text = "拍摄信息暂不可用。";
        UpdatePreviewNavigationState();
        PreviewReadError.Visibility = Visibility.Visible;
        ResumeSlideshowTimer();
    }
    private void UpdatePreviewNavigationState()
    {
        var valid = _previewIndex >= 0 && _previewIndex < _currentView.Count;
        PreviewPrevBtn.IsEnabled = valid && _previewIndex > 0;
        PreviewNextBtn.IsEnabled = valid && _previewIndex < _currentView.Count - 1;
        PreviewPrevBtn.Opacity = PreviewPrevBtn.IsEnabled ? 1 : .35;
        PreviewNextBtn.Opacity = PreviewNextBtn.IsEnabled ? 1 : .35;
        PreviewSlideshowBtn.IsEnabled = _currentView.Count > 1;
        if (_currentView.Count < 2 && _slideshowActive) StopSlideshow();
        PreviewErrorSkipButton.Content = PreviewNextBtn.IsEnabled ? "下一张" : "上一张";
        PreviewErrorSkipButton.IsEnabled = PreviewNextBtn.IsEnabled || PreviewPrevBtn.IsEnabled;
    }
    private void PreviewErrorSkip_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewNextBtn.IsEnabled) ShowPreviewAt(_previewIndex + 1);
        else if (PreviewPrevBtn.IsEnabled) ShowPreviewAt(_previewIndex - 1);
    }
    private void PreviewRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_previewIndex >= 0 && _previewIndex < _currentView.Count && !_previewIsLoading)
            ShowPreviewAt(_previewIndex, true);
    }
}
