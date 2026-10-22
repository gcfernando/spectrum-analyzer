using System.Windows.Forms;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("Spectrum.Tests.SpectrumTestFramework", "Spectrum.Tests")]

namespace Spectrum.Tests;

// Match Program.Main before any test creates a native control. Enabling visual
// styles later, in an individual case, leaves earlier registered edit classes
// unthemed and makes common-controls features depend on test execution order.
public sealed class SpectrumTestFramework : XunitTestFramework
{
    public SpectrumTestFramework(IMessageSink messageSink) : base(messageSink)
        => Application.EnableVisualStyles();
}
