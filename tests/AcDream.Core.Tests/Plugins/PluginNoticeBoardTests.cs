using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class PluginNoticeBoardTests
{
    [Fact]
    public void NoticesAreKeptInOrderUnderThePostingPluginAndTheOldestGoFirst()
    {
        var board = new PluginNoticeBoard();
        board.Post("acdream.mosstank", "route-stuck", PluginNoticeSeverity.Warning, "stuck", "{\"waypoint\":3}");
        board.Post("acdream.mosstank", "macro-idle", PluginNoticeSeverity.Info, "idle", null);

        IReadOnlyList<PluginNotice> all = board.Capture(0);

        Assert.Equal(["route-stuck", "macro-idle"], all.Select(notice => notice.Kind));
        Assert.Equal("acdream.mosstank", all[0].PluginId);
        Assert.Equal(PluginNoticeSeverity.Warning, all[0].Severity);
        Assert.Equal("{\"waypoint\":3}", all[0].DetailsJson);
        Assert.Equal(["macro-idle"], board.Capture(all[0].Sequence).Select(notice => notice.Kind));

        for (int index = 0; index < PluginNoticeBoard.Capacity; index++)
            board.Post("acdream.mosstank", "again", PluginNoticeSeverity.Info, "again", null);

        Assert.Equal(PluginNoticeBoard.Capacity, board.Capture(0).Count);
        Assert.DoesNotContain(board.Capture(0), notice => notice.Kind == "route-stuck");
    }

    [Fact]
    public void AHostWithoutANoticeBoardTakesNoticesAndKeepsNone()
    {
        IPluginNoticeBoard none = NoOpPluginNoticeBoard.Instance;

        none.Post("kind", PluginNoticeSeverity.Error, "message");

        Assert.Empty(none.Capture(0));
    }
}
