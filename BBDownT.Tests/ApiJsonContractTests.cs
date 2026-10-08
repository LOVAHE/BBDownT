using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace BBDownT.Tests;

public class ApiJsonContractTests
{
    [Fact]
    public void TaskSnapshot_PreservesResponseFieldsAndDetachedSavePaths()
    {
        var task = new DownloadTask("task-42", "1", "fixture", 100);
        task.SetMetadata("Title", "cover.jpg", 90);
        task.ReportDownloadedBytes(100);
        task.AddSavePath("file.mp4");
        task.SetStream("1/100/1", "P1 · WEB · 4K 超清 3840x2160 HEVC · 192K M4A");
        task.Finish(102, true);
        var snapshot = task.CreateSnapshot();
        task.AddSavePath("later.mp4");
        task.SetStream("1/101/2", "later");
        task.SetError("later change");

        var json = JsonSerializer.Serialize(snapshot, AppJsonSerializerContext.Default.DownloadTask);

        var expected = JsonNode.Parse("""
            {
              "TaskId":"task-42", "Aid":"1", "Url":"fixture", "TaskCreateTime":100,
              "Title":"Title", "Pic":"cover.jpg", "VideoPubTime":90, "TaskFinishTime":102,
              "Progress":1, "DownloadSpeed":50, "TotalDownloadedBytes":100,
              "IsSuccessful":true, "Error":null, "SavePaths":[],
              "Streams":["P1 · WEB · 4K 超清 3840x2160 HEVC · 192K M4A"]
            }
            """)!;
        // 相对路径按任务执行时的工作目录(下载目录)记录为绝对路径
        expected["SavePaths"] = new JsonArray(JsonValue.Create(Path.GetFullPath("file.mp4")));
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(json)), json);
    }

    [Fact]
    public void CollectionAndSubmission_KeepSourceGeneratedResponseContracts()
    {
        var collection = new DownloadTaskCollection([], [], []);
        var collectionJson = JsonSerializer.Serialize(collection, AppJsonSerializerContext.Default.DownloadTaskCollection);
        var submissionJson = JsonSerializer.Serialize(new TaskSubmissionResult("task-42"),
            AppJsonSerializerContext.Default.TaskSubmissionResult);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"Pending":[],"Running":[],"Finished":[]} """),
            JsonNode.Parse(collectionJson)));
        Assert.Equal("""{"TaskId":"task-42"}""", submissionJson);
    }

    [Fact]
    public async Task RequestBinding_PreservesDefaultsAndCallbackOptions()
    {
        using var body = Body("""{"Url":"fixture","AudioOnly":true,"CallBackWebHook":"https://example.test/callback"}""");
        var context = Context(body);

        var binding = await MyOptionBindingResult<ServeRequestOptions>.BindAsync(context);

        Assert.True(binding.IsValid);
        Assert.NotNull(binding.Result);
        Assert.Equal("fixture", binding.Result.Url);
        Assert.True(binding.Result.AudioOnly);
        Assert.True(binding.Result.MultiThread);
        Assert.True(binding.Result.ForceHttp);
        Assert.Equal("https://example.test/callback", binding.Result.CallBackWebHook);
    }

    [Theory]
    [InlineData("null", typeof(NoNullAllowedException))]
    [InlineData("{", typeof(JsonException))]
    public async Task RequestBinding_RejectsNullOrMalformedJson(string json, Type errorType)
    {
        using var body = Body(json);

        var binding = await MyOptionBindingResult<ServeRequestOptions>.BindAsync(Context(body));

        Assert.False(binding.IsValid);
        Assert.Null(binding.Result);
        Assert.IsType(errorType, binding.Exception);
    }

    [Fact]
    public async Task RequestBinding_RejectsTypesOutsideSourceGenerationContext()
    {
        using var body = Body("{}");

        var binding = await MyOptionBindingResult<DownloadTask>.BindAsync(Context(body));

        Assert.False(binding.IsValid);
        Assert.IsType<InvalidOperationException>(binding.Exception);
    }

    [Fact]
    public void UiStatus_ReportsVipAndPerApiTokenState()
    {
        var json = JsonSerializer.Serialize(
            new UiStatus("2.1.4", false, true, "user", true, "年度大会员", false, true),
            AppJsonSerializerContext.Default.UiStatus);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "Version":"2.1.4", "AuthRequired":false, "BiliCookieSaved":true, "BiliUserName":"user",
              "BiliVip":true, "BiliVipLabel":"年度大会员", "TvTokenSaved":false, "AppTokenSaved":true
            }
            """), JsonNode.Parse(json)), json);
    }

    [Fact]
    public void WebAccount_ParsesNavLoginVipAndWbiKey()
    {
        const string vip = """
            {"code":0,"data":{"isLogin":true,"uname":"user","vipStatus":1,"vip_label":{"text":"年度大会员"},
             "wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
            """;
        const string guest = """
            {"code":-101,"data":{"isLogin":false,
             "wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}
            """;

        var account = WebAccount.Parse(vip, out var wbi);
        var anonymous = WebAccount.Parse(guest, out var guestWbi);

        Assert.Equal(new WebAccount(true, "user", true, "年度大会员"), account);
        Assert.Equal(32, wbi?.Length);
        Assert.Equal(WebAccount.Anonymous, anonymous);
        Assert.Equal(wbi, guestWbi);
    }

    private static MemoryStream Body(string json) => new(Encoding.UTF8.GetBytes(json));

    private static DefaultHttpContext Context(Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        return context;
    }
}
