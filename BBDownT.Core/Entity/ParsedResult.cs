using static BBDownT.Core.Entity.Entity;

namespace BBDownT.Core.Entity;

public class ParsedResult
{
    public string WebJsonString { get; set; } = string.Empty;
    public List<Video> VideoTracks { get; set; } = new();
    public List<Audio> AudioTracks { get; set; } = new();
    public List<Audio> BackgroundAudioTracks { get; set; } = new();
    public List<AudioMaterialInfo> RoleAudioList { get; set; } = new();
    public List<ViewPoint> ExtraPoints { get; set; } = new();
    // ⬇⬇⬇⬇⬇ FOR FLV ⬇⬇⬇⬇⬇
    public List<string> Clips { get; set; } = new();
    public List<string> Dfns { get; set; } = new();
}
