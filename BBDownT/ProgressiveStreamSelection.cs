using System;
using System.IO;
using System.Threading.Tasks;
using BBDownT.Core;
using BBDownT.Core.Entity;

namespace BBDownT;

internal sealed class ProgressiveStreamSelection
{
    internal string RequestedQuality { get; private set; } = "0";

    internal async Task<ParsedResult> ChooseAsync(ParsedResult initial,
        Func<string, Task<ParsedResult>> fetch, TextReader input, TextWriter output)
    {
        for (var i = 0; i < initial.Dfns.Count; i++)
            output.WriteLine($"{i}.{Config.qualitys[initial.Dfns[i]]}");
        output.Write("请选择最想要的清晰度(输入序号): ");
        var index = Program.ParseSelectionIndex(input.ReadLine(), initial.Dfns.Count);
        RequestedQuality = initial.Dfns[index];
        var selected = await fetch(RequestedQuality);
        if (selected.Clips.Count == 0 || selected.VideoTracks.Count == 0)
            throw new InvalidDataException("所选清晰度未返回可下载的合并流，请重新选择清晰度或切换接口。");
        return selected;
    }
}
