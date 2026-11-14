using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class PersonsControllerTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IDtoService> _dtoService = new();
    private readonly Mock<ISearchManager> _searchManager = new();
    private readonly List<InternalPeopleQuery> _queries = [];
    private readonly ServerConfiguration _configuration = new();
    private BaseItem[] _peopleItems = [];

    public PersonsControllerTests()
    {
        _libraryManager.Setup(m => m.GetPeopleItems(It.IsAny<InternalPeopleQuery>()))
            .Callback<InternalPeopleQuery>(_queries.Add)
            .Returns(() => new QueryResult<BaseItem>(_peopleItems));
        _dtoService.Setup(m => m.GetItemByNameDto(It.IsAny<BaseItem>(), It.IsAny<DtoOptions>(), It.IsAny<List<BaseItem>?>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User?>()))
            .Returns<BaseItem, DtoOptions, List<BaseItem>?, Jellyfin.Database.Implementations.Entities.User?>((item, _, _, _) => new BaseItemDto { Name = item.Name });
        _searchManager.Setup(m => m.GetProviders()).Returns([]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetPersons_EnableTotalRecordCount_PassedToQuery(bool enableTotalRecordCount)
    {
        await CallGetPersons(searchTerm: null, enableTotalRecordCount: enableTotalRecordCount);

        Assert.Equal(enableTotalRecordCount, Assert.Single(_queries).EnableTotalRecordCount);
    }

    [Fact]
    public async Task GetPersons_NoExternalProvider_SearchesNames()
    {
        await CallGetPersons(searchTerm: "seven");

        var query = Assert.Single(_queries);
        Assert.Equal("seven", query.NameContains);
        Assert.Null(query.Names);
    }

    [Fact]
    public async Task GetPersons_ExternalProviderFindsNothing_SearchesNames()
    {
        AddExternalProvider();

        await CallGetPersons(searchTerm: "seven");

        var query = Assert.Single(_queries);
        Assert.Equal("seven", query.NameContains);
        Assert.Null(query.Names);
    }

    [Fact]
    public async Task GetPersons_ExternalProviderFails_SearchesNames()
    {
        var provider = new Mock<IExternalSearchProvider>();
        provider.Setup(p => p.CanSearch(It.IsAny<SearchProviderQuery>())).Returns(true);
        provider.Setup(p => p.SearchAsync(It.IsAny<SearchProviderQuery>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("search server down"));
        _searchManager.Setup(m => m.GetProviders()).Returns([provider.Object]);

        await CallGetPersons(searchTerm: "seven");

        var query = Assert.Single(_queries);
        Assert.Equal("seven", query.NameContains);
        Assert.Null(query.Names);
    }

    [Fact]
    public async Task GetPersons_ExternalProviderFindsPeople_KeepsItsOrder()
    {
        var simon = AddPerson("Simon Seven");
        var steven = AddPerson("Steven Ito");
        AddExternalProvider(simon, steven);

        // The people query returns them alphabetically. The result follows the provider.
        _peopleItems = [_libraryManager.Object.GetItemById(steven)!, _libraryManager.Object.GetItemById(simon)!];

        var result = await CallGetPersons(searchTerm: "seven");

        var query = Assert.Single(_queries);
        Assert.Null(query.NameContains);
        Assert.Equal(new[] { "Simon Seven", "Steven Ito" }, query.Names);
        Assert.Equal(0, query.Limit);
        Assert.Equal(new[] { "Simon Seven", "Steven Ito" }, result.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task GetPersons_FallbackOffAndExternalProviderFindsNothing_ReturnsEmpty()
    {
        _configuration.EnableDatabaseSearchFallback = false;
        AddExternalProvider();

        var result = await CallGetPersons(searchTerm: "zzqqxx");

        Assert.Empty(_queries);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetPersons_FallbackOffAndExternalProviderFails_SearchesNames()
    {
        _configuration.EnableDatabaseSearchFallback = false;
        var provider = new Mock<IExternalSearchProvider>();
        provider.Setup(p => p.CanSearch(It.IsAny<SearchProviderQuery>())).Returns(true);
        provider.Setup(p => p.SearchAsync(It.IsAny<SearchProviderQuery>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("search server down"));
        _searchManager.Setup(m => m.GetProviders()).Returns([provider.Object]);

        await CallGetPersons(searchTerm: "seven");

        Assert.Equal("seven", Assert.Single(_queries).NameContains);
    }

    private IServerConfigurationManager ConfigurationManager()
    {
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(m => m.Configuration).Returns(_configuration);
        return configurationManager.Object;
    }

    private Guid AddPerson(string name)
    {
        var id = Guid.NewGuid();
        _libraryManager.Setup(m => m.GetItemById(id)).Returns(new Person { Id = id, Name = name });
        return id;
    }

    private void AddExternalProvider(params Guid[] ids)
    {
        var provider = new Mock<IExternalSearchProvider>();
        provider.Setup(p => p.CanSearch(It.IsAny<SearchProviderQuery>())).Returns(true);
        provider.Setup(p => p.SearchAsync(It.IsAny<SearchProviderQuery>(), It.IsAny<CancellationToken>()))
            .Returns(() => Results(ids));
        _searchManager.Setup(m => m.GetProviders()).Returns([provider.Object]);
    }

    private static async IAsyncEnumerable<SearchResult> Results(Guid[] ids, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var id in ids)
        {
            await Task.Yield();
            yield return new SearchResult(id, 100f);
        }
    }

    private async Task<QueryResult<BaseItemDto>> CallGetPersons(string? searchTerm, bool enableTotalRecordCount = true)
    {
        var controller = new PersonsController(_libraryManager.Object, _dtoService.Object, Mock.Of<IUserManager>(), _searchManager.Object, ConfigurationManager(), NullLogger<PersonsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
            }
        };

        var response = await controller.GetPersons(
            startIndex: null,
            limit: 24,
            searchTerm: searchTerm,
            nameStartsWith: null,
            nameLessThan: null,
            nameStartsWithOrGreater: null,
            fields: [],
            filters: [],
            isFavorite: null,
            enableUserData: null,
            imageTypeLimit: null,
            enableImageTypes: [],
            excludePersonTypes: [],
            personTypes: [],
            parentId: null,
            appearsInItemId: null,
            userId: null,
            enableTotalRecordCount: enableTotalRecordCount);

        return response.Value!;
    }
}
