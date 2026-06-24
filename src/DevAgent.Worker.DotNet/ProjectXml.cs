namespace DevAgent.Worker.DotNet;

using System.Text;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// Saves an edited project <see cref="XDocument"/> back to disk WITHOUT adding
/// noise to the diff. <see cref="XDocument.Save(string)"/> injects an XML
/// declaration and a UTF-8 BOM even when the original file had neither, which
/// shows up as spurious changes in the resulting pull request. This helper
/// preserves the original's declaration choice and never writes a BOM.
/// </summary>
internal static class ProjectXml
{
    public static void Save(XDocument doc, string path)
    {
        var settings = new XmlWriterSettings
        {
            // Only emit a declaration if the original file actually had one.
            OmitXmlDeclaration = doc.Declaration is null,
            // UTF-8 without a byte-order mark (the .csproj convention).
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using var writer = XmlWriter.Create(path, settings);
        doc.Save(writer);
    }
}
