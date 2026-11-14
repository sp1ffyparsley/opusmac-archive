using System.Xml;
using System.Xml.Linq;

namespace OpusMac.Core.Persistence;

public enum ListerStyle
{
    Commander, Explorer, Single, Images, Filmstrip, DualHorizontal, DualVertical
}

public static class ListerStyleImport
{
    public static async Task<ListerStyle> ReadAsync(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 32 * 1024
        });
        var root = (await XDocument.LoadAsync(reader, LoadOptions.None, CancellationToken.None)).Root;
        if (root?.Name != "style" || (string?)root.Attribute("hidden") != "no" ||
            root.Attributes().Any(attribute => attribute.Name != "hidden") ||
            root.Elements().Take(2).ToArray() is not [{ Name.LocalName: "layout" } layout] ||
            layout.Name != "layout" || layout.HasElements ||
            layout.Attributes().Any(attribute => attribute.Name != "flags" && attribute.Name != "view_mode") ||
            !string.IsNullOrWhiteSpace(layout.Value))
            throw new InvalidDataException("This Opus style contains unsupported layout settings.");

        return ((string?)layout.Attribute("flags"), (string?)layout.Attribute("view_mode")) switch
        {
            ("0x600009", null) => ListerStyle.Commander,
            ("0x600003", null) => ListerStyle.Explorer,
            ("0x600001", null) => ListerStyle.Single,
            ("0x600007", "thumbnails,thumbnails") => ListerStyle.Images,
            ("0x4a00045", "thumbnails,thumbnails") => ListerStyle.Filmstrip,
            ("0x60402b", null) => ListerStyle.DualHorizontal,
            ("0x60400b", null) => ListerStyle.DualVertical,
            _ => throw new InvalidDataException("This Opus style is outside the supported Commander, Explorer, Single, Images, Filmstrip, Dual Horizontal and Dual Vertical layouts.")
        };
    }
}
