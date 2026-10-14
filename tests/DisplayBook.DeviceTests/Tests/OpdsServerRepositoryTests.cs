using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests;

[Collection(AppDataCollection.Name)]
public sealed partial class OpdsServerRepositoryTests : IDisposable
{
	static readonly string serversFile = Path.Combine(BookStorageService.ContentRoot, "Opds", "servers.json");

	readonly OpdsServerRepository repository = new();

	public OpdsServerRepositoryTests() => AppDataSandbox.Reset();

	public void Dispose() => repository.Dispose();

	static OpdsServer MakeServer(string name, string url, string id = "") => new() { Id = id, Name = name, Url = url };

	[Fact]
	public async Task GetAll_NoFile_IsEmpty()
	{
		Assert.Empty(await repository.GetAllAsync());
	}

	[Fact]
	public async Task Add_BlankId_AssignsOneAndWritesThrough()
	{
		OpdsServer stored = await repository.AddAsync(MakeServer("Calibre", "http://calibre.local:8080/opds"));

		Assert.False(string.IsNullOrWhiteSpace(stored.Id));
		Assert.True(File.Exists(serversFile));

		using OpdsServerRepository reopened = new();
		OpdsServer reloaded = Assert.Single(await reopened.GetAllAsync());
		Assert.Equal((stored.Id, "Calibre", "http://calibre.local:8080/opds"), (reloaded.Id, reloaded.Name, reloaded.Url));
	}

	[Fact]
	public async Task Add_StoresACopy()
	{
		OpdsServer input = MakeServer("Calibre", "http://calibre.local:8080/opds", id: "s1");

		await repository.AddAsync(input);
		input.Name = "Changed after adding";

		Assert.Equal("Calibre", repository.GetById("s1")?.Name);
	}

	[Fact]
	public async Task Add_ExistingId_IsNotDuplicated()
	{
		await repository.AddAsync(MakeServer("First", "http://a.local/opds", id: "s1"));
		await repository.AddAsync(MakeServer("Second", "http://b.local/opds", id: "s1"));

		OpdsServer only = Assert.Single(await repository.GetAllAsync());
		Assert.Equal("First", only.Name);
	}

	[Fact]
	public async Task GetAll_OrdersByNameIgnoringCase()
	{
		await repository.AddAsync(MakeServer("charlie", "http://c.local/opds"));
		await repository.AddAsync(MakeServer("Alpha", "http://a.local/opds"));
		await repository.AddAsync(MakeServer("bravo", "http://b.local/opds"));

		Assert.Equal(["Alpha", "bravo", "charlie"], (await repository.GetAllAsync()).Select(server => server.Name));
	}

	[Fact]
	public async Task Update_ReplacesTheStoredServer()
	{
		await repository.AddAsync(MakeServer("Old", "http://old.local/opds", id: "s1"));

		await repository.UpdateAsync("s1", MakeServer("New", "http://new.local/opds"));

		OpdsServer updated = Assert.Single(await repository.GetAllAsync());
		Assert.Equal(("s1", "New", "http://new.local/opds"), (updated.Id, updated.Name, updated.Url));
	}

	[Fact]
	public async Task Update_UnknownId_DoesNothing()
	{
		await repository.AddAsync(MakeServer("Only", "http://only.local/opds", id: "s1"));

		await repository.UpdateAsync("missing", MakeServer("Ghost", "http://ghost.local/opds"));

		Assert.Equal(["Only"], (await repository.GetAllAsync()).Select(server => server.Name));
	}

	[Fact]
	public async Task Remove_DeletesAndPersists()
	{
		await repository.AddAsync(MakeServer("Keep", "http://keep.local/opds", id: "keep"));
		await repository.AddAsync(MakeServer("Drop", "http://drop.local/opds", id: "drop"));

		await repository.RemoveAsync("drop");

		using OpdsServerRepository reopened = new();
		Assert.Equal(["keep"], (await reopened.GetAllAsync()).Select(server => server.Id));
	}

	[Theory]
	[InlineData("http://calibre.local:8080/opds/")]
	[InlineData("HTTP://CALIBRE.LOCAL:8080/opds")]
	[InlineData("http://calibre.local:8080/opds?page=2")]
	public async Task FindByUrl_MatchesNormalizedUrl(string lookup)
	{
		await repository.AddAsync(MakeServer("Calibre", "http://calibre.local:8080/opds", id: "s1"));

		Assert.Equal("s1", repository.FindByUrl(lookup)?.Id);
	}

	[Fact]
	public async Task FindByUrl_OtherServer_ReturnsNull()
	{
		await repository.AddAsync(MakeServer("Calibre", "http://calibre.local:8080/opds"));

		Assert.Null(repository.FindByUrl("http://calibre.local:9090/opds"));
		Assert.Null(repository.FindByUrl(" "));
	}

	[Fact]
	public async Task CorruptFile_LoadsEmptyAndIsOverwrittenByTheNextSave()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(serversFile)!);
		await File.WriteAllTextAsync(serversFile, "{ this is not json");

		Assert.Empty(await repository.GetAllAsync());

		await repository.AddAsync(MakeServer("Fresh", "http://fresh.local/opds"));
		using OpdsServerRepository reopened = new();
		Assert.Equal(["Fresh"], (await reopened.GetAllAsync()).Select(server => server.Name));
	}

	[Fact]
	public async Task Load_DropsIncompleteEntriesAndDuplicateIds()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(serversFile)!);
		await File.WriteAllTextAsync(serversFile, """
			[
				{ "id": "s1", "name": "Kept", "url": "http://kept.local/opds" },
				{ "id": "s1", "name": "Duplicate id", "url": "http://dup.local/opds" },
				{ "id": "s2", "name": "", "url": "http://no-name.local/opds" },
				{ "id": "s3", "name": "No url", "url": "" },
				{ "id": "", "name": "No id", "url": "http://no-id.local/opds" }
			]
			""");

		IReadOnlyList<OpdsServer> servers = await repository.GetAllAsync();

		Assert.Equal(["Kept", "No id"], servers.Select(server => server.Name));
		Assert.False(string.IsNullOrWhiteSpace(servers[1].Id));
	}
}
