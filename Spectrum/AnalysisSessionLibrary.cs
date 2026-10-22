using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Spectrum.Dsp;

namespace Spectrum;

internal enum AnalysisLibraryKind { Session, Reference }
internal enum AnalysisLibrarySort { Recent, Newest, Name, Favorites }

internal sealed class AnalysisLibraryEntry
{
    internal AnalysisLibraryEntry(Guid id, string name, string notes, bool favorite, AnalysisLibraryKind kind,
        DateTime createdUtc, DateTime? openedUtc, string filePath, PayloadInfo info, string problem = "")
    {
        Id = id; Name = name; Notes = notes; IsFavorite = favorite; Kind = kind; CreatedUtc = createdUtc;
        LastOpenedUtc = openedUtc; FilePath = filePath; Problem = problem ?? "";
        SampleRate = info?.Plan.SampleRate ?? 0; Channels = info?.Channels ?? 0; BandCount = info?.Plan.Count ?? 0;
        DurationSeconds = info?.Duration ?? 0; SourceName = info?.Source ?? ""; Channel = info?.Channel ?? "";
        Response = info?.Response ?? ""; CapturedUtc = info?.CapturedUtc;
        SpectrumPower = Array.AsReadOnly(info == null ? Array.Empty<double>() : (double[])info.Power.Clone());
    }
    public Guid Id { get; }
    public string Name { get; }
    public string Notes { get; }
    public bool IsFavorite { get; }
    public AnalysisLibraryKind Kind { get; }
    public DateTime CreatedUtc { get; }
    public DateTime? LastOpenedUtc { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BandCount { get; }
    public double DurationSeconds { get; }
    public string SourceName { get; }
    public string Channel { get; }
    public string Response { get; }
    public DateTime? CapturedUtc { get; }
    public IReadOnlyList<double> SpectrumPower { get; }
    public string Problem { get; }
    public bool IsAvailable => string.IsNullOrEmpty(Problem);
    internal string FilePath { get; }
    public override string ToString() => (IsFavorite ? "★ " : "") + Name;
}

internal sealed class PayloadInfo
{
    internal BandPlan Plan;
    internal double[] Power;
    internal int Channels;
    internal double Duration;
    internal string Source, Channel, Response;
    internal DateTime? CapturedUtc;
}

internal sealed class AnalysisLibraryNavigationRequest
{
    internal AnalysisLibraryNavigationRequest(string workspace, AnalysisLibraryEntry entry)
    { Workspace = workspace; Entry = entry; }
    public string Workspace { get; }
    public AnalysisLibraryEntry Entry { get; }
}

// Index reads are passive. Files/directories are created only by explicit save/import/edit actions.
// Payloads are canonical version-one measurement XML, never original audio or decoded PCM.
internal sealed class AnalysisSessionLibrary
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, AnalysisLibraryEntry> _entries = new();
    private bool _invalidIndex;
    private string _revision;
    private const long MaximumPayloadBytes = 64L * 1024 * 1024;
    private const int MaximumEntries = 2000;
    public AnalysisSessionLibrary(string directory = null)
    {
        RootDirectory = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spectrum", "Library"));
        Refresh();
    }
    public string RootDirectory { get; }
    internal string IndexPath => Path.Combine(RootDirectory, "library.xml");
    public string LastError { get; private set; } = "";
    public event Action Changed;

    public IReadOnlyList<AnalysisLibraryEntry> Query(string search = "", AnalysisLibrarySort sort = AnalysisLibrarySort.Recent, bool favoritesOnly = false)
    {
        lock (_sync)
        {
            var query = _entries.Values.Where(e => !favoritesOnly || e.IsFavorite);
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(e => (e.Name + " " + e.Notes + " " + e.SourceName + " " + e.Channel + " " + e.Kind)
                    .IndexOf(search.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0);
            query = sort switch
            {
                AnalysisLibrarySort.Name => query.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Id),
                AnalysisLibrarySort.Newest => query.OrderByDescending(e => e.CreatedUtc).ThenBy(e => e.Id),
                AnalysisLibrarySort.Favorites => query.OrderByDescending(e => e.IsFavorite).ThenByDescending(e => e.LastOpenedUtc ?? e.CreatedUtc).ThenBy(e => e.Id),
                _ => query.OrderByDescending(e => e.LastOpenedUtc ?? e.CreatedUtc).ThenBy(e => e.Id)
            };
            return query.ToArray();
        }
    }

    public void Refresh()
    {
        lock (_sync)
        {
            _entries.Clear(); LastError = ""; _invalidIndex = false; _revision = null;
            if (File.Exists(IndexPath))
            {
                try
                {
                    var xml = ReadXml(IndexPath, 4 * 1024 * 1024);
                    if (xml.Name != "spectrum-library" || (int?)xml.Attribute("version") != 1)
                        throw new InvalidDataException("Unsupported library index version.");
                    _revision = (string)xml.Attribute("revision");
                    if (_revision != null && !Guid.TryParse(_revision, out _)) throw new InvalidDataException("Invalid library index revision.");
                    var entries = xml.Elements("entry").Take(MaximumEntries + 1).ToArray();
                    if (entries.Length > MaximumEntries) throw new InvalidDataException("The library index contains too many entries.");
                    foreach (var row in entries)
                    {
                        if (!Guid.TryParse((string)row.Attribute("id"), out var id) || _entries.ContainsKey(id) ||
                            !Enum.TryParse((string)row.Attribute("kind"), out AnalysisLibraryKind kind) || !Enum.IsDefined(typeof(AnalysisLibraryKind), kind))
                            throw new InvalidDataException("The library index contains an invalid entry identity.");
                        var name = Name((string)row.Attribute("name")); var notes = Notes((string)row.Element("notes"));
                        var created = Date((string)row.Attribute("createdUtc")) ?? throw new InvalidDataException("Missing library creation time.");
                        var opened = Date((string)row.Attribute("openedUtc")); var favorite = (bool?)row.Attribute("favorite") ?? false;
                        _entries.Add(id, Entry(id, name, notes, favorite, kind, created, opened));
                    }
                }
                catch (Exception exception) when (IsFileError(exception))
                {
                    _entries.Clear(); _invalidIndex = true;
                    LastError = "The library index could not be read. Existing saved files were preserved. " + exception.Message;
                }
            }
        }
        Changed?.Invoke();
    }

    public AnalysisLibraryEntry SaveCurrent(AnalysisWorkspace workspace, string name, string notes = "", bool referenceOnly = false)
    {
        if (workspace == null) throw new ArgumentNullException(nameof(workspace));
        if (referenceOnly && workspace.ReferencePower == null) throw new InvalidOperationException("Capture a reference before saving it to the library.");
        return Add(referenceOnly ? AnalysisLibraryKind.Reference : AnalysisLibraryKind.Session, Name(name), Notes(notes),
            path => { if (referenceOnly) workspace.SaveWorkspace(path); else workspace.SaveSession(path); });
    }

    public AnalysisLibraryEntry Import(string path, string name = null, string notes = "")
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a saved session or reference workspace.", nameof(path));
        path = Path.GetFullPath(path);
        var xml = ReadXml(path, MaximumPayloadBytes);
        var workspace = new AnalysisWorkspace(); AnalysisLibraryKind kind;
        if (xml.Name == "spectrum-session") { workspace.LoadSession(path); kind = AnalysisLibraryKind.Session; }
        else if (xml.Name == "spectrum-workspace")
        {
            workspace.LoadWorkspace(path); kind = AnalysisLibraryKind.Reference;
            if (workspace.ReferencePower == null) throw new InvalidDataException("The workspace has no captured reference to import.");
        }
        else throw new InvalidDataException("Choose a Spectrum session or a workspace containing a reference.");
        var importedName = workspace.WorkspaceName?.Trim();
        if (string.IsNullOrEmpty(importedName)) importedName = Path.GetFileNameWithoutExtension(path);
        if (importedName.Length > 160) importedName = importedName.Substring(0, 160);
        return Add(kind, Name(name ?? importedName), Notes(notes),
            destination => { if (kind == AnalysisLibraryKind.Session) workspace.SaveSession(destination); else workspace.SaveWorkspace(destination); });
    }

    public AnalysisLibraryEntry UpdateMetadata(Guid id, string name, string notes, bool favorite)
    {
        AnalysisLibraryEntry result;
        lock (_sync)
        {
            Writable(); var previous = Get(id);
            result = Entry(id, Name(name), Notes(notes), favorite, previous.Kind, previous.CreatedUtc, previous.LastOpenedUtc);
            Commit(_entries.Values.Where(e => e.Id != id).Concat(new[] { result })); _entries[id] = result;
        }
        Changed?.Invoke(); return result;
    }

    // Remove only unlinks metadata. The GUID payload is deliberately preserved for recovery.
    public void Remove(Guid id)
    {
        lock (_sync) { Writable(); Get(id); Commit(_entries.Values.Where(e => e.Id != id)); _entries.Remove(id); }
        Changed?.Invoke();
    }

    public void OpenSession(Guid id, AnalysisWorkspace workspace)
    {
        if (workspace == null) throw new ArgumentNullException(nameof(workspace));
        AnalysisLibraryEntry entry; lock (_sync) entry = Get(id);
        if (entry.Kind != AnalysisLibraryKind.Session) throw new InvalidOperationException("This entry is a reference. Use Compare to apply it to the current analysis.");
        ReadPayload(entry.FilePath, entry.Kind); // Validate before changing the supplied workspace.
        workspace.LoadSession(entry.FilePath); MarkOpened(id);
    }

    public void ApplyReference(Guid id, AnalysisWorkspace workspace)
    {
        if (workspace == null) throw new ArgumentNullException(nameof(workspace));
        AnalysisLibraryEntry entry; lock (_sync) entry = Get(id);
        var info = ReadPayload(entry.FilePath, entry.Kind);
        workspace.ApplyStoredReference(info.Plan, info.Power, entry.Name, info.CapturedUtc, info.Channel, info.Response);
        MarkOpened(id);
    }

    public void Export(Guid id, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Choose an export path.", nameof(destination));
        AnalysisLibraryEntry entry; lock (_sync) entry = Get(id);
        ReadPayload(entry.FilePath, entry.Kind); destination = Path.GetFullPath(destination);
        if (destination.StartsWith(RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an export path outside the library storage folder.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(entry.FilePath, temporary); AtomicMove(temporary, destination); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private AnalysisLibraryEntry Add(AnalysisLibraryKind kind, string name, string notes, Action<string> write)
    {
        AnalysisLibraryEntry result;
        lock (_sync)
        {
            Writable(); if (_entries.Count >= MaximumEntries) throw new InvalidOperationException("The library is full. Remove an entry before adding another.");
            Directory.CreateDirectory(RootDirectory); var id = Guid.NewGuid(); var destination = PayloadPath(id, kind); var temporary = destination + ".tmp";
            try
            {
                write(temporary); ReadPayload(temporary, kind); File.Move(temporary, destination);
                result = Entry(id, name, notes, false, kind, DateTime.UtcNow, null);
                Commit(_entries.Values.Concat(new[] { result })); _entries.Add(id, result); LastError = "";
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Changed?.Invoke(); return result;
    }
    private void MarkOpened(Guid id)
    {
        lock (_sync)
        {
            try
            {
                Writable(); var old = Get(id); var recent = Entry(id, old.Name, old.Notes, old.IsFavorite, old.Kind, old.CreatedUtc, DateTime.UtcNow);
                Commit(_entries.Values.Where(e => e.Id != id).Concat(new[] { recent })); _entries[id] = recent; LastError = "";
            }
            catch (Exception exception) when (IsFileError(exception)) { LastError = "The analysis opened, but its recent-history entry could not be updated. " + exception.Message; }
        }
        Changed?.Invoke();
    }
    private AnalysisLibraryEntry Entry(Guid id, string name, string notes, bool favorite, AnalysisLibraryKind kind, DateTime created, DateTime? opened)
    {
        var path = PayloadPath(id, kind);
        try { return new AnalysisLibraryEntry(id, name, notes, favorite, kind, created, opened, path, ReadPayload(path, kind)); }
        catch (Exception exception) when (IsFileError(exception)) { return new AnalysisLibraryEntry(id, name, notes, favorite, kind, created, opened, path, null, exception.Message); }
    }
    private AnalysisLibraryEntry Get(Guid id) => _entries.TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException("The library entry is no longer available. Refresh the list.");
    private void Writable() { if (_invalidIndex) throw new InvalidDataException(LastError + " Restore the index and refresh before writing."); }
    private string PayloadPath(Guid id, AnalysisLibraryKind kind) => Path.Combine(RootDirectory, id.ToString("N") + (kind == AnalysisLibraryKind.Session ? ".spectrum-session.xml" : ".spectrum-workspace.xml"));
    private void Commit(IEnumerable<AnalysisLibraryEntry> entries)
    {
        Directory.CreateDirectory(RootDirectory);
        using var transaction = new FileStream(Path.Combine(RootDirectory, "library.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (File.Exists(IndexPath))
        {
            var current = ReadXml(IndexPath, 4 * 1024 * 1024);
            if (current.Name != "spectrum-library" || (int?)current.Attribute("version") != 1 || (string)current.Attribute("revision") != _revision)
                throw new InvalidOperationException("The library changed in another window. Refresh before saving changes.");
        }
        else if (_revision != null) throw new IOException("The library index is missing. Restore it or refresh before saving changes.");
        var revision = Guid.NewGuid().ToString("D");
        var xml = new XElement("spectrum-library", new XAttribute("version", 1), new XAttribute("revision", revision), entries.OrderBy(e => e.Id).Select(e =>
            new XElement("entry", new XAttribute("id", e.Id.ToString("D")), new XAttribute("kind", e.Kind), new XAttribute("name", e.Name),
                new XAttribute("favorite", e.IsFavorite), new XAttribute("createdUtc", e.CreatedUtc.ToString("o", CultureInfo.InvariantCulture)),
                e.LastOpenedUtc.HasValue ? new XAttribute("openedUtc", e.LastOpenedUtc.Value.ToString("o", CultureInfo.InvariantCulture)) : null,
                new XElement("notes", e.Notes))));
        if (Encoding.UTF8.GetByteCount(xml.ToString()) + 128 > 4 * 1024 * 1024)
            throw new InvalidOperationException("The library index is full. Shorten notes or remove listings before saving more entries.");
        var temporary = Path.Combine(RootDirectory, "library." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false })) xml.Save(writer); stream.Flush(true); }
            AtomicMove(temporary, IndexPath); _revision = revision; LastError = "";
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void AtomicMove(string source, string destination)
    { if (File.Exists(destination)) File.Replace(source, destination, null); else File.Move(source, destination); }
    private static string Name(string value)
    { value = (value ?? "").Trim(); if (value.Length == 0 || value.Length > 160) throw new ArgumentException("Use a library name between 1 and 160 characters."); return value; }
    private static string Notes(string value)
    { value ??= ""; if (value.Length > 4000) throw new ArgumentException("Keep notes within 4000 characters."); return value; }
    private static DateTime? Date(string value)
        => string.IsNullOrEmpty(value) ? null : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result.ToUniversalTime() : throw new InvalidDataException("Invalid library timestamp.");
    private static bool IsFileError(Exception error) => error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is XmlException || error is ArgumentException || error is InvalidOperationException || error is FormatException || error is OverflowException;
    private static XElement ReadXml(string path, long maximum)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The saved library file is missing. Import it again or remove its library listing.", path);
        if (new FileInfo(path).Length > maximum) throw new InvalidDataException("The saved file exceeds the library size limit.");
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximum });
        return XElement.Load(reader);
    }
    private static PayloadInfo ReadPayload(string path, AnalysisLibraryKind kind)
    {
        var xml = ReadXml(path, MaximumPayloadBytes); var workspace = new AnalysisWorkspace();
        if (kind == AnalysisLibraryKind.Session)
        {
            workspace.LoadSession(path);
            return new PayloadInfo { Plan = workspace.Latest.Plan, Power = (double[])workspace.DisplayPower.Clone(), Channels = workspace.Latest.Channels,
                Duration = workspace.Measurements?.DurationSeconds ?? 0, Source = workspace.SourceName, CapturedUtc = workspace.Latest.CapturedUtc,
                Channel = workspace.Settings.Channel, Response = workspace.Settings.Response };
        }
        workspace.LoadWorkspace(path);
        if (workspace.ReferencePower == null) throw new InvalidDataException("The saved workspace has no reference spectrum.");
        var reference = xml.Element("reference");
        var edges = reference.Element("edges").Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return new PayloadInfo { Plan = BandPlan.CreateFromEdges(edges, (int)reference.Attribute("rate")), Power = (double[])workspace.ReferencePower.Clone(),
            Channels = 0, Duration = 0, Source = workspace.ReferenceName, CapturedUtc = workspace.ReferenceCapturedUtc,
            Channel = workspace.Settings.Channel, Response = workspace.Settings.Response };
    }
}
