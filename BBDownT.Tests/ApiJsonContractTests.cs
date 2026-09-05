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

    private static MemoryStream Body(string json) => new(Encoding.UTF8.GetBytes(json));

    private static DefaultHttpContext Context(Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        return context;
    }
}
