using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private static readonly HttpClient MapGeocoder = CreateMapGeocoder();
    private readonly SemaphoreSlim _mapSearchGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset SavedAt, List<MapSearchResult> Results)> _mapSearchCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _mapSearchLastRequest = DateTimeOffset.MinValue;
    private bool _mapSearchRunning;

    private static HttpClient CreateMapGeocoder()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("haruphoto/1.3.8 (+https://github.com/scj040921/haruphoto)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");
        return client;
    }

    private void MapSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _ = SearchMapPlacesAsync();
    }

    private void MapSearchButton_Click(object sender, RoutedEventArgs e) => _ = SearchMapPlacesAsync();

    private async Task SearchMapPlacesAsync()
    {
        var query = MapSearchBox.Text.Trim();
        if (query.Length == 0 || _mapSearchRunning || MapOverlay.Visibility != Visibility.Visible) return;

        _mapSearchRunning = true;
        MapSearchButton.IsEnabled = false;
        MapStatus.Text = $"正在搜索“{query}”…";
        try
        {
            List<MapSearchResult> results;
            if (_mapSearchCache.TryGetValue(query, out var cached) && cached.SavedAt > DateTimeOffset.UtcNow.AddMinutes(-15))
            {
                results = cached.Results;
            }
            else
            {
                using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
                await _mapSearchGate.WaitAsync(tokenSource.Token);
                try
                {
                    var delay = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - _mapSearchLastRequest);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, tokenSource.Token);
                    _mapSearchLastRequest = DateTimeOffset.UtcNow;
                    var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=8&accept-language=zh-CN&q=" + Uri.EscapeDataString(query);
                    using var response = await MapGeocoder.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, tokenSource.Token);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(tokenSource.Token);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: tokenSource.Token);
                    results = MapSearchParser.Parse(document.RootElement);
                    _mapSearchCache[query] = (DateTimeOffset.UtcNow, results);
                }
                finally { _mapSearchGate.Release(); }
            }

            MapSearchResults.ItemsSource = results;
            MapSearchResultsPanel.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MapStatus.Text = results.Count > 0
                ? $"找到 {results.Count} 个地点 · 选择结果可定位，也可直接放置照片"
                : "没有找到地点；试试更完整的地名。搜索仅在你确认后发送。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MapSearchResults.ItemsSource = null;
            MapSearchResultsPanel.Visibility = Visibility.Collapsed;
            MapStatus.Text = "地名搜索失败，请检查网络后重试。";
            System.Diagnostics.Debug.WriteLine($"Map place search failed: {ex.Message}");
        }
        finally
        {
            _mapSearchRunning = false;
            if (!_windowClosing) MapSearchButton.IsEnabled = true;
        }
    }

    private async void MapSearchResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MapSearchResult result) return;
        MapSearchResultsPanel.Visibility = Visibility.Collapsed;
        if (_mapPlacement.Count > 0)
        {
            await PlacePhotosAsync(result.Latitude, result.Longitude, result.Name);
            return;
        }

        _mapLatitude = result.Latitude;
        _mapLongitude = result.Longitude;
        _mapZoom = Math.Max(_mapZoom, 14);
        MapStatus.Text = $"已定位：{result.Name}";
        await AnimateMapMotionAsync(Math.Clamp(_settings.AnimationDurationMs, 50, 600));
    }
}
