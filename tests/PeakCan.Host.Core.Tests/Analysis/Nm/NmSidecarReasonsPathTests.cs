using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmSidecarReasonsPathTests
{
    [Fact]
    public void Sidecar_RoundTripsReasonsPath()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.nmprofile.json");
        try
        {
            NmSidecarStore.SaveTo(path, new NmUserSupplement(
                ReasonsPath: "reasons.nmreasons.json"));
            var loaded = NmSidecarStore.LoadFrom(path);
            Assert.NotNull(loaded);
            Assert.Equal("reasons.nmreasons.json", loaded!.ReasonsPath);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
