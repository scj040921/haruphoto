using PhotoAlbum;
using System.Text.Json;

int passed = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); passed++; }
void Reject(Action action, string description) { try { action(); } catch (InvalidDataException) { Check(true, description); return; } throw new Exception(description); }
var root = Path.Combine(Path.GetTempPath(), "haruphoto-ui-test-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("HARUPHOTO_TEST_DATA",root);
Directory.CreateDirectory(root);
try
{
    Check(UpdateService.IsNewer("V1.3.2", "1.3.1", out var remote) && remote == "1.3.2", "case-insensitive tags");
    Check(!UpdateService.IsNewer("v1.3.2", "1.3.2", out _) && !UpdateService.IsNewer("v1.3.1", "1.3.2", out _), "no downgrade / repeat upgrade");
    Check(!UpdateService.IsNewer("v1", "1.3.1", out _) && !UpdateService.IsNewer("v1.4-beta", "1.3.1", out _), "reject invalid versions");
    var sha = new string('a',64);
    Check(UpdateService.IsSha256("sha256:" + sha) && !UpdateService.IsSha256(""), "require strong checksums");
    Check(UpdateService.ReadChecksum(sha + "  app.zip\r\n", "app.zip") == sha, "checksum file parsing");
    Reject(() => UpdateService.ReadChecksum(sha + "  other.zip", "app.zip"), "reject checksum for another file");
    var assets = new[] { new { name="installer.zip", browser_download_url="ignored", digest=sha,size=123 }, new {name="haruphoto-v1.3.2-portable.zip",browser_download_url="https://github.com/scj040921/haruphoto/releases/download/v1.3.2/haruphoto-v1.3.2-portable.zip",digest=sha,size=123} };
    using var releaseDoc = JsonDocument.Parse(JsonSerializer.Serialize(new {tag_name="v1.3.2",draft=false,prerelease=false,assets,body="notes"}));
    var release = UpdateService.ParseRelease(releaseDoc.RootElement,"1.3.1")!;
    Check(release.PackageUrl.EndsWith("-portable.zip") && release.Notes == "notes", "choose portable not installer / carry release notes");
    UpdateService.ValidateRelease(release);
    Reject(() => UpdateService.ValidateRelease(release with {PackageUrl="https://evil.invalid/package.zip"}), "reject foreign download source");
    Reject(() => UpdateService.ValidateRelease(release with {Sha256=""}), "reject unverified downloads");
    using var draft = JsonDocument.Parse(JsonSerializer.Serialize(new {tag_name="v1.3.2",draft=true,prerelease=false,assets}));
    Check(UpdateService.ParseRelease(draft.RootElement,"1.3.1") == null, "ignore unpublished drafts");
    using var manifest = JsonDocument.Parse(JsonSerializer.Serialize(new {tag="v1.3.2",version="1.3.2",architecture="win-x64",url=release.PackageUrl,sha256=sha,size=123}));
    Check(UpdateService.ParseManifest(manifest.RootElement,"1.3.1")!.Version == "1.3.2", "API-independent update manifest");
    using var badManifest = JsonDocument.Parse(JsonSerializer.Serialize(new {tag="v1.3.2",version="1.3.2",architecture="arm64",url=release.PackageUrl,sha256=sha,size=123}));
    Reject(() => UpdateService.ParseManifest(badManifest.RootElement,"1.3.1"), "reject incompatible architecture");
    var nightlyTag = "nightly-20261013-12";
    var nightlyAssets = new[] { new {name="haruphoto-"+nightlyTag+"-portable.zip",browser_download_url="https://github.com/scj040921/haruphoto/releases/download/"+nightlyTag+"/haruphoto-"+nightlyTag+"-portable.zip",digest=sha,size=123} };
    using var nightlyDoc=JsonDocument.Parse(JsonSerializer.Serialize(new {tag_name=nightlyTag,draft=false,prerelease=true,assets=nightlyAssets,body="<!-- haruphoto-version:1.3.3 -->",target_commitish=new string('b',40)}));
    Check(UpdateService.ParseRelease(nightlyDoc.RootElement,"1.3.2")==null,"Stable ignores Nightly prereleases");
    var nightly=UpdateService.ParseRelease(nightlyDoc.RootElement,"1.3.3","Nightly","Stable")!;
    Check(nightly.Channel=="Nightly" && nightly.Version=="1.3.3" && nightly.Commit.Length==40,"Stable to Nightly without artificial version inflation");
    UpdateService.ValidateRelease(nightly);
    Check(UpdateService.ParseRelease(nightlyDoc.RootElement,"1.3.3","Nightly","Nightly",nightlyTag)==null,"same Nightly never reinstalls");
    Check(UpdateService.ParseRelease(nightlyDoc.RootElement,"1.3.3","Nightly","Nightly","nightly-20261013-9")!=null,"Nightly build numbers compare numerically");
    Check(UpdateService.ParseRelease(nightlyDoc.RootElement,"1.3.3","Nightly","Nightly","nightly-20261014-1")==null,"Nightly rejects old date");
    Check(UpdateService.ParseRelease(nightlyDoc.RootElement,"1.4.0","Nightly","Nightly","nightly-20261012-9")==null,"Nightly rejects semantic downgrade");
    Check(UpdateService.ParseRelease(releaseDoc.RootElement,"1.3.3","Stable","Nightly",nightlyTag)!=null,"explicit return to Stable allowed even at lower base version");
    Check(UpdateService.ParseRelease(releaseDoc.RootElement,"1.3.1","Nightly")==null,"Nightly ignores Stable releases");
    Check(UpdateService.NormalizeChannel("invalid")=="Stable","invalid saved update channel falls back to Stable");
    Check(UpdateStartup.IsOwnedSignal(Path.Combine(Path.GetTempPath(),"haruphoto-update-"+Guid.NewGuid().ToString("N"),"restarted.json")),"restart handshake accepts owned workspace");
    Check(!UpdateStartup.IsOwnedSignal(Path.Combine(root,"restarted.json")),"restart handshake cannot write outside updater workspace");
    Reject(()=>UpdateService.ValidateRelease(release with {PackageUrl=release.PackageUrl+"?token=secret"}),"update URLs cannot carry credentials");
    Reject(()=>UpdateService.ProxyUrl("https://api.github.com/repos/scj040921/haruphoto/releases"),"metadata API never sent to third-party proxy");
    Check(UpdateService.ProxyUrl(release.PackageUrl)==UpdateService.PublicProxy+release.PackageUrl,"public proxy only wraps official release URL");
    await File.WriteAllTextAsync(Path.Combine(root,"build-info.json"),JsonSerializer.Serialize(new {version="1.3.3",channel="Nightly",tag=nightlyTag,commit=new string('b',40)}));
    Check(BuildIdentity.Load(root,"1.3.3").Channel=="Nightly","installed build identity is independent from settings");
    Check(BuildIdentity.Load(root,"1.3.2").Channel=="Stable","mismatched build metadata rejected");
    using var nightlyManifest=JsonDocument.Parse(JsonSerializer.Serialize(new {tag=nightlyTag,version="1.3.3",channel="Nightly",architecture="win-x64",url=nightly.PackageUrl,sha256=sha,size=123}));
    Check(UpdateService.ParseChannelManifest(nightlyManifest.RootElement,"1.3.3","Nightly","Stable","")?.Channel=="Nightly","direct Release asset fallback understands Nightly");
    Check(UpdateService.ParseChannelManifest(nightlyManifest.RootElement,"1.3.3","Nightly","Nightly",nightlyTag)==null,"fallback also avoids repeat Nightly install");
    Reject(()=>UpdateService.ParseChannelManifest(nightlyManifest.RootElement,"1.3.3","Stable","Stable",""),"fallback cannot cross channels silently");
    Check(UpdateService.ParseChannelManifest(manifest.RootElement,"1.3.1","Stable","Stable","")?.Version=="1.3.2","direct Release asset fallback supports old Stable manifest");
    var bytes = System.Text.Encoding.UTF8.GetBytes("verified package fixture");
    var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    using var client = new HttpClient(new PackageHandler(bytes));
    var validDownload = release with { Sha256=actualHash, PackageSize=bytes.Length };
    var downloaded = await UpdateService.DownloadUsingClientAsync(validDownload,null,CancellationToken.None,client);
    Check((await File.ReadAllBytesAsync(downloaded.PackagePath)).SequenceEqual(bytes), "verified download is complete before closing app");
    UpdateService.CleanupDownload(downloaded.Directory);
    Check(!Directory.Exists(downloaded.Directory), "completed download cleanup");
    var beforeDownloads = Directory.GetDirectories(Path.GetTempPath(),"haruphoto-update-*").ToHashSet();
    try { await UpdateService.DownloadUsingClientAsync(validDownload with {Sha256=sha},null,CancellationToken.None,client); throw new Exception("bad hash accepted"); }
    catch (InvalidDataException) { Check(true,"bad SHA256 download rejected"); }
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await UpdateService.DownloadUsingClientAsync(validDownload,null,canceled.Token,client); throw new Exception("canceled download accepted"); }
    catch (OperationCanceledException) { Check(true,"canceled download aborts safely"); }
    Check(!Directory.GetDirectories(Path.GetTempPath(),"haruphoto-update-*").Except(beforeDownloads).Any(), "failed/canceled downloads leave no temporary payload");
    using var fallbackHandler=new FallbackHandler(bytes);
    using var fallbackClient=new HttpClient(fallbackHandler);
    var viaProxy=await UpdateService.DownloadUsingClientAsync(validDownload with {UsePublicProxy=true},null,CancellationToken.None,fallbackClient);
    Check(fallbackHandler.Urls.Count==2 && fallbackHandler.Urls[0]==validDownload.PackageUrl && fallbackHandler.Urls[1].StartsWith(UpdateService.PublicProxy),"direct failure retries via public proxy");
    Check((await File.ReadAllBytesAsync(viaProxy.PackagePath)).SequenceEqual(bytes),"proxy result still verifies GitHub checksum");
    UpdateService.CleanupDownload(viaProxy.Directory);
    using var corruptProxy=new HttpClient(new FallbackHandler(System.Text.Encoding.UTF8.GetBytes("bad proxy")));
    try { await UpdateService.DownloadUsingClientAsync(validDownload with {UsePublicProxy=true},null,CancellationToken.None,corruptProxy); throw new Exception("untrusted proxy accepted"); }
    catch (InvalidDataException) { Check(true,"corrupt proxy response rejected"); }
    using var canceledProxyHandler=new FallbackHandler(bytes);using var canceledProxyClient=new HttpClient(canceledProxyHandler);
    try { await UpdateService.DownloadUsingClientAsync(validDownload with {UsePublicProxy=true},null,canceled.Token,canceledProxyClient); throw new Exception("canceled proxy accepted"); }
    catch (OperationCanceledException) { Check(canceledProxyHandler.Urls.Count<=1,"user cancellation never retries via proxy"); }
    var original = new AppSettings { BackgroundImagePath="original", WatchedFolders=new() {"original"} };
    var clone = original.Clone(); clone.BackgroundImagePath="changed";clone.WatchedFolders.Clear();
    Check(original.BackgroundImagePath=="original" && original.WatchedFolders.Count==1,"settings cancel preserves original");
    foreach(var profile in AppearanceProfile.BuiltIns()) { var target=original.Clone(); profile.ApplyTo(target); Check(target.WatchedFolders.Count==1,"appearance profile preserves folders: "+profile.Name); }
    var legacy = JsonSerializer.Deserialize<AppSettings>("{\"CardMinWidth\":240}")!;
    Check(legacy.PreviewInfoDefaultVisible && legacy.FilmstripMode==0,"old settings migration");
    Check(original.TrySave(Path.Combine(root,"settings-test.json"),out _),"atomic settings save");
    Check(!original.TrySave(root,out _),"settings failure surfaced");
    var photo=new PhotoItem {Filename="test",FilePath=Path.Combine(root,"photo.png"),DateTaken=new DateTime(2026,1,2),DateAdded=new DateTime(2026,2,3)};
    photo.Rating=100;Check(photo.Rating==5,"rating upper clamp");photo.Rating=-1;Check(photo.Rating==0,"rating lower clamp");
    photo.ThumbnailAspectRatio=double.PositiveInfinity;Check(double.IsFinite(photo.ThumbnailAspectRatio),"finite masonry proportions");
    bool categoryNotification=false;photo.PropertyChanged+=(_,e)=>categoryNotification|=e.PropertyName==nameof(PhotoItem.CategoryTagOpacity);photo.Category="test";Check(categoryNotification,"category badge notifies UI");
    await File.WriteAllTextAsync(photo.FilePath,"fixture");
    var modified=new DateTime(2025,4,5,12,0,0);File.SetLastWriteTime(photo.FilePath,modified);
    var missing=Path.Combine(root,"unmounted.png");
    await File.WriteAllTextAsync(LibraryStore.LibraryPath, JsonSerializer.Serialize(new {version=1,photos=new[] {new {path=photo.FilePath,favorite=true,rating=3,added=photo.DateAdded,taken=photo.DateTaken,category="test"}, new {path=missing,favorite=true,rating=5,added=photo.DateAdded,taken=photo.DateTaken,category="offline"}}}));
    var loaded=await LibraryStore.LoadAsync();Check(loaded.Photos.Count==1 && loaded.MissingCount==1,"skip offline files without crashing");
    Check(loaded.Photos[0].TimelineDate==modified,"source modification time not EXIF or import time");
    await Task.WhenAll(Enumerable.Range(0,15).Select(_=>LibraryStore.SaveAsync(loaded.Photos)));
    using var saved=JsonDocument.Parse(await File.ReadAllTextAsync(LibraryStore.LibraryPath));
    Check(saved.RootElement.GetProperty("photos").GetArrayLength()==2,"concurrent atomic saves preserve offline metadata");
    Check(!Directory.EnumerateFiles(root,"*.tmp").Any(),"no abandoned save temporaries");
    LibraryStore.ForgetMissingEntries(); await LibraryStore.SaveAsync(Array.Empty<PhotoItem>());
    using var cleared=JsonDocument.Parse(await File.ReadAllTextAsync(LibraryStore.LibraryPath));Check(cleared.RootElement.GetProperty("photos").GetArrayLength()==0,"explicit clear forgets offline entries");
    await PerformanceChecks.RunAsync(Check, root, args.Contains("--performance"));
    Console.WriteLine($"TOTAL {passed} PASSED");
    if (args.Contains("--live-check"))
    {
        foreach (var channel in new[] { "Stable", "Nightly" })
        {
            var actual = await UpdateService.CheckLatestAsync(channel, true);
            if (actual != null) { UpdateService.ValidateRelease(actual); Check(actual.Channel==channel,"live GitHub Release selection: "+channel); }
            Console.WriteLine($"LIVE {channel}: {actual?.DisplayVersion ?? "no release"}");
        }
    }
}
finally { Directory.Delete(root,true); }

sealed class PackageHandler(byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}

sealed class FallbackHandler(byte[] bytes) : HttpMessageHandler
{
    public List<string> Urls {get;}=new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Urls.Add(request.RequestUri!.AbsoluteUri);
        if (!request.RequestUri.AbsoluteUri.StartsWith(UpdateService.PublicProxy)) throw new HttpRequestException("simulated direct failure");
        if (request.Headers.Authorization != null) throw new Exception("proxy received credentials");
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
    }
}
