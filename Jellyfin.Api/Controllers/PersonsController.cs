using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.ModelBinders;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Persons controller.
/// </summary>
[Authorize]
[Tags("Person")]
public class PersonsController : BaseJellyfinApiController
{
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;
    private readonly ISearchManager _searchManager;
    private readonly ILogger<PersonsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="searchManager">Instance of the <see cref="ISearchManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PersonsController}"/> interface.</param>
    public PersonsController(
        ILibraryManager libraryManager,
        IDtoService dtoService,
        IUserManager userManager,
        ISearchManager searchManager,
        ILogger<PersonsController> logger)
    {
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _userManager = userManager;
        _searchManager = searchManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets all persons.
    /// </summary>
    /// <param name="startIndex">Optional. All items with a lower index will be dropped from the response.</param>
    /// <param name="limit">Optional. The maximum number of records to return.</param>
    /// <param name="searchTerm">The search term.</param>
    /// <param name="nameStartsWith">Optional. Filter by items whose name starts with the given input string.</param>
    /// <param name="nameLessThan">Optional. Filter by items whose name will appear before this value when sorted alphabetically.</param>
    /// <param name="nameStartsWithOrGreater">Optional. Filter by items whose name will appear after this value when sorted alphabetically.</param>
    /// <param name="fields">Optional. Specify additional fields of information to return in the output.</param>
    /// <param name="filters">Optional. Specify additional filters to apply.</param>
    /// <param name="isFavorite">Optional filter by items that are marked as favorite, or not. userId is required.</param>
    /// <param name="enableUserData">Optional, include user data.</param>
    /// <param name="imageTypeLimit">Optional, the max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="excludePersonTypes">Optional. If specified results will be filtered to exclude those containing the specified PersonType. Allows multiple, comma-delimited.</param>
    /// <param name="personTypes">Optional. If specified results will be filtered to include only those containing the specified PersonType. Allows multiple, comma-delimited.</param>
    /// <param name="parentId">Optional. Specify this to localize the search to a specific library. Omit to use the root.</param>
    /// <param name="appearsInItemId">Optional. If specified, person results will be filtered on items related to said persons.</param>
    /// <param name="userId">User id.</param>
    /// <param name="enableTotalRecordCount">Optional. Enable the total record count.</param>
    /// <param name="enableImages">Optional, include image information in output.</param>
    /// <response code="200">Persons returned.</response>
    /// <returns>An <see cref="OkResult"/> containing the queryresult of persons.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetPersons(
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] string? searchTerm,
        [FromQuery] string? nameStartsWith,
        [FromQuery] string? nameLessThan,
        [FromQuery] string? nameStartsWithOrGreater,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFilter[] filters,
        [FromQuery] bool? isFavorite,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] string[] excludePersonTypes,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] string[] personTypes,
        [FromQuery] Guid? parentId,
        [FromQuery] Guid? appearsInItemId,
        [FromQuery] Guid? userId,
        [FromQuery] bool enableTotalRecordCount = true,
        [FromQuery] bool? enableImages = true)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);

        User? user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);

        var isFavoriteInFilters = filters.Any(f => f == ItemFilter.IsFavorite);

        // With a search term, external search providers (e.g. Meilisearch) find the names first. The people query
        // below then applies person types, favorites and access to those names, and keeps the providers' order.
        var searchedNames = string.IsNullOrWhiteSpace(searchTerm)
            ? null
            : await SearchPeopleNamesAsync(searchTerm, userId, (startIndex ?? 0) + (limit ?? 50), HttpContext.RequestAborted).ConfigureAwait(false);

        var peopleItems = _libraryManager.GetPeopleItems(new InternalPeopleQuery(
            personTypes,
            excludePersonTypes)
        {
            AccessFilter = BuildAccessFilter(user),
            NameContains = searchedNames is null ? searchTerm : null,
            Names = searchedNames,
            NameStartsWith = nameStartsWith,
            NameLessThan = nameLessThan,
            NameStartsWithOrGreater = nameStartsWithOrGreater,
            User = user,
            IsFavorite = !isFavorite.HasValue && isFavoriteInFilters ? true : isFavorite,
            AppearsInItemId = appearsInItemId ?? Guid.Empty,
            ParentId = parentId,
            StartIndex = searchedNames is null ? startIndex : null,
            Limit = searchedNames is null ? limit ?? 0 : 0,
            EnableTotalRecordCount = enableTotalRecordCount && searchedNames is null
        });

        if (searchedNames is not null)
        {
            // Back into the providers' order, then page. The total is what the providers found and the filters kept.
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < searchedNames.Count; i++)
            {
                rank.TryAdd(searchedNames[i], i);
            }

            var ordered = peopleItems.Items.OrderBy(person => rank.GetValueOrDefault(person.Name, int.MaxValue)).ToList();
            var page = ordered.Skip(startIndex ?? 0).Take(limit ?? ordered.Count);
            return new QueryResult<BaseItemDto>(
                startIndex ?? 0,
                enableTotalRecordCount ? ordered.Count : 0,
                page.Select(person => _dtoService.GetItemByNameDto(person, dtoOptions, null, user)).ToArray());
        }

        return new QueryResult<BaseItemDto>(
            peopleItems.StartIndex,
            peopleItems.TotalRecordCount,
            peopleItems.Items
                .Select(person => _dtoService.GetItemByNameDto(person, dtoOptions, null, user))
                .ToArray());
    }

    // Names of the people the external search providers find, best first, or null when none of them can search or
    // they find nothing. Only external providers: the internal one scans the database, which the people query does
    // faster by itself.
    private async Task<IReadOnlyList<string>?> SearchPeopleNamesAsync(string searchTerm, Guid? userId, int wanted, CancellationToken cancellationToken)
    {
        var query = new SearchProviderQuery
        {
            SearchTerm = searchTerm,
            UserId = userId,
            IncludeItemTypes = [BaseItemKind.Person],
            Limit = Math.Max(wanted, 50) * 2
        };

        var names = new List<string>();
        foreach (var provider in _searchManager.GetProviders().OfType<IExternalSearchProvider>())
        {
            if (!provider.CanSearch(query))
            {
                continue;
            }

            try
            {
                await foreach (var result in provider.SearchAsync(query, cancellationToken).ConfigureAwait(false))
                {
                    if (_libraryManager.GetItemById(result.ItemId) is Person person)
                    {
                        names.Add(person.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failing provider must not fail the request: the name search below still works.
                _logger.LogWarning(ex, "Search provider {Provider} failed for people search '{SearchTerm}'", provider.Name, searchTerm);
                names.Clear();
                continue;
            }

            if (names.Count > 0)
            {
                return names;
            }
        }

        return null;
    }

    // People are not owned by a library, so nothing in the Peoples table says which of them a user is
    // allowed to see; that only follows from the items they are credited on.
    private InternalItemsQuery? BuildAccessFilter(User? user)
    {
        if (user is null || !user.HasContentRestrictions())
        {
            return null;
        }

        var accessFilter = new InternalItemsQuery(user) { IncludeOwnedItems = true };
        _libraryManager.ConfigureUserAccess(accessFilter, user);
        return accessFilter;
    }

    /// <summary>
    /// Get person by name.
    /// </summary>
    /// <param name="name">Person name.</param>
    /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
    /// <response code="200">Person returned.</response>
    /// <response code="404">Person not found.</response>
    /// <returns>An <see cref="OkResult"/> containing the person on success,
    /// or a <see cref="NotFoundResult"/> if person not found.</returns>
    [HttpGet("{name}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BaseItemDto> GetPerson([FromRoute, Required] string name, [FromQuery] Guid? userId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var dtoOptions = new DtoOptions();

        var item = _libraryManager.GetPerson(name);
        if (item is null)
        {
            return NotFound();
        }

        if (!userId.IsNullOrEmpty())
        {
            var user = _userManager.GetUserById(userId.Value);
            return _dtoService.GetBaseItemDto(item, dtoOptions, user);
        }

        return _dtoService.GetBaseItemDto(item, dtoOptions);
    }
}
