using BBDownT.Core;
using BBDownT.Core.Protobuf;
using Google.Protobuf;
using System.Text.Json;

namespace BBDownT.Tests;

public class AppHelperMessageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    public void PackAndReadMessage_RoundTripsPayload(int length)
    {
        var payload = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();

        var decoded = AppHelper.ReadMessage(AppHelper.PackMessage(payload));

        Assert.Equal(payload, decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ReadMessage_RejectsTruncatedHeader(int length)
    {
        Assert.Throws<InvalidDataException>(() => AppHelper.ReadMessage(new byte[length]));
    }

    [Fact]
    public void ReadMessage_RejectsDeclaredPayloadLargerThanAvailableData()
    {
        var frame = new byte[] { 0, 0, 0, 0, 10, 1, 2 };

        Assert.Throws<InvalidDataException>(() => AppHelper.ReadMessage(frame));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AppPreviewStatus_SurvivesConversionToParsedResult(bool? preview)
    {
        var fixture = CreatePlaybackReply(preview);
        var reply = PlayViewReply.Parser.ParseFrom(fixture.ToByteArray());
        var json = AppHelper.ConvertToDashJson(reply);
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(preview == true, data.GetProperty("is_preview").GetBoolean());
        Assert.Equal(reply.VideoInfo.Timelength, data.GetProperty("timelength").GetUInt64());

        var fetchCount = 0;
        var result = await Parser.ExtractTracksWithFetcherAsync(
            "ep:1", "2", "3", false, false, true, "0",
            _ =>
            {
                fetchCount++;
                return Task.FromResult(json);
            },
            (_, _) => throw new InvalidOperationException("APP playback must not request an international variant."));

        Assert.Equal(1, fetchCount);
        Assert.Equal(preview == true, result.IsPreviewOnly);
        var video = Assert.Single(result.VideoTracks);
        Assert.Equal("80", video.id);
        Assert.Equal("https://cdn.test/main-video.m4s", video.baseUrl);
        Assert.Equal("HEVC", video.codecs);
        var audio = Assert.Single(result.AudioTracks);
        Assert.Equal("30280", audio.id);
        Assert.Equal("https://cdn.test/main-audio.m4s", audio.baseUrl);
        Assert.Equal("M4A", audio.codecs);
        var background = Assert.Single(result.BackgroundAudioTracks);
        Assert.Equal("https://cdn.test/full-background.m4s", background.baseUrl);
        var role = Assert.Single(result.RoleAudioList);
        Assert.Equal("角色配音", role.title);
        Assert.Equal("配音演员", role.personName);
        Assert.Equal("https://cdn.test/full-role.m4s", Assert.Single(role.audio).baseUrl);
    }

    private static PlayViewReply CreatePlaybackReply(bool? preview)
    {
        var reply = new PlayViewReply
        {
            VideoInfo = new VideoInfo
            {
                Timelength = preview == true ? 360000UL : 1440000UL,
                StreamList =
                {
                    new StreamItem
                    {
                        StreamInfo = new StreamInfo { Quality = 80 },
                        DashVideo = new DashVideo
                        {
                            BaseUrl = "https://cdn.test/main-video.m4s",
                            Bandwidth = 2000000,
                            Codecid = 12
                        }
                    }
                },
                DashAudio = { CreateAudio(30280, "main-audio") }
            },
            PlayExtInfo = new PlayExtInfo
            {
                PlayDubbingInfo = new PlayDubbingInfo
                {
                    BackgroundAudio = new AudioMaterialProto
                    {
                        Audio = { CreateAudio(30280, "full-background") }
                    },
                    RoleAudioList =
                    {
                        new RoleAudioProto
                        {
                            AudioMaterialList =
                            {
                                new AudioMaterialProto
                                {
                                    AudioId = "role-1",
                                    Title = "角色配音",
                                    PersonName = "配音演员",
                                    Audio = { CreateAudio(30280, "full-role") }
                                }
                            }
                        }
                    }
                }
            }
        };
        if (preview.HasValue)
            reply.Business = new BusinessInfo { IsPreview = preview.Value };
        return reply;
    }

    private static DashItem CreateAudio(uint id, string name) => new()
    {
        Id = id,
        BaseUrl = $"https://cdn.test/{name}.m4s",
        Bandwidth = 192000
    };
}
