using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// Every test that touches the database belongs to this group.
/// </summary>
/// <remarks>
/// xunit runs test classes in parallel, and these share one Postgres: each truncates the tables
/// it uses, so run concurrently they delete each other's rows and fail in ways that look exactly
/// like query bugs - which is how this was found. One collection makes them sequential. Worth
/// the wall-clock: the alternative is a schema per test, and the point of these is that they run
/// against the real thing.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DatabaseTestGroup
{
    public const string Name = "database";
}
