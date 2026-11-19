using System.Net;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public class ActivationReferencesTests
{
    [Theory]
    [InlineData("US-1356")]
    [InlineData("K-0001@WA1234")]
    [InlineData("US-1356,US-4516")]
    [InlineData("")]
    public void Pota_accepts_park_references_and_lists(string value) =>
        Assert.True(ActivationReferences.IsValid(ActivationProgramme.Pota, value));

    [Theory]
    [InlineData("US1356")]
    [InlineData("US-1356 US-4516")]
    [InlineData("W3/SV-001")]
    public void Pota_rejects_other_shapes(string value) =>
        Assert.False(ActivationReferences.IsValid(ActivationProgramme.Pota, value));

    [Theory]
    [InlineData("W3/SV-001")]
    [InlineData("G/LD-003")]
    [InlineData("")]
    public void Sota_accepts_summit_references(string value) =>
        Assert.True(ActivationReferences.IsValid(ActivationProgramme.Sota, value));

    [Theory]
    [InlineData("W3-SV-001")]
    [InlineData("W3/SV-1")]
    [InlineData("US-1356")]
    public void Sota_rejects_other_shapes(string value) =>
        Assert.False(ActivationReferences.IsValid(ActivationProgramme.Sota, value));

    [Theory]
    [InlineData("KFF-4655")]
    [InlineData("3DAFF-0002")]
    [InlineData("FFF-0003")]
    public void Wwff_accepts_national_programme_references(string value) =>
        Assert.True(ActivationReferences.IsValid(ActivationProgramme.Wwff, value));

    [Theory]
    [InlineData("KFF4655")]
    [InlineData("KFF-46555")]
    [InlineData("KFF-4655,KFF-4656")]
    [InlineData("US-1356")]
    public void Wwff_rejects_other_shapes(string value) =>
        Assert.False(ActivationReferences.IsValid(ActivationProgramme.Wwff, value));

    [Fact]
    public void Normalize_trims_upper_cases_and_removes_spaces_around_commas() =>
        Assert.Equal("US-1356,US-4516", ActivationReferences.Normalize(" us-1356 , us-4516 "));

    [Fact]
    public void Normalize_drops_empty_list_entries_so_a_trailing_comma_is_harmless() =>
        Assert.Equal("US-1356", ActivationReferences.Normalize("US-1356,"));

    [Fact]
    public void ToStored_keeps_valid_values_normalised_and_drops_invalid_ones()
    {
        Assert.Equal("KFF-4655", ActivationReferences.ToStored(ActivationProgramme.Wwff, "kff-4655"));
        Assert.Equal("", ActivationReferences.ToStored(ActivationProgramme.Wwff, "KFF4655"));
        Assert.Equal("", ActivationReferences.ToStored(ActivationProgramme.Sota, null));
    }

    [Fact]
    public void AdifRefTag_uses_the_standard_field_names()
    {
        Assert.Equal("MY_POTA_REF", ActivationReferences.AdifRefTag(ActivationProgramme.Pota));
        Assert.Equal("MY_SOTA_REF", ActivationReferences.AdifRefTag(ActivationProgramme.Sota));
        Assert.Equal("MY_WWFF_REF", ActivationReferences.AdifRefTag(ActivationProgramme.Wwff));
    }
}

public class ActivationReferenceRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"oscarwatch-activation-{Guid.NewGuid():N}.db");
    private readonly QsoLogbookRepository _repository;

    public ActivationReferenceRepositoryTests()
    {
        _repository = new QsoLogbookRepository(_path);
    }

    [Fact]
    public async Task Logbook_references_are_stored_on_create_and_update()
    {
        await _repository.InitializeAsync();

        var created = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = "Park day",
            PotaRef = "us-1356, us-4516",
            SotaRef = "W3/SV-001",
            WwffRef = "KFF-4655"
        });

        Assert.Equal("US-1356,US-4516", created.PotaRef);
        Assert.Equal("W3/SV-001", created.SotaRef);
        Assert.Equal("KFF-4655", created.WwffRef);

        var updated = await _repository.UpdateLogbookAsync(new QsoLogbookUpdateRequest
        {
            Id = created.Id,
            Name = "Park day",
            PotaRef = "K-0001",
            SotaRef = "",
            WwffRef = ""
        });

        Assert.Equal("K-0001", updated.PotaRef);
        Assert.Equal("", updated.SotaRef);
        Assert.Equal("", updated.WwffRef);

        var listed = (await _repository.ListLogbooksAsync()).Single(l => l.Id == created.Id);
        Assert.Equal("K-0001", listed.PotaRef);
    }

    [Fact]
    public async Task Invalid_logbook_references_are_not_stored()
    {
        await _repository.InitializeAsync();

        var created = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = "Bad input",
            PotaRef = "US1356",
            WwffRef = "KFF4655"
        });

        Assert.Equal("", created.PotaRef);
        Assert.Equal("", created.WwffRef);
    }

    [Fact]
    public async Task Qso_keeps_its_own_references_and_they_survive_an_edit()
    {
        await _repository.InitializeAsync();
        var logbook = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest { Name = "Park day" });

        var added = await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = logbook.Id,
            QsoUtc = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc),
            Call = "g0abc",
            PotaRef = "US-1356",
            SotaRef = "W3/SV-001",
            WwffRef = "KFF-4655"
        });

        Assert.Equal("US-1356", added.PotaRef);

        var reloaded = (await _repository.ListQsosAsync(logbook.Id)).Single();
        Assert.Equal("US-1356", reloaded.PotaRef);
        Assert.Equal("W3/SV-001", reloaded.SotaRef);
        Assert.Equal("KFF-4655", reloaded.WwffRef);

        var edited = await _repository.UpdateQsoAsync(new QsoRecordUpdateRequest
        {
            Id = added.Id,
            QsoUtc = added.QsoUtc,
            Call = "G0ABC",
            PotaRef = "US-4516",
            SotaRef = "",
            WwffRef = "KFF-4655"
        });

        Assert.Equal("US-4516", edited.PotaRef);
        Assert.Equal("", edited.SotaRef);
        Assert.Equal("KFF-4655", edited.WwffRef);
    }

    public void Dispose()
    {
        _repository.Dispose();
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (IOException)
        {
            // Temp database may still be held by pooled connections; the temp folder is cleaned by the OS.
        }
    }
}

public class ActivationDirectoryServiceTests : IDisposable
{
    private const string PotaSample =
        "\"reference\",\"name\",\"active\",\"entityId\",\"locationDesc\",\"latitude\",\"longitude\",\"grid\"\n" +
        "\"US-0001\",\"Acadia National Park\",\"1\",\"291\",\"US-ME\",\"44.31\",\"-68.2034\",\"FN54vh\"\n" +
        "\"US-0002\",\"Alagnak Wild River, Preserve\",\"1\",\"6\",\"US-AK\",\"59.0908\",\"-156.463\",\"BO19sc\"\n" +
        "\"US-0003\",\"Retired Park\",\"0\",\"6\",\"US-AK\",\"0\",\"0\",\"\"\n";

    private const string SotaSample =
        "SOTA Summits List (Date=2026-11-01)\n" +
        "SummitCode,AssociationName,RegionName,SummitName,AltM,AltFt,GridRef1,GridRef2,Longitude,Latitude,Points\n" +
        "W3/SV-001,England,South West,\"Hill, Top\",500,1640,1,2,-3.1,50.1,10\n" +
        "G/LD-003,England,Lake District,Skiddaw,931,3054,1,2,-3.1,54.6,10\n";

    private const string WwffSample =
        "reference,status,name,country,dxcc\n" +
        "KFF-4655,active,Rattlesnake Ridge,United States,291\n" +
        "KFF-0001,deleted,Old Name,United States,291\n";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"oscarwatch-dirs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp folder.
        }
    }

    [Fact]
    public void ParsePota_keeps_active_parks_and_handles_quoted_commas()
    {
        var entries = ActivationDirectoryService.ParsePota(new StringReader(PotaSample));

        Assert.Equal(2, entries.Count);
        Assert.Equal("US-0001", entries[0].Reference);
        Assert.Equal("Acadia National Park", entries[0].Name);
        Assert.Equal("US-ME", entries[0].Detail);
        Assert.Equal("Alagnak Wild River, Preserve", entries[1].Name);
    }

    [Fact]
    public void ParseSota_skips_the_title_line_and_reads_the_header()
    {
        var entries = ActivationDirectoryService.ParseSota(new StringReader(SotaSample));

        Assert.Equal(2, entries.Count);
        Assert.Equal("W3/SV-001", entries[0].Reference);
        Assert.Equal("Hill, Top", entries[0].Name);
        Assert.Equal("England / South West", entries[0].Detail);
        Assert.Equal("G/LD-003", entries[1].Reference);
    }

    [Fact]
    public void ParseWwff_keeps_only_active_references()
    {
        var entries = ActivationDirectoryService.ParseWwff(new StringReader(WwffSample));

        var entry = Assert.Single(entries);
        Assert.Equal("KFF-4655", entry.Reference);
        Assert.Equal("Rattlesnake Ridge", entry.Name);
        Assert.Equal("United States", entry.Detail);
    }

    [Fact]
    public async Task Fresh_cache_is_used_without_downloading()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(
            Path.Combine(_folder, "wwff.tsv"),
            "KFF-4655\tRattlesnake Ridge\tUnited States\n");
        var handler = new StubHandler(_ => throw new InvalidOperationException("Should not download."));
        var service = new ActivationDirectoryService(_folder, new HttpClient(handler));

        await service.EnsureLoadedAsync(ActivationProgramme.Wwff);

        Assert.Equal(0, handler.Calls);
        Assert.Equal(1, service.Count(ActivationProgramme.Wwff));
        Assert.Single(service.Search(ActivationProgramme.Wwff, "KFF-46"));
    }

    [Fact]
    public async Task Stale_cache_is_kept_when_the_download_fails()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "wwff.tsv");
        await File.WriteAllTextAsync(path, "KFF-4655\tRattlesnake Ridge\tUnited States\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = new ActivationDirectoryService(_folder, new HttpClient(handler));

        await service.EnsureLoadedAsync(ActivationProgramme.Wwff);

        Assert.True(handler.Calls > 0);
        Assert.Equal(1, service.Count(ActivationProgramme.Wwff));
        Assert.NotEqual("", service.LastError(ActivationProgramme.Wwff));
    }

    [Fact]
    public async Task Stale_cache_is_replaced_by_a_successful_download()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "pota.tsv");
        await File.WriteAllTextAsync(path, "US-0009\tOld park\tUS-ME\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PotaSample)
        });
        var service = new ActivationDirectoryService(_folder, new HttpClient(handler));

        await service.EnsureLoadedAsync(ActivationProgramme.Pota);

        Assert.Equal(2, service.Count(ActivationProgramme.Pota));
        Assert.Equal("", service.LastError(ActivationProgramme.Pota));
        Assert.Single(service.Search(ActivationProgramme.Pota, "acadia"));
        Assert.Empty(service.Search(ActivationProgramme.Pota, "US-0009"));
    }

    [Fact]
    public async Task Search_puts_reference_prefix_matches_before_name_matches()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(
            Path.Combine(_folder, "pota.tsv"),
            "XX-0001\tCamp US-1\tUS-ME\n" +
            "US-1356\tFort Rock\tUS-OR\n");
        var service = new ActivationDirectoryService(_folder, new HttpClient(new StubHandler(_ => throw new InvalidOperationException())));
        await service.EnsureLoadedAsync(ActivationProgramme.Pota);

        var results = service.Search(ActivationProgramme.Pota, "us-1");

        Assert.Equal(["US-1356", "XX-0001"], results.Select(r => r.Reference).ToArray());
    }

    [Fact]
    public void Search_returns_nothing_before_the_directory_is_loaded()
    {
        var service = new ActivationDirectoryService(_folder, new HttpClient(new StubHandler(_ => throw new InvalidOperationException())));

        Assert.Empty(service.Search(ActivationProgramme.Sota, "W3"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
