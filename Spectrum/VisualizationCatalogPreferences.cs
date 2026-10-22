using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Spectrum;

internal sealed class VisualizationCatalogPreferences
{
    private readonly string _path;
    private readonly HashSet<string> _favorites = new(StringComparer.Ordinal);
    private readonly List<string> _recent = new();
    internal VisualizationCatalogPreferences(string path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spectrum", "views.xml");
        try
        {
            if (!File.Exists(_path)) return;
            using var reader = XmlReader.Create(_path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
            var xml = XElement.Load(reader);
            if (xml.Name != "visualization-catalog" || (string)xml.Attribute("version") != "1") throw new InvalidDataException("Unsupported view preferences format.");
            foreach (var item in xml.Elements("favorite")) if (Known(item.Value)) _favorites.Add(item.Value);
            foreach (var item in xml.Elements("recent")) if (Known(item.Value) && !_recent.Contains(item.Value) && _recent.Count < 12) _recent.Add(item.Value);
        }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is XmlException)
        { Error = "View preferences could not be read: " + error.Message; }
    }
    internal string Error { get; private set; }
    internal bool IsFavorite(string mode) => _favorites.Contains(mode);
    internal IReadOnlyList<string> Recent => _recent.ToArray();
    internal void ToggleFavorite(string mode)
    {
        if (!Known(mode)) throw new ArgumentException("Unknown visualization.", nameof(mode));
        if (!_favorites.Remove(mode)) _favorites.Add(mode);
        Save();
    }
    internal void RecordRecent(string mode)
    {
        if (!Known(mode)) return;
        if (_recent.Count > 0 && _recent[0] == mode) return;
        _recent.Remove(mode); _recent.Insert(0, mode);
        if (_recent.Count > 12) _recent.RemoveAt(12);
        Save();
    }
    private static bool Known(string mode) => FormAudioSpectrum.VisualModeNames.Contains(mode);
    private void Save()
    {
        // A malformed original stays available for repair; opening the application never overwrites it.
        if (Error != null) return;
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
            new XElement("visualization-catalog", new XAttribute("version", 1),
                _favorites.OrderBy(v => v, StringComparer.Ordinal).Select(v => new XElement("favorite", v)),
                _recent.Select(v => new XElement("recent", v))).Save(temporary);
            if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        { Error = "View preferences could not be saved: " + error.Message; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
