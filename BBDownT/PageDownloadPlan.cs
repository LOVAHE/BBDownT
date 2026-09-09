using System.Collections.Generic;
using System.Linq;
using BBDownT.Core.Entity;
using static BBDownT.Core.Entity.Entity;

namespace BBDownT;

// This is a selected-page plan, not an isolated configuration for concurrent tasks.
// Keep the original Page instances: the page downloader fills their chapter information.
internal sealed record PageDownloadPlan(List<Page> Pages, string SavePathFormat)
{
    internal static PageDownloadPlan Create(
        VInfo info, MyOption option, List<string>? selectedPages,
        string singlePageDefault, string multiPageDefault)
    {
        var pages = selectedPages is null
            ? info.PagesInfo
            : info.PagesInfo.Where(page => selectedPages.Contains(page.index.ToString())).ToList();

        // Naming depends on the original video, even when only one page is selected.
        var useMultiPagePattern = info.PagesInfo.Count > 1 || (info.IsBangumi && !info.IsBangumiEnd);
        var pattern = useMultiPagePattern ? option.MultiFilePattern : option.FilePattern;
        if (string.IsNullOrEmpty(pattern))
            pattern = useMultiPagePattern ? multiPageDefault : singlePageDefault;

        return new PageDownloadPlan(pages, pattern);
    }
}
