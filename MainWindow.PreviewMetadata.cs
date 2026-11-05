namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private bool _syncingPreviewFilmstrip;

    private void RefreshPreviewAfterMetadataChange(PhotoItem photo, int previousIndex)
    {
        var fromMap = _mapPreviewRestore != null;
        RefreshPhotos();
        if (fromMap)
        {
            _mapPreviewRestore = _currentView;
            _currentView = _mapPlace.Where(_allPhotos.Contains).ToList();
        }
        var index = _currentView.IndexOf(photo);
        if (index < 0)
        {
            if (_currentView.Count == 0) ClosePreview();
            else ShowPreviewAt(Math.Min(previousIndex, _currentView.Count - 1));
            return;
        }
        // Metadata changes must not reload the image or discard its zoom/pan.
        _previewIndex = index;
        PreviewCounter.Text = $"{index + 1} / {_currentView.Count}";
        PreviewPrevBtn.IsEnabled = index > 0;
        PreviewNextBtn.IsEnabled = index < _currentView.Count - 1;
        PreviewSlideshowBtn.IsEnabled = _currentView.Count > 1;
        _syncingPreviewFilmstrip = true;
        try
        {
            PreviewFilmstrip.ItemsSource = _currentView;
            PreviewFilmstrip.SelectedItem = photo;
            if (!_filmstripHidden && !_previewFullscreen) PreviewFilmstrip.ScrollIntoView(photo);
        }
        finally { _syncingPreviewFilmstrip = false; }
    }
}
