using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MineOS.Application.Options;
using MineOS.Infrastructure.Services;

namespace MineOS.Tests.Unit;

/// <summary>
/// The file manager runs as root on the host, so these pin down that it can only
/// ever touch the server's own directory - also through symbolic links - and that
/// it never leaves a half-written file behind.
/// </summary>
public sealed class FileServiceTests : IDisposable
{
    private const string Server = "srv";

    private readonly string _base;
    private readonly string _serverDir;
    private readonly string _outside;
    private readonly FileService _files;

    public FileServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "mineos-files-" + Guid.NewGuid().ToString("N"));
        _serverDir = Path.Combine(_base, "servers", Server);
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "host secret");
        File.WriteAllText(Path.Combine(_serverDir, "server.properties"), "motd=hi\n");

        // Ownership is not under test here (see the filesystem-ownership skill), and
        // chowning temp files to another uid would only get in the way of cleanup.
        var options = Options.Create(new HostOptions
        {
            BaseDirectory = _base,
            RunAsUid = Environment.IsPrivilegedProcess ? 0 : -1,
            RunAsGid = Environment.IsPrivilegedProcess ? 0 : -1
        });
        _files = new FileService(Mock.Of<ILogger<FileService>>(), options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private string InServer(string relative) => Path.Combine(_serverDir, relative);

    // ---- symbolic links (finding 1) -------------------------------------------------

    [Fact]
    public async Task Cannot_Read_Through_A_Link_To_Outside()
    {
        Directory.CreateSymbolicLink(InServer("plugins-data"), _outside);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.ReadFileAsync(Server, "/plugins-data/secret.txt", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.ListFilesAsync(Server, "/plugins-data", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.OpenReadAsync(Server, "/plugins-data/secret.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Cannot_Read_A_File_Link_To_Outside()
    {
        File.CreateSymbolicLink(InServer("innocent.txt"), Path.Combine(_outside, "secret.txt"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.ReadFileAsync(Server, "/innocent.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Cannot_Write_Through_A_Link_To_Outside()
    {
        Directory.CreateSymbolicLink(InServer("plugins-data"), _outside);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.WriteFileAsync(Server, "/plugins-data/planted.txt", "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.WriteFileAsync(Server, "/plugins-data/deeper/planted.txt", "x", CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_outside, "planted.txt")));
        Assert.False(Directory.Exists(Path.Combine(_outside, "deeper")));
    }

    [Fact]
    public async Task Cannot_Overwrite_A_File_Link_To_Outside()
    {
        File.CreateSymbolicLink(InServer("innocent.txt"), Path.Combine(_outside, "secret.txt"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.WriteFileAsync(Server, "/innocent.txt", "pwned", CancellationToken.None));

        Assert.Equal("host secret", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public async Task Links_That_Stay_Inside_Still_Work()
    {
        Directory.CreateDirectory(InServer("real"));
        File.WriteAllText(InServer("real/a.txt"), "inside");
        Directory.CreateSymbolicLink(InServer("alias"), InServer("real"));

        var read = await _files.ReadFileAsync(Server, "/alias/a.txt", CancellationToken.None);
        Assert.Equal("inside", read.Content);
    }

    [Fact]
    public async Task Deleting_A_Link_Removes_The_Link_Not_Its_Target()
    {
        Directory.CreateSymbolicLink(InServer("plugins-data"), _outside);

        await _files.DeleteFileAsync(Server, "/plugins-data", CancellationToken.None);

        Assert.False(Path.Exists(InServer("plugins-data")));
        Assert.True(File.Exists(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public async Task Cannot_Delete_Through_A_Link_To_Outside()
    {
        Directory.CreateSymbolicLink(InServer("plugins-data"), _outside);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.DeleteFileAsync(Server, "/plugins-data/secret.txt", CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public async Task Plain_Traversal_Is_Still_Refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.ReadFileAsync(Server, "/../../outside/secret.txt", CancellationToken.None));
    }

    // ---- the server directory itself (finding 2) ------------------------------------

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("/.")]
    [InlineData("/plugins/..")]
    public async Task The_Server_Directory_Itself_Cannot_Be_Deleted(string path)
    {
        Directory.CreateDirectory(InServer("plugins"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.DeleteFileAsync(Server, path, CancellationToken.None));

        Assert.True(File.Exists(InServer("server.properties")));
    }

    [Fact]
    public async Task A_Blank_Name_Is_A_Name_Not_The_Root()
    {
        // " " used to be trimmed to "" -> "/" -> Directory.Delete(serverDir).
        File.WriteAllText(InServer(" "), "odd but legal");

        await _files.DeleteFileAsync(Server, "/ ", CancellationToken.None);

        Assert.False(File.Exists(InServer(" ")));
        Assert.True(File.Exists(InServer("server.properties")));
    }

    [Fact]
    public async Task The_Server_Directory_Cannot_Be_Moved_Or_Overwritten()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.MoveAsync(Server, "/", "/elsewhere", false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.WriteFileAsync(Server, "/", "x", CancellationToken.None));
    }

    // ---- atomic writes (finding 7) / streaming uploads (finding 8) ------------------

    [Fact]
    public async Task Write_Replaces_The_File_And_Leaves_No_Temp_Behind()
    {
        await _files.WriteFileAsync(Server, "/server.properties", "motd=new\n", CancellationToken.None);

        Assert.Equal("motd=new\n", File.ReadAllText(InServer("server.properties")));
        Assert.Empty(Directory.GetFiles(_serverDir, "*.mineos-tmp"));
    }

    [Fact]
    public async Task A_Failed_Write_Keeps_The_Old_File()
    {
        var failing = new FailingStream(bytesBeforeFailure: 10);

        await Assert.ThrowsAsync<IOException>(() =>
            _files.WriteFileStreamAsync(Server, "/server.properties", failing, CancellationToken.None));

        Assert.Equal("motd=hi\n", File.ReadAllText(InServer("server.properties")));
        Assert.Empty(Directory.GetFiles(_serverDir, "*.mineos-tmp"));
    }

    [Fact]
    public async Task Write_Keeps_The_File_Mode()
    {
        var script = InServer("start.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await _files.WriteFileAsync(Server, "/start.sh", "#!/bin/sh\necho hi\n", CancellationToken.None);

        Assert.True(File.GetUnixFileMode(script).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Writing_Through_An_Inside_Link_Updates_The_Target()
    {
        Directory.CreateDirectory(InServer("real"));
        File.WriteAllText(InServer("real/a.txt"), "old");
        File.CreateSymbolicLink(InServer("a-link.txt"), InServer("real/a.txt"));

        await _files.WriteFileAsync(Server, "/a-link.txt", "new", CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(InServer("real/a.txt")));
        Assert.NotNull(new FileInfo(InServer("a-link.txt")).LinkTarget);
    }

    [Fact]
    public async Task Stream_Upload_Creates_Missing_Folders()
    {
        var data = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();

        await _files.WriteFileStreamAsync(Server, "/world/region/r.0.0.mca", new MemoryStream(data), CancellationToken.None);

        Assert.Equal(data, File.ReadAllBytes(InServer("world/region/r.0.0.mca")));
    }

    // ---- new file / folder, move, copy (finding 15) ---------------------------------

    [Fact]
    public async Task Create_File_And_Folder_Refuse_Existing_Paths()
    {
        await _files.CreateDirectoryAsync(Server, "/plugins/Essentials", CancellationToken.None);
        await _files.CreateFileAsync(Server, "/plugins/Essentials/config.yml", CancellationToken.None);

        Assert.True(Directory.Exists(InServer("plugins/Essentials")));
        Assert.Equal(0, new FileInfo(InServer("plugins/Essentials/config.yml")).Length);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _files.CreateFileAsync(Server, "/server.properties", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _files.CreateDirectoryAsync(Server, "/plugins", CancellationToken.None));
    }

    [Fact]
    public async Task Move_Renames_And_Respects_Overwrite()
    {
        File.WriteAllText(InServer("a.txt"), "a");
        File.WriteAllText(InServer("b.txt"), "b");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _files.MoveAsync(Server, "/a.txt", "/b.txt", overwrite: false, CancellationToken.None));
        Assert.Equal("b", File.ReadAllText(InServer("b.txt")));

        await _files.MoveAsync(Server, "/a.txt", "/b.txt", overwrite: true, CancellationToken.None);
        Assert.False(File.Exists(InServer("a.txt")));
        Assert.Equal("a", File.ReadAllText(InServer("b.txt")));

        await _files.MoveAsync(Server, "/b.txt", "/sub/dir/c.txt", overwrite: false, CancellationToken.None);
        Assert.Equal("a", File.ReadAllText(InServer("sub/dir/c.txt")));
    }

    [Fact]
    public async Task Move_Refuses_Folder_Into_Itself_And_Onto_Folders()
    {
        Directory.CreateDirectory(InServer("world/region"));
        Directory.CreateDirectory(InServer("other"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.MoveAsync(Server, "/world", "/world/region/world", false, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _files.MoveAsync(Server, "/world", "/other", true, CancellationToken.None));
    }

    [Fact]
    public async Task Cannot_Move_Out_Through_A_Link()
    {
        Directory.CreateSymbolicLink(InServer("plugins-data"), _outside);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.MoveAsync(Server, "/server.properties", "/plugins-data/stolen.properties", false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.MoveAsync(Server, "/plugins-data/secret.txt", "/secret.txt", false, CancellationToken.None));
        Assert.True(File.Exists(InServer("server.properties")));
    }

    [Fact]
    public async Task Copy_Copies_Trees_Without_Following_Links()
    {
        Directory.CreateDirectory(InServer("world/region"));
        File.WriteAllText(InServer("world/level.dat"), "level");
        File.WriteAllText(InServer("world/region/r.0.0.mca"), "chunks");
        Directory.CreateSymbolicLink(InServer("world/escape"), _outside);

        await _files.CopyAsync(Server, "/world", "/world-backup", false, CancellationToken.None);

        Assert.Equal("level", File.ReadAllText(InServer("world-backup/level.dat")));
        Assert.Equal("chunks", File.ReadAllText(InServer("world-backup/region/r.0.0.mca")));
        Assert.False(Path.Exists(InServer("world-backup/escape")));
        Assert.Equal("level", File.ReadAllText(InServer("world/level.dat")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _files.CopyAsync(Server, "/world", "/world-backup", true, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _files.CopyAsync(Server, "/world", "/world/inner", false, CancellationToken.None));
    }

    // ---- download (finding 13) ------------------------------------------------------

    [Fact]
    public async Task Directory_Download_Is_A_Zip_Without_Links()
    {
        Directory.CreateDirectory(InServer("world/region"));
        Directory.CreateDirectory(InServer("world/empty"));
        File.WriteAllText(InServer("world/level.dat"), "level");
        File.WriteAllText(InServer("world/region/r.0.0.mca"), "chunks");
        Directory.CreateSymbolicLink(InServer("world/escape"), _outside);

        var info = await _files.GetDownloadInfoAsync(Server, "/world", CancellationToken.None);
        Assert.True(info.IsDirectory);
        Assert.Equal("world.zip", info.FileName);

        using var buffer = new MemoryStream();
        await _files.WriteDirectoryZipAsync(Server, "/world", buffer, CancellationToken.None);
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "empty/", "level.dat", "region/r.0.0.mca" }, names);
        using var reader = new StreamReader(zip.GetEntry("region/r.0.0.mca")!.Open());
        Assert.Equal("chunks", reader.ReadToEnd());
    }

    [Fact]
    public async Task File_Download_Streams_The_File()
    {
        var info = await _files.GetDownloadInfoAsync(Server, "/server.properties", CancellationToken.None);
        Assert.False(info.IsDirectory);
        Assert.Equal("server.properties", info.FileName);
        Assert.Equal(8, info.Length);

        await using var stream = await _files.OpenReadAsync(Server, "/server.properties", CancellationToken.None);
        using var reader = new StreamReader(stream);
        Assert.Equal("motd=hi\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Root_Download_Is_Named_After_The_Server()
    {
        var info = await _files.GetDownloadInfoAsync(Server, "/", CancellationToken.None);
        Assert.Equal("srv.zip", info.FileName);
    }

    /// <summary>Delivers some bytes, then fails like a dropped upload.</summary>
    private sealed class FailingStream : Stream
    {
        private int _remaining;
        public FailingStream(int bytesBeforeFailure) => _remaining = bytesBeforeFailure;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) throw new IOException("connection reset");
            var n = Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)'x', offset, n);
            _remaining -= n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
