using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class SchemePlannerTests
{
    private static readonly ConnectionSettings _s3 = new() { Id = Guid.NewGuid(), Name = "b2", Type = ConnectionType.S3, Bucket = "media", BucketPath = "jf" };
    private static readonly ConnectionSettings _sftp = new() { Id = Guid.NewGuid(), Name = "ssh", Type = ConnectionType.Sftp, RemoteRoot = "/srv/ssh" };
    private static readonly ConnectionSettings _server = new() { Id = Guid.NewGuid(), Name = "srv", Type = ConnectionType.Server, ServerPath = "/srv/media" };
    private static readonly ConnectionSettings _local = new() { Id = Guid.NewGuid(), Name = "local", Type = ConnectionType.Worker, LocalRoot = "/data" };

    private readonly WorkerSettings _settings = new() { Connections = [_s3, _sftp, _server, _local] };

    [Fact]
    public void Source_WithoutScheme_UploadsNowhere_AndDeclaresTheFileOfThisMachine()
    {
        var plan = SchemePlanner.ResolveSource(_settings, null, Library());

        Assert.Empty(plan.Uploads);
        Assert.Null(plan.Declaration);
    }

    [Theory]
    [InlineData(true, "films")]
    [InlineData(false, "")]
    public void Source_PutsTheFilesBelowThePrefix_WhenTheLibraryAsks(bool prefixed, string expected)
    {
        var scheme = Scheme(sources: [Line(_s3, true)]);

        var plan = SchemePlanner.ResolveSource(_settings, scheme, Library("/films/", prefixSources: prefixed));

        Assert.Equal(expected, Assert.Single(plan.Uploads).Path);
        Assert.Equal(new PlanDeclaration(DeclarationKind.S3, _s3, expected), plan.Declaration);
        Assert.Equal(prefixed ? "b2:films" : "b2", plan.Uploads[0].Label);
    }

    [Fact]
    public void Source_UploadsToEveryLine_AndDeclaresTheOneDeclared()
    {
        var scheme = Scheme(sources: [Line(_local), Line(_s3, true), Line(_sftp)]);

        var plan = SchemePlanner.ResolveSource(_settings, scheme, Library("a"));

        Assert.Equal([_local, _s3, _sftp], plan.Uploads.Select(u => u.Connection));
        Assert.Equal(new PlanDeclaration(DeclarationKind.S3, _s3, "a"), plan.Declaration);
    }

    [Fact]
    public void Source_DeclaringNothing_DeclaresTheFileOfThisMachine()
    {
        var plan = SchemePlanner.ResolveSource(_settings, Scheme(sources: [Line(_s3), Line(_local)]), Library());

        Assert.Equal(2, plan.Uploads.Count);
        Assert.Null(plan.Declaration);
    }

    [Fact]
    public void Source_DeclaresADirectoryOfTheServer_AndNeverUploadsToIt()
    {
        var scheme = Scheme(sources: [Line(_local), Line(_server, true)]);

        var plan = SchemePlanner.ResolveSource(_settings, scheme, Library("p"));

        Assert.Equal(_local, Assert.Single(plan.Uploads).Connection);
        Assert.Equal(new PlanDeclaration(DeclarationKind.ServerPath, _server, "p"), plan.Declaration);
    }

    [Fact]
    public void Source_TellsTheKindOfEachDirectory()
    {
        Assert.Equal(DeclarationKind.ServerPath, Kind(_sftp));
        Assert.Equal(DeclarationKind.ServerPath, Kind(_server));
        Assert.Equal(DeclarationKind.WorkerLocal, Kind(_local));

        DeclarationKind Kind(ConnectionSettings connection)
            => SchemePlanner.ResolveSource(_settings, Scheme(sources: [Line(connection, true)]), Library()).Declaration!.Kind;
    }

    [Fact]
    public void Source_IntoTheLibrary_IsDeclaredAsTheFileOfTheLibrary()
    {
        var scheme = Scheme(sources: [Line(_s3), new SchemeEntry { ConnectionId = SchemeEntry.JellyfinLibrary, Declare = true }]);

        var plan = SchemePlanner.ResolveSource(_settings, scheme, Library("films"));

        Assert.Null(plan.Uploads[1].Connection);
        Assert.Equal(SchemePlanner.JellyfinLibraryLabel + ":films", plan.Uploads[1].Label);
        Assert.Equal(new PlanDeclaration(DeclarationKind.JellyfinLibrary, null, "films"), plan.Declaration);
    }

    [Fact]
    public void Source_RefusesTheLibraryNotDeclared()
    {
        var scheme = Scheme(sources: [new SchemeEntry { ConnectionId = SchemeEntry.JellyfinLibrary }, Line(_s3, true)]);

        Assert.Throws<ArgumentException>(() => SchemePlanner.ResolveSource(_settings, scheme, Library()));
    }

    [Fact]
    public void Hls_WithoutLines_GoesNowhere()
    {
        Assert.Null(SchemePlanner.ResolveHls(_settings, null, Library()));
        Assert.Null(SchemePlanner.ResolveHls(_settings, Scheme(sources: [Line(_s3, true)]), Library()));
        Assert.Null(SchemePlanner.ResolveHls(_settings, Scheme(hls: [new SchemeEntry()]), Library()));
    }

    [Fact]
    public void Hls_ThatDeclaresNothing_IsDeclaredOnThisMachine()
    {
        var scheme = Scheme(hls: [Line(_s3)]);

        var plan = SchemePlanner.ResolveHls(_settings, scheme, Library("movies"))!;

        Assert.Null(plan.Base);
        Assert.Null(plan.DeclaredS3);
        Assert.Equal("movies", plan.LocalPath);
        Assert.Equal("movies", Assert.Single(plan.Uploads).Path);
    }

    [Theory]
    [InlineData(0, "s3://media/jf/movies")]
    [InlineData(1, "/srv/ssh/movies")]
    [InlineData(2, "/srv/media/movies")]
    [InlineData(3, "/data/movies")]
    public void Hls_NamesTheRootAsTheServerSeesIt(int declared, string expected)
    {
        var connection = _settings.Connections[declared];
        var scheme = Scheme(hls: connection == _local ? [Line(_local, true)] : [Line(_local), Line(connection, true)]);

        var plan = SchemePlanner.ResolveHls(_settings, scheme, Library("movies"))!;

        Assert.Equal(expected, plan.Base);
        Assert.Equal(declared == 0 ? _s3 : null, plan.DeclaredS3);
        // The directory of the server is only declared.
        ConnectionSettings[] uploads = connection == _server || connection == _local ? [_local] : [_local, connection];
        Assert.Equal(uploads, plan.Uploads.Select(u => u.Connection));
        Assert.All(plan.Uploads, u => Assert.Equal("movies", u.Path));
    }

    [Fact]
    public void Hls_DeclaringOnlyADirectoryOfTheServer_IsRefused()
    {
        var scheme = Scheme(hls: [Line(_server, true)]);

        Assert.Throws<ArgumentException>(() => SchemePlanner.ResolveHls(_settings, scheme, Library()));
    }

    [Fact]
    public void Hls_LeavesThePrefixOut_WhenTheLibraryAsks()
    {
        var scheme = Scheme(hls: [Line(_s3, true)]);

        var plan = SchemePlanner.ResolveHls(_settings, scheme, Library("movies", prefixHls: false))!;

        Assert.Equal("s3://media/jf", plan.Base);
        Assert.Equal(string.Empty, plan.Uploads[0].Path);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("/a/", "b", "a/b")]
    [InlineData("a", "", "a")]
    [InlineData("", "/b/c/", "b/c")]
    public void Join_LeavesOutTheEmptyParts(string first, string second, string expected)
        => Assert.Equal(expected, SchemePlanner.Join(first, second));

    private static LibrarySettings Library(string prefix = "", bool prefixSources = true, bool prefixHls = true)
        => new() { Id = Guid.NewGuid(), Prefix = prefix, PrefixSources = prefixSources, PrefixHls = prefixHls };

    private static SchemeSettings Scheme(List<SchemeEntry>? hls = null, List<SchemeEntry>? sources = null)
        => new() { Id = Guid.NewGuid(), Name = "s", Hls = hls ?? [], Sources = sources ?? [] };

    private static SchemeEntry Line(ConnectionSettings connection, bool declare = false) => new() { ConnectionId = connection.Id, Declare = declare };
}
