using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Purpose", "Diagnostic")]
[Trait("Lane", "Manual")]
[Trait("ManualTask", "LiveMountProbe")]
public sealed class BookPanelSlotProbeTests
{
    private static readonly uint[] Children =
    {
        BookPanelController.TitleTextId,
        BookPanelController.PageTextId,
        BookPanelController.PreviousButtonId,
        BookPanelController.NextButtonId,
        BookPanelController.PageMenuId,
        BookPanelController.PageNumberTextId,
    };

    [Fact]
    public void ProbeBookPanelSlot()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        ElementInfo? slot = LayoutImporter.ImportInfos(
            dats,
            BookPanelController.HostLayoutId,
            BookPanelController.SlotElementId);
        Assert.NotNull(slot);

        Console.WriteLine(
            $"[book] slot 0x{slot!.Id:X8} type={slot.Type} "
            + $"({slot.X},{slot.Y} {slot.Width}x{slot.Height}) "
            + $"children={slot.Children.Count}");
        Dump(slot, 1);

        Console.WriteLine("--- named children ---");
        foreach (uint id in Children)
        {
            ElementInfo? info = Find(slot, id);
            if (info is null)
            {
                Console.WriteLine($"[book] 0x{id:X8} MISSING");
                continue;
            }

            Console.WriteLine(
                $"[book] 0x{id:X8} type={info.Type} "
                + $"({info.X},{info.Y} {info.Width}x{info.Height}) "
                + $"hj={info.HJustify} vj={info.VJustify} "
                + $"maxW={info.MaxWidth} maxH={info.MaxHeight} "
                + $"scrollbar=0x{info.ScrollbarElementId:X8} "
                + $"templates={info.TemplateList.Count} "
                + $"children={info.Children.Count} "
                + $"readOrder={info.ReadOrder} z={info.ZLevel}");

            foreach (uint property in new uint[]
                { 0x16u, 0x1Fu, 0x20u, 0x27u, 0x2Eu, 0x57u })
            {
                if (info.TryGetEffectiveBool(property, out bool flag))
                    Console.WriteLine($"    bool 0x{property:X2} = {flag}");
                else if (info.TryGetEffectiveInteger(property, out int number))
                    Console.WriteLine($"    int  0x{property:X2} = {number}");
            }

            foreach (UiTemplateListEntry template in info.TemplateList)
            {
                Console.WriteLine(
                    $"    template layout=0x{template.TemplateLayoutId:X8} "
                    + $"element=0x{template.TemplateElementId:X8}");
            }
        }

        Console.WriteLine("--- built widgets ---");
        ImportedLayout built = LayoutImporter.Build(slot, _ => (0u, 0, 0), null, null);
        foreach (uint id in Children)
        {
            UiElement? element = built.FindElement(id);
            Console.WriteLine(
                $"[book] 0x{id:X8} -> {element?.GetType().Name ?? "NULL"} "
                + $"({element?.Width}x{element?.Height})");
        }

        Console.WriteLine("--- menu art ---");
        ElementInfo menuInfo = Find(slot, BookPanelController.PageMenuId)!;
        DumpMedia(menuInfo, 0);

        Console.WriteLine("--- page text wrap ---");
        if (built.FindElement(BookPanelController.PageTextId) is UiField field)
        {
            const string sample =
                "The first paragraph of a letter from home runs on for a good "
                + "while so that it has to be broken across several lines by the "
                + "reader rather than by the writer.\n\n"
                + "The second paragraph is just as long again, and in the real "
                + "client it goes on below the first one without any gap in the "
                + "page and without anything covering it up.";
            field.SetText(sample);
            Console.WriteLine(
                $"[book] field {field.Width}x{field.Height} "
                + $"editable={field.Editable} maxChars={field.MaxCharacters} "
                + $"textLen={field.Text.Length}");
            foreach (string name in new[] { "WordWrap", "MultiLine", "Wrap" })
            {
                var property = typeof(UiField).GetProperty(name);
                if (property is not null)
                {
                    Console.WriteLine(
                        $"    {name} = {property.GetValue(field)}");
                }
            }
        }
        else
        {
            Console.WriteLine("[book] page text did not build as a field");
        }
    }

    private static void DumpMedia(ElementInfo info, int depth)
    {
        string pad = new string(' ', depth * 2);
        foreach (var (state, media) in info.StateMedia)
        {
            Console.WriteLine(
                $"{pad}[book] 0x{info.Id:X8} state=\"{state}\" "
                + $"file=0x{media.File:X8} draw={media.DrawMode}");
        }

        if (info.FontColor is { } color)
            Console.WriteLine($"{pad}[book] 0x{info.Id:X8} fontColor={color}");
        Console.WriteLine($"{pad}[book] 0x{info.Id:X8} fontDid=0x{info.FontDid:X8}");

        foreach (ElementInfo child in info.Children)
            DumpMedia(child, depth + 1);
    }

    private static void Dump(ElementInfo info, int depth)
    {
        foreach (ElementInfo child in info.Children)
        {
            Console.WriteLine(
                new string(' ', depth * 2)
                + $"0x{child.Id:X8} type={child.Type} "
                + $"({child.X},{child.Y} {child.Width}x{child.Height})");
            Dump(child, depth + 1);
        }
    }

    private static ElementInfo? Find(ElementInfo info, uint id)
    {
        if (info.Id == id) return info;
        foreach (ElementInfo child in info.Children)
        {
            if (Find(child, id) is { } found) return found;
        }

        return null;
    }
}
