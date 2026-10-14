using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BBDownT.Tests;

public class ApiJsonContractTests
{
    [Fact]
    public void TaskSnapshot_PreservesResponseFieldsAndDetachedSavePaths()
    {
        var task = new DownloadTask("task-42", "1", "fixture", 100);
        task.SetMetadata("Title", "cover.jpg", 90);
        task.ReportDownloadedBytes(100, 100);
        task.AddSavePath("file.mp4");
        task.Finish(102, true);
        var snapshot = task.CreateSnapshot();
        task.AddSavePath("later.mp4");
        task.SetError("later change");

        var json = JsonSerializer.Serialize(snapshot, AppJsonSerializerContext.Default.DownloadTask);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "TaskId":"task-42", "Aid":"1", "Url":"fixture", "TaskCreateTime":100,
              "Title":"Title", "Pic":"cover.jpg", "VideoPubTime":90, "TaskFinishTime":102,
              "Progress":1, "DownloadSpeed":50, "TotalDownloadedBytes":100,
              "IsSuccessful":true, "Error":null, "SavePaths":["file.mp4"]
            }
            """), JsonNode.Parse(json)), json);
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

    [Fact]
    public async Task RequestBinding_TreatsNullLikeAnOmittedOption()
    {
        using var body = Body("""
            {"Url":"BV1xx411c7mD","AccessToken":null,"Cookie":null,"Aria2cProxy":null,"Host":null,
             "FilePattern":null,"DelayPerPage":null,"AudioLanguage":null,"CallBackWebHook":null}
            """);

        var binding = await MyOptionBindingResult<ServeRequestOptions>.BindAsync(Context(body));

        Assert.True(binding.IsValid);
        var request = binding.Result!;
        Assert.Equal("", request.AccessToken);
        Assert.Equal("", request.Cookie);
        Assert.Equal("", request.Aria2cProxy);
        Assert.Equal("api.bilibili.com", request.Host);
        Assert.Equal("", request.FilePattern);
        Assert.Equal("0", request.DelayPerPage);
        Assert.Null(request.AudioLanguage);
        Assert.Null(request.CallBackWebHook);
        Assert.Null(new BBDownTApiServer().ValidateAndNormalizeServerRequest(request));
    }

    [Fact]
    public async Task RequestBinding_StillReportsANullUrlAsMissing()
    {
        using var body = Body("""{"Url":null}""");

        var binding = await MyOptionBindingResult<ServeRequestOptions>.BindAsync(Context(body));

        Assert.True(binding.IsValid);
        Assert.Equal("Url不能为空", new BBDownTApiServer().ValidateAndNormalizeServerRequest(binding.Result!));
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
    public async Task TaskSubmission_ReturnsTheDocumentedTaskIdField()
    {
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        await BBDownTApiServer.SubmissionAccepted("task-42").ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Equal("{\"TaskId\":\"task-42\"}", Encoding.UTF8.GetString(body.ToArray()));
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
