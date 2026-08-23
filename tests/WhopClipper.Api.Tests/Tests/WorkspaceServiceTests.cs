using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class WorkspaceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clipper-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolvesChildButRejectsTraversal()
    {
        var service = new WorkspaceService(Options.Create(new AppOptions { WorkspaceRoot = _root }));
        Assert.StartsWith(Path.GetFullPath(_root), service.ResolveRelative(Path.Combine("campaigns", "one")));
        Assert.Throws<InvalidOperationException>(() => service.ResolveRelative(Path.Combine("..", "outside.txt")));
    }

    [Theory]
    [InlineData("../bad:name?.mp4", "bad_name_")]
    [InlineData("", "media")]
    public void SanitizesFileNames(string input, string expectedFragment)
    {
        Assert.Contains(expectedFragment, WorkspaceService.SafeFileName(input));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
