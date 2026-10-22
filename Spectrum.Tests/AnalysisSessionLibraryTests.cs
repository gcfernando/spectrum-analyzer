using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public sealed class AnalysisSessionLibraryTests
{
    internal static AnalysisWorkspace Measured(double level = -20)
    {
        var workspace = new AnalysisWorkspace { WorkspaceName = "Library measurement" };
        workspace.Settings.AveragingMilliseconds = 0; workspace.Settings.SmoothingBands = 0; workspace.UpdateSettings();
        workspace.Accept(AnalysisWorkspaceTests.Tone(level, frames: 64)); return workspace;
    }

    [Fact]
    public void ConstructingAndSearchingANewLibraryDoesNotCreateUserFiles()
    {
        using var folder = new LibraryFolder(); var path = Path.Combine(folder.Path, "not-created");
        var library = new AnalysisSessionLibrary(path);
        Assert.Empty(library.Query()); Assert.Empty(library.Query("anything", favoritesOnly: true));
        Assert.False(Directory.Exists(path)); Assert.Equal("", library.LastError);
    }

    [Fact]
    public void SavingAnalysisUsesGuidPayloadsAndNoPcmAndReopensExactMeasurements()
    {
        using var folder = new LibraryFolder(); var workspace = Measured(-100); var power = (double[])workspace.Latest.Power.Clone();
        var library = new AnalysisSessionLibrary(folder.Path); var entry = library.SaveCurrent(workspace, "Quiet tail", "Keep weak bands");
        Assert.Equal(entry.Id.ToString("N") + ".spectrum-session.xml", Path.GetFileName(entry.FilePath));
        Assert.Equal(AnalysisLibraryKind.Session, entry.Kind); Assert.Equal(83, entry.BandCount); Assert.Equal(48000, entry.SampleRate);
        Assert.InRange(10 * Math.Log10(entry.SpectrumPower[80]), -100.0001, -99.9999);
        var xml = XElement.Load(entry.FilePath); Assert.Equal("spectrum-session", xml.Name.LocalName);
        Assert.DoesNotContain(xml.Descendants(), element => element.Name.LocalName == "samples" || element.Name.LocalName == "pcm");
        var target = Measured(); library.OpenSession(entry.Id, target);
        Assert.True(target.IsSavedSession); Assert.Equal(0, target.Latest.FrameCount); Assert.Empty(target.Latest.Samples); Assert.Equal(power, target.Latest.Power);
        Assert.True(library.Query().Single().LastOpenedUtc.HasValue);
    }

    [Fact]
    public void NamesNotesFavoritesAndSearchSurviveAtomicIndexRoundTrip()
    {
        using var folder = new LibraryFolder(); var library = new AnalysisSessionLibrary(folder.Path);
        var first = library.SaveCurrent(Measured(), "First"); library.SaveCurrent(Measured(), "Second");
        library.UpdateMetadata(first.Id, "Vocal reference α", "Warm voice; keep below-floor tail", true);
        var restored = new AnalysisSessionLibrary(folder.Path); var favorite = Assert.Single(restored.Query("VOICE", favoritesOnly: true));
        Assert.Equal(first.Id, favorite.Id); Assert.Equal("Vocal reference α", favorite.Name); Assert.True(favorite.IsFavorite);
        Assert.Equal("Warm voice; keep below-floor tail", favorite.Notes);
        Assert.Equal(first.Id, restored.Query(sort: AnalysisLibrarySort.Favorites).First().Id);
        Assert.Equal("Second", restored.Query(sort: AnalysisLibrarySort.Name).First().Name);
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }

    [Fact]
    public void RecentActivityAndNewestSavedHaveDistinctOrdering()
    {
        using var folder = new LibraryFolder(); var library = new AnalysisSessionLibrary(folder.Path);
        var first = library.SaveCurrent(Measured(), "First"); Thread.Sleep(20); var second = library.SaveCurrent(Measured(), "Second");
        Thread.Sleep(20); library.OpenSession(first.Id, Measured());
        Assert.Equal(first.Id, library.Query(sort: AnalysisLibrarySort.Recent).First().Id);
        Assert.Equal(second.Id, library.Query(sort: AnalysisLibrarySort.Newest).First().Id);
    }

    [Fact]
    public void ImportNormalizesMeasurementsWithoutModifyingTheOriginalOrStoringExtraPcm()
    {
        using var folder = new LibraryFolder(); var source = Path.Combine(folder.Path, "source.xml");
        Measured().SaveSession(source); var xml = XElement.Load(source); xml.Add(new XElement("pcm", "This unknown extension must not enter the library")); xml.Save(source);
        var original = File.ReadAllBytes(source); var library = new AnalysisSessionLibrary(Path.Combine(folder.Path, "library"));
        var entry = library.Import(source, "Imported");
        Assert.Equal(original, File.ReadAllBytes(source)); Assert.NotEqual(source, entry.FilePath);
        Assert.Empty(XElement.Load(entry.FilePath).Elements("pcm")); Assert.Equal("Imported", entry.Name);
    }

    [Theory]
    [InlineData(0)] [InlineData(200)]
    public void ValidLegacyTitlesCanBeEmptyOrLongWithoutBlockingImport(int length)
    {
        using var folder = new LibraryFolder(); var source = Path.Combine(folder.Path, "legacy-session.xml");
        var workspace = Measured(); workspace.WorkspaceName = new string('a', length); workspace.SaveSession(source);
        var original = File.ReadAllBytes(source); var library = new AnalysisSessionLibrary(Path.Combine(folder.Path, "library"));
        var entry = library.Import(source); Assert.InRange(entry.Name.Length, 1, 160); Assert.Equal(original, File.ReadAllBytes(source));
        library.OpenSession(entry.Id, Measured());
    }

    [Fact]
    public void WorkspaceReferenceImportAndCurrentReferenceSaveKeepReferencePower()
    {
        using var folder = new LibraryFolder(); var workspace = Measured(-26); workspace.CaptureReference();
        var reference = (double[])workspace.ReferencePower.Clone(); var source = Path.Combine(folder.Path, "reference.xml"); workspace.SaveWorkspace(source);
        var library = new AnalysisSessionLibrary(Path.Combine(folder.Path, "library"));
        var imported = library.Import(source, "A reference"); var saved = library.SaveCurrent(workspace, "B reference", referenceOnly: true);
        Assert.Equal(AnalysisLibraryKind.Reference, imported.Kind); Assert.Equal(reference, imported.SpectrumPower.ToArray()); Assert.Equal(reference, saved.SpectrumPower.ToArray());
        var target = Measured(-32); var latest = target.Latest; var raw = (double[])target.Latest.Power.Clone(); var settings = target.Settings.Clone(); var history = target.History.Count;
        library.ApplyReference(imported.Id, target);
        Assert.Same(latest, target.Latest); Assert.Equal(raw, target.Latest.Power); Assert.Equal(history, target.History.Count);
        Assert.Equal(settings.FloorDb, target.Settings.FloorDb); Assert.Equal(settings.Channel, target.Settings.Channel); Assert.Equal(settings.Response, target.Settings.Response);
        Assert.Equal(reference, target.ReferencePower); Assert.Equal("A reference", target.ReferenceName); Assert.True(target.ReferenceVisible);
    }

    [Fact]
    public void SessionAsReferenceUsesItsSavedDisplayCurveAndDoesNotReplaceCurrentAnalysis()
    {
        using var folder = new LibraryFolder(); var library = new AnalysisSessionLibrary(folder.Path); var source = Measured(-20);
        var entry = library.SaveCurrent(source, "Measured A"); var target = Measured(-26); var frame = target.Latest;
        library.ApplyReference(entry.Id, target);
        Assert.Same(frame, target.Latest); Assert.Equal(source.DisplayPower, target.ReferencePower);
        Assert.InRange(10 * Math.Log10(target.DisplayPower[80] / target.ReferencePower[80]), -6.00001, -5.99999);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("channel")]
    [InlineData("response")]
    [InlineData("plan")]
    public void IncompatibleReferencesRejectBeforeMutatingCurrentMeasurementsOrReference(string condition)
    {
        using var folder = new LibraryFolder(); var library = new AnalysisSessionLibrary(folder.Path); var entry = library.SaveCurrent(Measured(), "A");
        var target = Measured(-26); target.CaptureReference();
        if (condition == "empty") target = new AnalysisWorkspace();
        else if (condition == "channel") { target.Settings.Channel = "Left"; target.UpdateSettings(); target.CaptureReference(); }
        else if (condition == "response") { target.Settings.Response = "Detailed"; target.UpdateSettings(); target.CaptureReference(); }
        else if (condition == "plan")
        {
            var plan = BandPlan.CreateLogarithmic(24, 30, 16000, 48000); var zero = new double[24];
            target.Accept(new AnalysisFrame(plan, 2, 64, 128, new float[128], zero, zero, zero, new object())); target.CaptureReference();
            Assert.Equal(24, target.Latest.Plan.Count);
        }
        var latest = target.Latest; var reference = target.ReferencePower == null ? null : (double[])target.ReferencePower.Clone();
        Assert.Throws<InvalidOperationException>(() => library.ApplyReference(entry.Id, target));
        Assert.Same(latest, target.Latest); Assert.Equal(reference, target.ReferencePower);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingOrMalformedPayloadRemainsBrowsableAndDoesNotCorruptTheSharedWorkspace(bool malformed)
    {
        using var folder = new LibraryFolder(); var library = new AnalysisSessionLibrary(folder.Path); var entry = library.SaveCurrent(Measured(), "Recoverable");
        var bytes = File.ReadAllBytes(entry.FilePath); if (malformed) File.WriteAllText(entry.FilePath, "<bad>"); else File.Delete(entry.FilePath);
        library.Refresh(); var broken = Assert.Single(library.Query()); Assert.False(broken.IsAvailable); Assert.False(string.IsNullOrWhiteSpace(broken.Problem));
        var target = Measured(-32); var frame = target.Latest; Assert.ThrowsAny<Exception>(() => library.OpenSession(entry.Id, target)); Assert.Same(frame, target.Latest);
        File.WriteAllBytes(entry.FilePath, bytes); library.Refresh(); Assert.True(Assert.Single(library.Query()).IsAvailable);
        library.OpenSession(entry.Id, target); Assert.True(target.IsSavedSession);
    }

    [Fact]
    public void UnknownIndexVersionPreservesAllFilesAndRejectsWritesUntilRestored()
    {
        using var folder = new LibraryFolder(); var library = new AnalysisSessionLibrary(folder.Path); var entry = library.SaveCurrent(Measured(), "Preserve");
        var original = File.ReadAllBytes(library.IndexPath); var xml = XElement.Load(library.IndexPath); xml.SetAttributeValue("version", 99); xml.Save(library.IndexPath);
        var badIndex = File.ReadAllBytes(library.IndexPath); var payload = File.ReadAllBytes(entry.FilePath);
        library.Refresh(); Assert.NotEmpty(library.LastError); Assert.Empty(library.Query());
        Assert.Throws<InvalidDataException>(() => library.SaveCurrent(Measured(), "Blocked"));
        Assert.Equal(badIndex, File.ReadAllBytes(library.IndexPath)); Assert.Equal(payload, File.ReadAllBytes(entry.FilePath));
        File.WriteAllBytes(library.IndexPath, original); library.Refresh(); Assert.Empty(library.LastError); Assert.Single(library.Query());
    }

    [Fact]
    public void FailedMetadataCommitKeepsThePreviousInMemoryEntryAndPayload()
    {
        using var folder = new LibraryFolder(); var library = new AnalysisSessionLibrary(folder.Path); var entry = library.SaveCurrent(Measured(), "Original");
        var index = File.ReadAllBytes(library.IndexPath); var payload = File.ReadAllBytes(entry.FilePath);
        File.Delete(library.IndexPath); Directory.CreateDirectory(library.IndexPath);
        Assert.ThrowsAny<IOException>(() => library.UpdateMetadata(entry.Id, "Uncommitted", "", true));
        Assert.Equal("Original", library.Query().Single().Name); Assert.False(library.Query().Single().IsFavorite); Assert.Equal(payload, File.ReadAllBytes(entry.FilePath));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
        Directory.Delete(library.IndexPath); File.WriteAllBytes(library.IndexPath, index); library.Refresh(); Assert.Equal("Original", library.Query().Single().Name);
    }

    [Fact]
    public void AStaleLibraryInstanceCannotOverwriteAnotherInstancesMetadata()
    {
        using var folder = new LibraryFolder(); var first = new AnalysisSessionLibrary(folder.Path); var entry = first.SaveCurrent(Measured(), "Original");
        var stale = new AnalysisSessionLibrary(folder.Path); first.UpdateMetadata(entry.Id, "Newest", "Keep this edit", true);
        Assert.Throws<InvalidOperationException>(() => stale.UpdateMetadata(entry.Id, "Stale", "", false));
        var refreshed = new AnalysisSessionLibrary(folder.Path); Assert.Equal("Newest", refreshed.Query().Single().Name); Assert.True(refreshed.Query().Single().IsFavorite);
        stale.Refresh(); stale.UpdateMetadata(entry.Id, "After refresh", "Preserved", true); Assert.Equal("After refresh", new AnalysisSessionLibrary(folder.Path).Query().Single().Name);
    }

    [Fact]
    public void ExportIsACopyAndRemovingTheListingPreservesItsPayloadAndOriginalImport()
    {
        using var folder = new LibraryFolder(); var source = Path.Combine(folder.Path, "original.xml"); Measured().SaveSession(source);
        var library = new AnalysisSessionLibrary(Path.Combine(folder.Path, "library")); var entry = library.Import(source);
        var original = File.ReadAllBytes(source); var payload = File.ReadAllBytes(entry.FilePath); var export = Path.Combine(folder.Path, "export.xml");
        library.Export(entry.Id, export); Assert.Equal(payload, File.ReadAllBytes(export));
        library.Remove(entry.Id); Assert.Empty(new AnalysisSessionLibrary(library.RootDirectory).Query());
        Assert.Equal(payload, File.ReadAllBytes(entry.FilePath)); Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void EmptyNamesOversizedNotesAndAWorkspaceWithoutReferenceRejectWithoutCreatingALibrary()
    {
        using var folder = new LibraryFolder(); var root = Path.Combine(folder.Path, "new-library"); var library = new AnalysisSessionLibrary(root);
        Assert.Throws<ArgumentException>(() => library.SaveCurrent(Measured(), " "));
        Assert.Throws<ArgumentException>(() => library.SaveCurrent(Measured(), "Name", new string('x', 4001)));
        Assert.Throws<InvalidOperationException>(() => library.SaveCurrent(Measured(), "Ref", referenceOnly: true));
        var source = Path.Combine(folder.Path, "workspace.xml"); Measured().SaveWorkspace(source);
        Assert.Throws<InvalidDataException>(() => library.Import(source)); Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void DockedBrowserSavesEditsSearchesOpensAndComparesThroughActualControls()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var folder = new LibraryFolder(); var workspace = Measured();
            using var form = new Form { ClientSize = new Size(1150, 760), ShowInTaskbar = false };
            using var control = new SessionLibraryControl(workspace, folder.Path); form.Controls.Add(control); form.Show(); ModernWorkbenchProbe.Pump();
            AnalysisLibraryNavigationRequest navigation = null; control.NavigationRequested += request => navigation = request;
            ModernWorkbenchProbe.Find<TextBox>(control, "libraryNewName").Text = "Shown reference";
            ModernWorkbenchProbe.Find<Button>(control, "librarySaveAnalysis").PerformClick(); ModernWorkbenchProbe.Pump();
            Assert.Single(control.Library.Query()); Assert.Single(ModernWorkbenchProbe.Find<ListBox>(control, "libraryEntries").Items.Cast<object>());
            ModernWorkbenchProbe.Find<TextBox>(control, "libraryNotes").Text = "Speech take for comparison";
            ModernWorkbenchProbe.Find<CheckBox>(control, "libraryFavorite").Checked = true;
            Assert.True(control.Library.Query().Single().IsFavorite); Assert.Contains("Speech", control.Library.Query().Single().Notes);
            ModernWorkbenchProbe.Find<TextBox>(control, "librarySearch").Text = "speech"; Assert.Single(ModernWorkbenchProbe.Find<ListBox>(control, "libraryEntries").Items.Cast<object>());
            var previous = workspace.Latest; ModernWorkbenchProbe.Find<Button>(control, "libraryCompare").PerformClick();
            Assert.Equal("Compare", navigation?.Workspace); Assert.Same(previous, workspace.Latest); Assert.NotNull(workspace.ReferencePower);
            ModernWorkbenchProbe.Find<Button>(control, "libraryOpen").PerformClick(); Assert.Equal("Analyze", navigation.Workspace); Assert.True(workspace.IsSavedSession);
            var entry = control.Library.Query().Single(); File.Delete(entry.FilePath); control.RefreshLibrary();
            Assert.True(ModernWorkbenchProbe.Find<Label>(control, "libraryError").Visible); Assert.False(ModernWorkbenchProbe.Find<Button>(control, "libraryOpen").Enabled);
            workspace.SaveSession(entry.FilePath); control.RefreshLibrary();
            Assert.False(ModernWorkbenchProbe.Find<Label>(control, "libraryError").Visible); Assert.True(ModernWorkbenchProbe.Find<Button>(control, "libraryOpen").Enabled);
            form.Close();
        });
    }

    [Theory]
    [InlineData(1200, 480)] [InlineData(900, 480)]
    public void ShortDockedBrowserScrollsSelectedDetailsToReachItsFileActions(int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var folder = new LibraryFolder(); var workspace = Measured();
            using var form = new Form { ClientSize = new Size(width, height), ShowInTaskbar = false };
            using var control = new SessionLibraryControl(workspace, folder.Path); form.Controls.Add(control); form.Show(); ModernWorkbenchProbe.Pump();
            ModernWorkbenchProbe.Find<Button>(control, "librarySaveAnalysis").PerformClick(); ModernWorkbenchProbe.Pump();
            var details = ModernWorkbenchProbe.Find<TableLayoutPanel>(control, "libraryEntryDetails");
            var export = ModernWorkbenchProbe.Find<Button>(control, "libraryExport");
            Assert.True(details.AutoScroll); details.ScrollControlIntoView(export); ModernWorkbenchProbe.Pump();
            var visibleBounds = details.RectangleToScreen(details.ClientRectangle); var exportBounds = export.RectangleToScreen(export.ClientRectangle);
            Assert.True(visibleBounds.Contains(exportBounds), "The actual Export button must be reachable inside the scrolled details viewport.");
            Assert.False(details.HorizontalScroll.Visible, "The details pane should need vertical scrolling only.");
            form.Close();
        });
    }

    [Theory]
    [InlineData(9f)] [InlineData(13.5f)]
    public void LibraryCommandLabelsFitAtTheActualMonitorDpiAndEnlargedFont(float fontSize)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            Assert.NotEqual(IntPtr.Zero, previousDpi);
            try
            {
                using var folder = new LibraryFolder(); using var font = new Font("Segoe UI", fontSize);
                using var form = new Form { ClientSize = new Size(1200, 720), ShowInTaskbar = false };
                using var control = new SessionLibraryControl(Measured(), folder.Path) { Font = font };
                form.Controls.Add(control); form.Show(); ModernWorkbenchProbe.Pump();
                ModernWorkbenchProbe.Find<Button>(control, "librarySaveAnalysis").PerformClick(); ModernWorkbenchProbe.Pump();
                Assert.Equal("Save analysis", ModernWorkbenchProbe.Find<Button>(control, "librarySaveAnalysis").Text);
                Assert.Equal("Save reference", ModernWorkbenchProbe.Find<Button>(control, "librarySaveReference").Text);
                AssertCommandTextFits(control);
                var search = ModernWorkbenchProbe.Find<TextBox>(control, "librarySearch");
                Assert.True(search.ClientSize.Width >= TextRenderer.MeasureText("Search names, notes or sources…", search.Font).Width,
                    "The search cue must fit at the actual monitor DPI=" + control.DeviceDpi + ".");
                var list = ModernWorkbenchProbe.Find<ListBox>(control, "libraryEntries");
                Assert.True(list.ItemHeight >= list.Font.Height * 3 + (int)Math.Ceiling(24 * control.DeviceDpi / 96d));
                form.Close();
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
        });
    }

    internal static void AssertCommandTextFits(SessionLibraryControl control)
    {
        foreach (var name in new[] { "librarySaveAnalysis", "librarySaveReference", "libraryImport", "libraryRefresh", "librarySaveDetails", "libraryOpen", "libraryCompare", "libraryExport", "libraryRemove" })
        {
            var button = ModernWorkbenchProbe.Find<Button>(control, name);
            var preferred = button.GetPreferredSize(Size.Empty); var text = TextRenderer.MeasureText(button.Text, button.Font);
            Assert.True(button.ClientSize.Width >= text.Width + 12 && button.Width >= preferred.Width && button.Height >= preferred.Height,
                name + " must show its complete distinct caption '" + button.Text + "' at DPI=" + control.DeviceDpi + "; actual=" + button.Size + "; preferred=" + preferred + "; text=" + text + ".");
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    internal sealed class LibraryFolder : IDisposable
    {
        internal LibraryFolder() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpectrumLibraryTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        internal string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}

// Explicit shown acceptance (outside discovery), usable by the coordinated runtime runner.
public static class SessionLibraryVerification
{
    public static string Run(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
        var directory = Path.Combine(output, "library-" + Guid.NewGuid().ToString("N"));
        var workspace = AnalysisSessionLibraryTests.Measured(-26);
        var source = Path.Combine(output, "import-" + Guid.NewGuid().ToString("N") + ".spectrum-session.xml");
        workspace.SaveSession(source); workspace.LoadSession(source); var originalSource = File.ReadAllBytes(source);
        var export = Path.Combine(output, "export-" + Guid.NewGuid().ToString("N") + ".spectrum-session.xml");
        using var form = new FormAudioSpectrum { ClientSize = new Size(1200, 820) }; form.Show(); ModernWorkbenchProbe.Pump();
        var hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(FormAudioSpectrum).GetField("_analysisWorkspace", hidden).SetValue(form, workspace);
        form.NavigateWorkspace("Sessions"); ModernWorkbenchProbe.Pump();
        var field = typeof(FormAudioSpectrum).GetField("_sessionLibrary", hidden);
        var previousControl = (SessionLibraryControl)field.GetValue(form);
        var host = (Panel)typeof(FormAudioSpectrum).GetField("_workspaceHost", hidden).GetValue(form);
        host.Controls.Remove(previousControl); previousControl.Dispose();
        using var control = new SessionLibraryControl(workspace, directory); field.SetValue(form, control); host.Controls.Add(control);
        var events = new System.Collections.Generic.List<string>();
        control.NavigationRequested += request => { events.Add(request.Workspace); form.NavigateWorkspace(request.Workspace); };
        form.NavigateWorkspace("Sessions"); ModernWorkbenchProbe.Pump(); SessionLibraryNativeDialogDriver.Completed = 0;
        try
        {
            ModernWorkbenchProbe.Find<TextBox>(control, "libraryNewName").Text = "Quiet reference";
            ModernWorkbenchProbe.Find<Button>(control, "librarySaveAnalysis").PerformClick();
            ModernWorkbenchProbe.Find<TextBox>(control, "libraryNotes").Text = "Voice comparison; preserve original measurements";
            ModernWorkbenchProbe.Find<CheckBox>(control, "libraryFavorite").Checked = true;
            var entry = Assert.Single(control.Library.Query()); Assert.True(entry.IsFavorite); Assert.Contains("Voice", entry.Notes);
            ModernWorkbenchProbe.Find<TextBox>(control, "librarySearch").Text = "voice"; Assert.Single(ModernWorkbenchProbe.Find<ListBox>(control, "libraryEntries").Items.Cast<object>());
            var original = workspace.Latest; ModernWorkbenchProbe.Find<Button>(control, "libraryCompare").PerformClick(); Assert.Same(original, workspace.Latest);
            Assert.Equal("Compare", (string)typeof(FormAudioSpectrum).GetField("_workspaceView", hidden).GetValue(form));
            form.NavigateWorkspace("Sessions"); ModernWorkbenchProbe.Pump();
            ModernWorkbenchProbe.Find<Button>(control, "libraryOpen").PerformClick(); Assert.True(workspace.IsSavedSession); Assert.Equal(new[] { "Compare", "Analyze" }, events);
            Assert.Equal("Analyze", (string)typeof(FormAudioSpectrum).GetField("_workspaceView", hidden).GetValue(form));
            form.NavigateWorkspace("Sessions"); ModernWorkbenchProbe.Pump();
            ModernWorkbenchProbe.Find<TextBox>(control, "librarySearch").Text = "";
            SessionLibraryNativeDialogDriver.Run(form, () => ModernWorkbenchProbe.Find<Button>(control, "libraryImport").PerformClick(),
                "library Import", "Import saved analysis or reference", source, false);
            Assert.Collection(control.Library.Query(), item => Assert.True(item.IsAvailable), item => Assert.True(item.IsAvailable)); Assert.Equal(originalSource, File.ReadAllBytes(source));
            SessionLibraryNativeDialogDriver.Run(form, () => ModernWorkbenchProbe.Find<Button>(control, "libraryExport").PerformClick(),
                "library Export", "Export library entry", export, false);
            Assert.True(File.Exists(export)); var exported = new AnalysisWorkspace(); exported.LoadSession(export);
            Assert.Equal(workspace.Latest.Power, exported.Latest.Power); Assert.True(exported.IsSavedSession); Assert.Empty(exported.Latest.Samples);
            Assert.Equal(2, SessionLibraryNativeDialogDriver.Completed);
            ModernWorkbenchProbe.Pump();
            AnalysisSessionLibraryTests.AssertCommandTextFits(control);
            using (var image = new Bitmap(form.Width, form.Height))
            {
                using var graphics = Graphics.FromImage(image); var dc = graphics.GetHdc();
                try { Assert.True(PrintWindow(form.Handle, dc, 2), "Native main-owned session library capture must succeed."); }
                finally { graphics.ReleaseHdc(dc); }
                image.Save(Path.Combine(output, "session-library.png"), ImageFormat.Png);
            }
            var json = "{\"passed\":true,\"mainWindowOwned\":true,\"savedEntries\":2,\"favoriteAndNotesPersisted\":true,\"searchMatched\":true,\"comparePreservedCurrent\":true,\"openRestoredSession\":true,\"nativeDialogsExercised\":true,\"nativeImportAndExportCompleted\":2,\"sourceFilePreserved\":true,\"exportHasNoPcm\":true}";
            File.WriteAllText(Path.Combine(output, "session-library.json"), json); return json;
        }
        finally { SessionLibraryNativeDialogDriver.CancelOwned(form.Handle); form.Close(); ModernWorkbenchProbe.Pump(); }
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
