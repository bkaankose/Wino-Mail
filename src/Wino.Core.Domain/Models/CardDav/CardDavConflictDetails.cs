using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.CardDav;

public sealed record CardDavFieldDifference(string FieldKey, string LocalValue, string ServerValue);

public sealed record CardDavConflictDetails(
    CardDavConflict Conflict,
    string ContactDisplayName,
    IReadOnlyList<CardDavFieldDifference> Differences);
