using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBDownT.Core.Util;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Tests;

public class IntlSubtitleSelectionTests
{
    private static List<Subtitle> SelectNonInteractive(IReadOnlyList<Subtitle> subtitles, MyOption option)
        => SubtitleSelection.Choose(subtitles, option, TextReader.Null, TextWriter.Null);

    [Theory]
    [InlineData("", "srt")]
    [InlineData("1-2", "ass")]
    [InlineData("3-4", "srt")]
    public void InteractiveSelection_ShowsAllFormatsAndUsesDefaultsOrExplicitChoices(
        string answer, string expectedFormat)
    {
        var resources = Episode("13287667");

        using var output = new StringWriter();
        var selected = SubtitleSelection.Choose(resources, IntlOption(interactive: true),
            new StringReader(answer + "\n"), output);

        Assert.Equal(4, resources.Count);
        Assert.Equal(2, selected.Count);
        Assert.All(selected, subtitle => Assert.Equal(expectedFormat, SubtitleSelection.OutputFormat(subtitle)));
        Assert.Contains("1. en | English | 来源未知 | ASS | 未选", output.ToString());
        Assert.Contains("2. th | Thai | 来源未知 | ASS | 未选", output.ToString());
        Assert.Contains("3. en | English | 来源未知 | SRT | 已选", output.ToString());
        Assert.Contains("4. th | Thai | 来源未知 | SRT | 已选", output.ToString());
        if (expectedFormat == "srt")
        {
            Assert.Equal(new[] { "en", "th" }, selected.Select(subtitle => subtitle.lan));
            Assert.All(selected, subtitle => Assert.EndsWith(".json", subtitle.url));
            Assert.All(selected, subtitle => Assert.EndsWith(".srt", subtitle.path));
        }
    }

    [Fact]
    public void OrdinaryInternationalDownload_SelectsOnlyPreferredSrtVariantsWithoutReadingInput()
    {
        var resources = Episode("13287667");

        var selected = SubtitleSelection.Choose(resources, IntlOption(), new NoInputReader(), TextWriter.Null);

        Assert.Equal(4, resources.Count);
        Assert.Equal(2, selected.Count);
        Assert.Equal(new[] { "en", "th" }, selected.Select(subtitle => subtitle.lan));
        Assert.All(selected, subtitle => Assert.EndsWith(".json", subtitle.url));
        Assert.All(selected, subtitle => Assert.EndsWith(".srt", subtitle.path));
    }

    [Fact]
    public void Mapper_LinksSameAssAcrossSourcesWithoutAssigningItsIdToJsonVariant()
    {
        var resources = Episode("13287667");
        var english = resources.Where(subtitle => subtitle.lan == "en").ToList();

        Assert.Equal(2, english.Count);
        Assert.Equal("1617629925714", english.Single(subtitle => SubtitleSelection.OutputFormat(subtitle) == "ass").id);
        Assert.Null(english.Single(subtitle => SubtitleSelection.OutputFormat(subtitle) == "srt").id);
        Assert.NotNull(english[0].FormatVariantGroup);
        Assert.Equal(english[0].FormatVariantGroup, english[1].FormatVariantGroup);
        Assert.NotEqual(english[0].FormatVariantGroup, resources.First(subtitle => subtitle.lan == "th").FormatVariantGroup);
        var json = JsonSerializer.Serialize(english[0], new JsonSerializerOptions { IncludeFields = true });
        Assert.DoesNotContain("FormatVariantGroup", json);
    }

    [Fact]
    public void AutoFormat_UsesAssWhenNoSrtVariantIsAvailable()
    {
        var resources = Episode("13287667").Where(subtitle => SubtitleSelection.OutputFormat(subtitle) == "ass").ToList();

        var selected = SelectNonInteractive(resources, IntlOption());

        Assert.Equal(2, selected.Count);
        Assert.All(selected, subtitle => Assert.Equal("ass", SubtitleSelection.OutputFormat(subtitle)));
    }

    [Fact]
    public void IndependentSameLanguageTracks_AreNotCollapsedByInternationalAutoFormat()
    {
        var resources = SameLanguageTracks("13287667", distinguishLabels: true);

        var selected = SelectNonInteractive(resources, IntlOption());

        Assert.Equal(4, resources.Count);
        Assert.Equal(2, selected.Count);
        Assert.Equal(new[] { "English", "English commentary" }, selected.Select(subtitle => subtitle.lanDoc));
        Assert.All(selected, subtitle => Assert.Equal("srt", SubtitleSelection.OutputFormat(subtitle)));
    }

    [Fact]
    public void UngroupedSameLanguageTracks_AndDomesticAutoKeepTheirExistingSelections()
    {
        var ungrouped = new[] { Track("en", "srt", "one"), Track("en", "srt", "two") };
        foreach (var subtitle in ungrouped) subtitle.FormatVariantGroup = null;
        Assert.Equal(2, SelectNonInteractive(ungrouped, IntlOption()).Count);

        var resources = Episode("13287667");
        Assert.Equal(4, SelectNonInteractive(resources, new MyOption()).Count);
    }

    [Fact]
    public void VariantGroups_NeverCollapseDifferentLanguages()
    {
        var resources = new[] { Track("en", "srt", "shared"), Track("th", "srt", "shared") };

        Assert.Equal(new[] { "en", "th" }, SelectNonInteractive(resources, IntlOption()).Select(subtitle => subtitle.lan));
    }

    [Theory]
    [InlineData("exclude", "", "en", 1)]
    [InlineData("include", "ALL", "en,en,ai-en,ai-en", 4)]
    [InlineData("prefer-human", "1", "en", 1)]
    [InlineData("only", "all", "ai-en,ai-en", 2)]
    public void DefaultAndManualChoices_CombineWithExistingLanguageAndAiPolicies(string policy, string answer, string languages, int count)
    {
        var resources = new[]
        {
            Track("en", "ass", "human", type: 0), Track("en", "srt", "human", type: 0),
            Track("ai-en", "ass", "ai", type: 1), Track("ai-en", "srt", "ai", type: 1),
            Track("th", "srt", "thai", type: 0)
        };
        var option = IntlOption(interactive: true);
        option.SubtitleLanguage = "en";
        option.AiSubtitlePolicy = policy;

        var selected = SubtitleSelection.Choose(resources, option, new StringReader(answer + "\n"), TextWriter.Null);

        Assert.Equal(count, selected.Count);
        Assert.Equal(languages.Split(','), selected.Select(subtitle => subtitle.lan));
    }

    [Fact]
    public void ManualChoice_IsReusedAcrossEpisodeIdsPathsUrlsAndResponseOrder()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        var first = Episode("13287667");
        var chosen = Assert.Single(Choose(first, option, session, "1"));
        var next = Episode("13287745");
        next.Reverse();
        using var output = new StringWriter();

        var reused = Assert.Single(SubtitleSelection.Choose(next, option, new NoInputReader(), output, session));
        var retry = Assert.Single(SubtitleSelection.Choose(next, option, new NoInputReader(), TextWriter.Null, session));

        Assert.Equal("en", reused.lan);
        Assert.Equal("ass", SubtitleSelection.OutputFormat(reused));
        Assert.NotEqual(chosen.id, reused.id);
        Assert.NotEqual(chosen.path, reused.path);
        Assert.NotEqual(chosen.url, reused.url);
        Assert.Same(reused, retry);
        Assert.Contains("发现 4 条，符合筛选 1 条", output.ToString());
        Assert.DoesNotContain("th |", output.ToString());
    }

    [Fact]
    public void FirstTwoAssChoices_AreReusedWithoutReadingInputDespiteSrtDefaults()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        var first = Choose(Episode("13287667"), option, session, "1-2");
        var later = Episode("13287745");
        later.Reverse();

        var selected = SubtitleSelection.Choose(later, option, new NoInputReader(), TextWriter.Null, session);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, selected.Count);
        Assert.Equal(new[] { "en", "th" }, selected.Select(subtitle => subtitle.lan));
        Assert.All(selected, subtitle => Assert.Equal("ass", SubtitleSelection.OutputFormat(subtitle)));
        Assert.All(selected, subtitle => Assert.DoesNotContain(first, old => old.id == subtitle.id || old.path == subtitle.path));
    }

    [Fact]
    public void AllChoice_ExplicitlyKeepsAndReusesAllFourFormatVariants()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        Assert.Equal(4, Choose(Episode("13287667"), option, session, "ALL").Count);
        var later = Episode("13287745");
        later.Reverse();

        var selected = SubtitleSelection.Choose(later, option, new NoInputReader(), TextWriter.Null, session);

        Assert.Equal(4, selected.Count);
        Assert.Equal(2, selected.Count(subtitle => SubtitleSelection.OutputFormat(subtitle) == "ass"));
        Assert.Equal(2, selected.Count(subtitle => SubtitleSelection.OutputFormat(subtitle) == "srt"));
    }

    [Fact]
    public void NoneChoice_IsReusedForFollowingEpisodesAndRetriesWithoutReadingInput()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        Assert.Empty(Choose(Episode("13287667"), option, session, "NONE"));
        using var output = new StringWriter();

        Assert.Empty(SubtitleSelection.Choose(Episode("13287745"), option, new NoInputReader(), output, session));
        Assert.Empty(SubtitleSelection.Choose(Episode("13287745"), option, new NoInputReader(), TextWriter.Null, session));
        Assert.Contains("跳过字幕", output.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyOrUnmatchedFirstEpisode_DoesNotFreezeLaterSubtitleChoice(bool noResources)
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        option.SubtitleLanguage = "en";
        var first = noResources ? new List<Subtitle>() : Episode("13287667").Where(subtitle => subtitle.lan == "th").ToList();
        Assert.Empty(SubtitleSelection.Choose(first, option, new NoInputReader(), TextWriter.Null, session));

        Assert.Equal("en", Assert.Single(Choose(Episode("13287745"), option, session, "1")).lan);
        Assert.Equal("en", Assert.Single(SubtitleSelection.Choose(Episode("13287800"), option,
            new NoInputReader(), TextWriter.Null, session)).lan);
    }

    [Fact]
    public void InfoOnly_DisplaysFormatsButDoesNotRecordInteractiveChoice()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        option.OnlyShowInfo = true;
        using var output = new StringWriter();

        Assert.Equal(2, SubtitleSelection.Choose(Episode("13287667"), option, new NoInputReader(), output, session).Count);
        Assert.Contains("ASS", output.ToString());
        Assert.Contains("SRT", output.ToString());
        Assert.Contains("未选", output.ToString());
        option.OnlyShowInfo = false;
        Assert.Equal("th", Assert.Single(Choose(Episode("13287745"), option, session, "2")).lan);
    }

    [Fact]
    public void DefaultAutoChoice_ReusesLanguageAndSourceWhileFallingBackToAss()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        Assert.All(Choose(Episode("13287667"), option, session, ""), subtitle => Assert.Equal("srt", SubtitleSelection.OutputFormat(subtitle)));
        var later = Episode("13287745").Where(subtitle => SubtitleSelection.OutputFormat(subtitle) == "ass").Reverse().ToList();

        var selected = SubtitleSelection.Choose(later, option, new NoInputReader(), TextWriter.Null, session);

        Assert.Equal(new[] { "en", "th" }, selected.Select(subtitle => subtitle.lan));
        Assert.All(selected, subtitle => Assert.Equal("ass", SubtitleSelection.OutputFormat(subtitle)));
    }

    [Fact]
    public void DefaultChoice_ReusesOnlyPreferredVariantsWhenBothFormatsRemainAvailable()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        Assert.Equal(2, Choose(Episode("13287667"), option, session, "").Count);
        var later = Episode("13287745");
        later.Reverse();

        var selected = SubtitleSelection.Choose(later, option, new NoInputReader(), TextWriter.Null, session);

        Assert.Equal(4, later.Count);
        Assert.Equal(2, selected.Count);
        Assert.All(selected, subtitle => Assert.Equal("srt", SubtitleSelection.OutputFormat(subtitle)));
    }

    [Theory]
    [InlineData("1", "ass", "srt")]
    [InlineData("2", "srt", "ass")]
    public void ManualFormatChoices_DoNotFallbackToAnotherFormat(string answer, string chosenFormat, string laterFormat)
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        option.SubtitleLanguage = "en";
        Assert.Equal(chosenFormat, SubtitleSelection.OutputFormat(Assert.Single(Choose(Episode("13287667"), option, session, answer))));
        var later = Episode("13287745").Where(subtitle => SubtitleSelection.OutputFormat(subtitle) == laterFormat).ToList();
        using var output = new StringWriter();

        Assert.Empty(SubtitleSelection.Choose(later, option, new NoInputReader(), output, session));
        Assert.Contains("格式", output.ToString());
    }

    [Fact]
    public void DistinguishableSameLanguageSources_ReuseTheChosenSourceAfterReordering()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        Assert.Equal("English", Assert.Single(Choose(SameLanguageTracks("first", true), option, session, "1")).lanDoc);
        var later = SameLanguageTracks("second", true);
        later.Reverse();

        var selected = Assert.Single(SubtitleSelection.Choose(later, option, new NoInputReader(), TextWriter.Null, session));

        Assert.Equal("English", selected.lanDoc);
        Assert.Contains("second", selected.path);
    }

    [Fact]
    public void IndistinguishablePartialSourceChoice_PromptsAgainInsteadOfExpandingSelection()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        Assert.Single(Choose(SameLanguageTracks("first", false), option, session, "1"));
        var later = SameLanguageTracks("second", false);
        later.Reverse();
        using var output = new StringWriter();

        var selected = Assert.Single(SubtitleSelection.Choose(later, option, new StringReader("2\n"), output, session));

        Assert.Same(SubtitleSelection.FilterCandidates(later, option)[1], selected);
        Assert.Contains("无法确定此前选择，请重新选择", output.ToString());
    }

    [Fact]
    public void MissingSourceLabel_UsesSingleSafeLanguageAndKindMatchWithoutReadingInput()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        var first = Track("en", "srt", "first", label: "English");
        Assert.Single(Choose([first], option, session, "1"));
        var later = Track("en", "srt", "second", label: null);

        Assert.Same(later, Assert.Single(SubtitleSelection.Choose([later], option, new NoInputReader(), TextWriter.Null, session)));
    }

    [Fact]
    public void MissingSourceLabel_WithMultipleLogicalTracksRequiresReselection()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        Assert.Single(Choose([Track("en", "srt", "first", label: "English")], option, session, "1"));
        var later = new[] { Track("en", "srt", "second-a", label: null), Track("en", "srt", "second-b", label: null) };
        using var output = new StringWriter();

        Assert.Same(later[1], Assert.Single(SubtitleSelection.Choose(later, option, new StringReader("2\n"), output, session)));
        Assert.Contains("来源标签缺失", output.ToString());
    }

    [Fact]
    public void ChangedSourceKind_DoesNotSubstituteAiForTheChosenHumanTrack()
    {
        var session = new SubtitleSelection.Session();
        var option = IntlOption(interactive: true);
        option.AiSubtitlePolicy = "include";
        Assert.Single(Choose([Track("en", "srt", "first", type: 0)], option, session, "1"));
        using var output = new StringWriter();

        Assert.Empty(SubtitleSelection.Choose([Track("en", "srt", "second", type: 1)], option,
            new NoInputReader(), output, session));
        Assert.Contains("未找到此前选择", output.ToString());
    }

    private static List<Subtitle> Choose(IReadOnlyList<Subtitle> subtitles, MyOption option,
        SubtitleSelection.Session session, string answer) => SubtitleSelection.Choose(subtitles, option,
        new StringReader(answer + "\n"), TextWriter.Null, session);

    private static MyOption IntlOption(bool interactive = false) => new()
    {
        UseIntlApi = true, Interactive = interactive
    };

    private static List<Subtitle> Episode(string episode)
    {
        var enId = episode == "13287667" ? 1617629925714L : 1617629930000L;
        var response = new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["subtitles"] = new JsonArray(Direct("en", "English", enId), Direct("th", "Thai", enId + 1)),
                ["video_subtitle"] = new JsonArray(Variants("en", "English", episode), Variants("th", "Thai", episode))
            }
        };
        return SubUtil.MergeSubtitleSources([SubUtil.ParseIntlSubtitleResponse(response.ToJsonString())], "intl_" + episode, "");

        JsonObject Direct(string language, string label, long id) => new()
        {
            ["lang_key"] = language, ["lang"] = label, ["subtitle_id"] = id,
            ["url"] = $"https://cdn.test/{episode}/{language}.ass"
        };
    }

    private static JsonObject Variants(string language, string label, string key) => new()
    {
        ["lang_key"] = language, ["lang"] = label,
        ["ass"] = new JsonObject { ["url"] = $"https://cdn.test/{key}/{language}.ass" },
        ["srt"] = new JsonObject { ["url"] = $"https://cdn.test/{key}/{language}.json" }
    };

    private static List<Subtitle> SameLanguageTracks(string episode, bool distinguishLabels)
    {
        var response = new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["video_subtitle"] = new JsonArray(Variants("en", "English", episode + "-one"),
                    Variants("en", distinguishLabels ? "English commentary" : "English", episode + "-two"))
            }
        };
        return SubUtil.MergeSubtitleSources([SubUtil.ParseIntlSubtitleResponse(response.ToJsonString())], "intl_" + episode, "");
    }

    private static Subtitle Track(string language, string format, string group, int? type = null, string? label = "English") => new()
    {
        lan = language, lanDoc = label, type = type, path = group + "." + format,
        url = $"https://cdn.test/{group}.{format}", FormatVariantGroup = group
    };

    [Fact]
    public async Task AppFallback_UsesTheSubtitlesOfTheRequestedEpisode()
    {
        static JsonObject Episode(string id, string language) => new()
        {
            ["id"] = id,
            ["subtitles"] = new JsonArray(new JsonObject { ["key"] = language, ["url"] = $"https://sub.test/{id}.json" })
        };
        var season = new JsonObject
        {
            ["code"] = 0,
            ["result"] = new JsonObject
            {
                ["modules"] = new JsonArray(
                    new JsonObject { ["data"] = new JsonObject { ["episodes"] = new JsonArray(Episode("10", "en"), Episode("20", "th")) } },
                    new JsonObject { ["data"] = new JsonObject { ["episodes"] = new JsonArray(Episode("30", "vi")) } })
            }
        };
        var responses = new Queue<string>(["{\"code\":0,\"data\":{}}", season.ToJsonString()]);

        var subtitles = await SubUtil.GetIntlSubtitlesAsync("intl_30", "", "30", _ => Task.FromResult(responses.Dequeue()));

        Assert.Equal("vi", Assert.Single(subtitles).lan);
    }

    private static string AppSeason(params string[] languages) => new JsonObject
    {
        ["code"] = 0,
        ["result"] = new JsonObject
        {
            ["modules"] = new JsonArray(new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["episodes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "30",
                        ["subtitles"] = new JsonArray(languages.Select(language => (JsonNode)new JsonObject
                        {
                            ["key"] = language, ["url"] = $"https://sub.test/30.{language}.json"
                        }).ToArray())
                    })
                }
            })
        }
    }.ToJsonString();

    [Fact]
    public async Task ListFailures_WithoutAnyAnswerForTheEpisodeEndTheTask()
    {
        var malformed = new InvalidDataException("bad subtitle list");
        var bothFailed = await Assert.ThrowsAsync<SubtitleUnavailableException>(() =>
            SubUtil.GetIntlSubtitlesAsync("intl_30", "", "30", _ => Task.FromException<string>(malformed)));
        Assert.Same(malformed, bothFailed.InnerException);

        var timeout = new HttpRequestException("timed out", new TimeoutException());
        var responses = new Queue<Func<Task<string>>>([
            () => Task.FromException<string>(timeout),
            () => Task.FromResult("{\"code\":0,\"result\":{\"modules\":[]}}")
        ]);
        Assert.Same(timeout, await Assert.ThrowsAsync<HttpRequestException>(() =>
            SubUtil.GetIntlSubtitlesAsync("intl_30", "", "30", _ => responses.Dequeue()())));

        var fallbackTimeout = new Queue<Func<Task<string>>>([
            () => Task.FromException<string>(malformed),
            () => Task.FromException<string>(timeout)
        ]);
        Assert.Same(timeout, await Assert.ThrowsAsync<HttpRequestException>(() =>
            SubUtil.GetIntlSubtitlesAsync("intl_30", "", "30", _ => fallbackTimeout.Dequeue()())));
    }

    [Fact]
    public async Task FallbackFailure_AfterAnEmptyPrimaryListMeansNoSubtitles()
    {
        var responses = new Queue<Func<Task<string>>>([
            () => Task.FromResult("{\"code\":0,\"data\":{}}"),
            () => Task.FromException<string>(new HttpRequestException("bad gateway", null, System.Net.HttpStatusCode.BadGateway))
        ]);

        Assert.Empty(await SubUtil.GetIntlSubtitlesAsync("intl_30", "", "30", _ => responses.Dequeue()()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrimaryFailure_UsesTheFallbackAnswerForTheEpisode(bool hasSubtitles)
    {
        string[] languages = hasSubtitles ? ["vi"] : [];
        var responses = new Queue<Func<Task<string>>>([
            () => Task.FromException<string>(new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset))),
            () => Task.FromResult(AppSeason(languages))
        ]);

        var subtitles = await SubUtil.GetIntlSubtitlesAsync("intl_30", "", "30", _ => responses.Dequeue()());

        Assert.Equal(languages, subtitles.Select(subtitle => subtitle.lan));
        Assert.All(subtitles, subtitle => Assert.EndsWith(".srt", subtitle.path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task MissingEpisodeId_DoesNotRequestInternationalSubtitleEndpoints(string episodeId)
    {
        var subtitles = await SubUtil.GetIntlSubtitlesAsync("117403434486720", "42651419955", episodeId,
            _ => throw new Xunit.Sdk.XunitException("An episode-only endpoint must not receive an empty ID"));

        Assert.Empty(subtitles);
    }

    [Theory]
    [InlineData("{\"code\":0}")]
    [InlineData("{\"code\":0,\"data\":null}")]
    [InlineData("{\"code\":0,\"data\":[]}")]
    [InlineData("{\"code\":0,\"data\":\"invalid\"}")]
    public void InvalidPrimaryPayload_ReportsInvalidData(string response)
    {
        var error = Assert.Throws<InvalidDataException>(() => SubUtil.ParseIntlSubtitleResponse(response));

        Assert.Equal("国际站字幕接口未返回有效数据", error.Message);
    }

    [Fact]
    public async Task NullPrimaryPayload_UsesTheFallbackSubtitlesForTheRequestedEpisode()
    {
        var requests = new List<Uri>();
        var responses = new Queue<string>(["{\"code\":0,\"data\":null}", AppSeason("vi")]);

        var subtitles = await SubUtil.GetIntlSubtitlesAsync("intl_30", "", "30", url =>
        {
            requests.Add(new Uri(url));
            return Task.FromResult(responses.Dequeue());
        });

        Assert.Equal(2, requests.Count);
        Assert.Contains("episode_id=30", requests[0].Query);
        Assert.Contains("ep_id=30", requests[1].Query);
        Assert.Equal("vi", Assert.Single(subtitles).lan);
        Assert.EndsWith(".srt", subtitles[0].path);
    }

    [Fact]
    public async Task NullPrimaryPayload_WithoutAFallbackAnswerPreservesTheFailure()
    {
        var responses = new Queue<string>([
            "{\"code\":0,\"data\":null}",
            "{\"code\":0,\"result\":{\"modules\":[]}}"
        ]);

        var error = await Assert.ThrowsAsync<SubtitleUnavailableException>(() =>
            SubUtil.GetIntlSubtitlesAsync("intl_30", "", "30", _ => Task.FromResult(responses.Dequeue())));

        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Contains("国际站字幕接口未返回有效数据", error.Message);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task LegacyAidWithAnEpisodeId_StillUsesThePrimarySubtitleEndpoint()
    {
        var requests = new List<Uri>();
        var subtitles = await SubUtil.GetIntlSubtitlesAsync("123", "456", "30", url =>
        {
            requests.Add(new Uri(url));
            return Task.FromResult("{\"code\":0,\"data\":{\"subtitles\":[{\"lang_key\":\"en\",\"url\":\"https://sub.test/30.json\"}]}}");
        });

        Assert.Contains("episode_id=30", Assert.Single(requests).Query);
        Assert.Equal("en", Assert.Single(subtitles).lan);
    }

    private sealed class NoInputReader : TextReader
    {
        public override string? ReadLine() => throw new Xunit.Sdk.XunitException("A reused or noninteractive subtitle choice read input.");
    }
}
