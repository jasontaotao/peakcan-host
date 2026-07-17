using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class ICredentialStoreTests
{
    [Fact]
    public void CredentialStoreException_Constructor_PopulatesKeyAndMessage()
    {
        var ex = new CredentialStoreException("api-key", "read failed");
        ex.Key.Should().Be("api-key");
        ex.Message.Should().Contain("read failed");
    }

    [Fact]
    public void CredentialStoreException_InnerException_Preserved()
    {
        var inner = new InvalidOperationException("underlying");
        var ex = new CredentialStoreException("token", "wrap", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }
}