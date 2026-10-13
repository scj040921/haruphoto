using PhotoAlbum;
using System.Diagnostics;
using System.Text.Json;

internal static class PerformanceChecks
{
    public static async Task RunAsync(Action<bool, string> check, string root, bool benchmark)
    {
        var collection = new RangeObservableCollection<int>();
        var notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;
        collection.ReplaceAll(Enumerable.Range(0, 100).ToArray());
        check(notifications == 1 && collection.Count == 100, "page replacement sends one collection notification");
        collection.ReplaceAll(Enumerable.Range(0, 100).ToArray());
        check(notifications == 1, "unchanged page preserves realized containers");
        collection.ReplaceAll(Array.Empty<int>());
        check(notifications == 2 && collection.Count == 0, "empty page clears once");

        var lru = new BoundedLru<int>(2);
        var released = new List<int>();
        lru.Touch(1); lru.Touch(2); lru.Touch(1); lru.Touch(3);
        lru.Trim(_ => false, released.Add);
        check(lru.Count == 2 && released.SequenceEqual(new[] { 2 }), "thumbnail memory evicts least recently used item");
        lru.Touch(4); lru.Trim(v => v == 1, released.Add);
        check(!released.Contains(1) && released.Contains(3), "visible thumbnails remain protected");
        for (var i = 5; i < 10000; i++) { lru.Touch(i); lru.Trim(v => v == 1, released.Add); }
        check(lru.Count == 2, "long browsing does not grow thumbnail residency");

        var now = DateTime.Now;
        var photos = Enumerable.Range(0, 300).Select(i => new PhotoItem { Filename = $"photo-{i:000}.jpg",
            FilePath = $"file-{i}", Category = i % 3 == 0 ? "travel" : "", Rating = i % 6,
            IsFavorite = i % 4 == 0, DateAdded = now.AddDays(-i), DateTaken = i % 7 == 0 ? null : now.AddDays(-i) }).ToList();
        var filters = new[] {
            new GalleryFilter(false,"","",0,0,"","",0), new GalleryFilter(true,"","",0,0,"","",1),
            new GalleryFilter(false,"travel","",2,0,"","photo",2), new GalleryFilter(false,"__UNCATEGORIZED__","",0,7,"","",0),
            new GalleryFilter(false,"",now.ToString("yyyy-MM"),0,0,"","",0), new GalleryFilter(false,"","bad-month",0,0,"","",1),
            new GalleryFilter(false,"","",0,-1,"","",2), new GalleryFilter(false,"","",0,0,"FavUnrated","",0),
            new GalleryFilter(false,"","",0,0,"Unfavorite","TRAVEL",1), new GalleryFilter(false,"","",0,0,"",now.ToString("yyyy-MM"),0)
        };
        check(filters.All(f => GalleryQuery.Apply(photos,f).SequenceEqual(LegacyQuery(photos,f))), "combined gallery query preserves filter and sort results");
        check(GalleryQuery.Page(photos,99,100).Count == 100 && GalleryQuery.Page(photos,99,100)[0] == photos[200], "page index clamps without rebuilding query");
        check(GalleryQuery.Page(new(),0,100).Count == 0, "empty library pagination");

        var scans = Path.Combine(root,"scan"); Directory.CreateDirectory(Path.Combine(scans,"nested"));
        var first = Path.Combine(scans,"a.JPG"); var second = Path.Combine(scans,"nested","b.png");
        await File.WriteAllTextAsync(first,"fixture"); await File.WriteAllTextAsync(second,"fixture");
        await File.WriteAllTextAsync(Path.Combine(scans,"not-a-photo.txt"),"fixture");
        var date = new DateTime(2025,2,3,4,5,6); File.SetLastWriteTime(second,date);
        var found = PhotoFileScanner.Scan(new[] {scans,second,scans},new[] {first});
        check(found.Count == 1 && found[0].Path == second, "shared scanner deduplicates overlapping inputs and known files");
        check(found[0].Modified == date && found[0].Size == 7, "scanner captures source timestamp and size in one pass");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { PhotoFileScanner.Scan(new[] {scans},Array.Empty<string>(),canceled.Token); throw new Exception("Scan ignored cancellation"); }
        catch (OperationCanceledException) { check(true,"background scan observes cancellation"); }

        var cache = Path.Combine(root,"cache"); Directory.CreateDirectory(cache);
        var cacheFiles = Enumerable.Range(0,3).Select(i => ThumbnailCacheMaintenance.CachePath("photo"+i,cache)).ToArray();
        for (var i=0;i<cacheFiles.Length;i++) { await File.WriteAllBytesAsync(cacheFiles[i],new byte[128]); File.SetLastWriteTimeUtc(cacheFiles[i],DateTime.UtcNow.AddDays(i-10)); }
        var keep = Path.Combine(cache,"personal.jpg"); await File.WriteAllTextAsync(keep,"keep");
        Directory.CreateDirectory(Path.Combine(cache,"child")); var nested = Path.Combine(cache,"child",Path.GetFileName(cacheFiles[0])); await File.WriteAllTextAsync(nested,"keep");
        var trimmed = await ThumbnailCacheMaintenance.TrimAsync(cache,256);
        check(trimmed.Removed == 1 && trimmed.FreedBytes == 128 && !File.Exists(cacheFiles[0]), "cache budget removes oldest generated thumbnail first");
        check(File.Exists(keep) && File.Exists(nested) && trimmed.Remaining.Bytes == 256, "cache cleanup preserves unknown files and nested folders");
        var cleared = await ThumbnailCacheMaintenance.TrimAsync(cache,0);
        check(cleared.Removed == 2 && cleared.Remaining.Count == 0 && File.Exists(keep), "manual cache clear only removes regenerable hash-named JPEGs");
        check(ThumbnailCacheMaintenance.CachePath("C:/PHOTO.JPG",cache) == ThumbnailCacheMaintenance.CachePath("c:/photo.jpg",cache), "cache keys remain compatible and case insensitive");
        check(new AppSettings().ThumbnailCacheLimitMb == 128, "legacy settings receive a bounded cache default");

        if (benchmark)
        {
            var large = Enumerable.Range(0,20000).Select(i => new PhotoItem { Filename=$"photo-{i:00000}.jpg",DateAdded=now.AddSeconds(-i),Rating=i%6 }).ToList();
            var filter = filters[0]; var view = GalleryQuery.Apply(large,filter);
            _ = LegacyQuery(large,filter); _ = GalleryQuery.Page(view,0,100);
            var watch = Stopwatch.StartNew(); long checksum = 0;
            for(var i=0;i<50;i++) checksum += LegacyQuery(large,filter).Skip((i%200)*100).Take(100).Count();
            var oldMs = watch.Elapsed.TotalMilliseconds; watch.Restart();
            for(var i=0;i<50;i++) checksum += GalleryQuery.Page(view,i%200,100).Count;
            var newMs = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine("BENCHMARK "+JsonSerializer.Serialize(new {photos=large.Count,pageChanges=50,oldFullRefreshMs=oldMs,newPageSliceMs=newMs,checksum,dataPathOnly=true}));
        }
    }

    // 固定保留旧版的查询规则，用于核对重构前后的结果与翻页成本。
    private static List<PhotoItem> LegacyQuery(IEnumerable<PhotoItem> photos, GalleryFilter f)
    {
        var q=photos;
        if(f.Favorites) q=q.Where(p=>p.IsFavorite);
        if(f.Category.Length>0) q=f.Category=="__UNCATEGORIZED__"?q.Where(p=>p.Category.Length==0):q.Where(p=>p.Category==f.Category);
        if(f.Month.Length>0) q=q.Where(p=>p.TimelineDate.ToString("yyyy-MM")==f.Month);
        if(f.MinRating>0) q=q.Where(p=>p.Rating>=f.MinRating);
        if(f.Days<0) q=q.Where(p=>!p.DateTaken.HasValue);
        if(f.Days>0) {var cutoff=DateTime.Now.AddDays(-f.Days);q=q.Where(p=>p.DateAdded>=cutoff);}
        if(f.Organize=="FavUnrated") q=q.Where(p=>p.IsFavorite&&p.Rating==0);
        if(f.Organize=="Unfavorite") q=q.Where(p=>!p.IsFavorite);
        if(!string.IsNullOrWhiteSpace(f.Search)) q=q.Where(p=>p.Filename.Contains(f.Search,StringComparison.OrdinalIgnoreCase)||p.Category.Contains(f.Search,StringComparison.OrdinalIgnoreCase)||p.DateText.Contains(f.Search,StringComparison.OrdinalIgnoreCase));
        return (f.Sort switch {1=>q.OrderBy(p=>p.Filename,StringComparer.CurrentCultureIgnoreCase),2=>q.OrderByDescending(p=>p.Rating).ThenByDescending(p=>p.DateAdded),_=>q.OrderByDescending(p=>p.DateAdded)}).ToList();
    }
}
