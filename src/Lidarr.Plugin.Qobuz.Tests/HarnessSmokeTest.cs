using FluentAssertions;
using NUnit.Framework;

namespace Lidarr.Plugin.Qobuz.Tests
{
    /// <summary>
    /// Proves the test harness itself is wired up: NUnit discovers tests, FluentAssertions
    /// resolves, and the project under test is referenced successfully. If this fails, the
    /// problem is the toolchain, not the plugin.
    /// </summary>
    [TestFixture]
    public class HarnessSmokeTest
    {
        [Test]
        public void Harness_runs_and_assertions_resolve()
        {
            var pluginAssembly = typeof(NzbDrone.Core.Plugins.QobuzPlugin).Assembly;

            pluginAssembly.GetName().Name.Should().Be("Lidarr.Plugin.Qobuz");
        }
    }
}
