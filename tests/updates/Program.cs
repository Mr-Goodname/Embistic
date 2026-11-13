using System.Net;
using System.Text;
using System.Text.Json;
using Embistic.Services;

internal static class Program
{
    private static int _checks;
    private static async Task Main()
    {
        foreach (var (latest,current,newer) in new[]
        {
            ("v0.3.1","0.3.0",true), ("v0.3.0","0.3.0",false), ("v0.2.9","0.3.0",false),
            ("1.0.0","0.99.99",true), ("0.3.0","0.3.0-beta.1",true), ("0.3.0-beta.1","0.3.0",false),
            ("0.3.0-beta.2","0.3.0-beta.1",true), ("0.3.0-beta.11","0.3.0-beta.2",true),
            ("0.3.0-beta.2","0.3.0-beta.11",false), ("0.3.0-beta","0.3.0-alpha",true),
            ("0.3.0+build.5","0.3.0+build.4",false),
            ("0.3.0-100000000000000000000000","0.3.0-99999999999999999999999",true),
        })
        {
            using var client=Client(HttpStatusCode.OK, Release(latest));
            using var service=new UpdateService(client,current);
            var info=await service.CheckAsync();
            Check(info?.IsNewer==newer, "semantic version "+latest+" / "+current);
        }
        using (var client=Client(HttpStatusCode.OK,Release("v0.3.1")))
        using (var service=new UpdateService(client,"0.3.0"))
        {
            var info=await service.CheckAsync();
            Check(info?.Version=="0.3.1" && info.ReleaseNotes=="Release notes", "release metadata");
            Check(info?.InstallerUrl=="https://github.com/Mr-Goodname/Embistic/releases/download/v0.3.1/Embistic-0.3.1-setup.exe","installer asset");
            Check(info?.ZipUrl=="https://github.com/Mr-Goodname/Embistic/releases/download/v0.3.1/Embistic-win-x64.zip","portable asset");
        }
        foreach(var invalid in new[]{
            "http://github.com/Mr-Goodname/Embistic/releases/tag/v0.3.0",
            "https://github.com.evil.test/Mr-Goodname/Embistic/releases/tag/v0.3.0",
            "https://github.com/other/Embistic/releases/tag/v0.3.0",
            "https://github.com/Mr-Goodname/Embistic/releases-old/v0.3.0",
            "https://github.com:444/Mr-Goodname/Embistic/releases/tag/v0.3.0",
            "https://secret@github.com/Mr-Goodname/Embistic/releases/tag/v0.3.0",
            "https://github.com/Mr-Goodname/Embistic/releases/tag/v0.3.0?token=secret",
            "https://github.com/Mr-Goodname/Embistic/releases/tag/v0.3.0#fragment"
        }) Check(!UpdateService.IsOfficialPageUrl(invalid), "untrusted page URL rejected");
        Check(UpdateService.IsOfficialPageUrl("https://github.com/Mr-Goodname/Embistic/releases"),"release index URL");
        Check(UpdateService.IsOfficialPageUrl("https://github.com/Mr-Goodname/Embistic/releases/tag/v0.3.0"),"release tag URL");
        Check(!UpdateService.IsOfficialDownloadUrl("https://github.com/Mr-Goodname/Embistic/releases/tag/v0.3.0"),"page is not an asset");
        Check(!UpdateService.IsOfficialDownloadUrl("https://github.com/Mr-Goodname/Embistic/releases/download/"),"empty asset URL");
        using (var client=Client(HttpStatusCode.NotFound,"{}"))
        using (var service=new UpdateService(client,"0.3.0")) Check(await service.CheckAsync() is null,"404 means no published release");
        foreach(var invalidTag in new[]{"latest","v0.03.0","0.3.0-beta..1","0.3.0-beta.01","999999999999999.0.0"})
        {
            using var client=Client(HttpStatusCode.OK,Release(invalidTag));
            using var service=new UpdateService(client,"0.3.0");
            await Expect<InvalidOperationException>(()=>service.CheckAsync(),"invalid tag rejected");
        }
        using (var client=Client(HttpStatusCode.OK,"not json"))
        using (var service=new UpdateService(client,"0.3.0"))
            await Expect<InvalidOperationException>(()=>service.CheckAsync(),"malformed response rejected");
        using (var client=Client(HttpStatusCode.Forbidden,"sensitive response should not be exposed"))
        using (var service=new UpdateService(client,"0.3.0"))
        {
            try { await service.CheckAsync(); throw new Exception("FAIL: expected rate-limit error"); }
            catch(HttpRequestException ex){Check(!ex.Message.Contains("sensitive"),"HTTP response body not exposed");}
        }
        using (var client=new HttpClient(new FaultHandler()))
        using (var service=new UpdateService(client,"0.3.0"))
        {
            try {await service.CheckAsync();throw new Exception("FAIL: expected network error");}
            catch(HttpRequestException ex){Check(!ex.Message.Contains("secret"),"network details hidden");}
        }
        using (var client=Client(HttpStatusCode.OK,Release("v0.3.1")))
        using (var service=new UpdateService(client,"0.3.0"))
        using (var cancellation=new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Expect<OperationCanceledException>(()=>service.CheckAsync(cancellation.Token),"caller cancellation preserved");
        }
        Console.WriteLine($"PASS: {_checks} release/update protocol checks.");
    }
    private static string Release(string tag)=>JsonSerializer.Serialize(new {
        tag_name=tag,html_url="https://github.com/Mr-Goodname/Embistic/releases/tag/"+tag,body="Release notes",
        assets=new[]{
            new{name="Embistic-0.3.1-setup.exe",browser_download_url="https://github.com/Mr-Goodname/Embistic/releases/download/v0.3.1/Embistic-0.3.1-setup.exe"},
            new{name="Embistic-win-x64.zip",browser_download_url="https://github.com/Mr-Goodname/Embistic/releases/download/v0.3.1/Embistic-win-x64.zip"},
            new{name="Embistic-evil-setup.exe",browser_download_url="https://evil.test/setup.exe"},
        }
    });
    private static HttpClient Client(HttpStatusCode status,string body)=>new(new FixtureHandler(status,body));
    private static async Task Expect<T>(Func<Task> action,string label) where T:Exception
    {try{await action();throw new Exception("FAIL: "+label);}catch(T){Check(true,label);}}
    private static void Check(bool value,string label){if(!value)throw new Exception("FAIL: "+label);_checks++;}
    private sealed class FixtureHandler(HttpStatusCode status,string body):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(request.RequestUri?.AbsoluteUri==UpdateService.LatestReleaseApi,"fixed public release endpoint");
            Check(request.Headers.Authorization is null && !request.Headers.Contains("X-GitHub-Token"),"no repository credentials");
            return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    private sealed class FaultHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
            =>throw new HttpRequestException("https://secret.example?token=secret");
    }
}
