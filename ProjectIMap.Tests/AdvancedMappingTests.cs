using FluentAssertions;
using ProjectIMap;

namespace ProjectIMap.Tests;

/// <summary>
/// Tests for algorithms and edge cases: DFS cycle detection, collection
/// mapping, and empty-collection safety.
/// All instances are configured locally — no DI container involved.
/// </summary>
public sealed class AdvancedMappingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an isolated <see cref="Mapper"/> with <c>Category → CategoryDto</c>
    /// registered for the cycle-detection tests.
    /// </summary>
    private static Mapper CreateCategoryMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Category, CategoryDto>();
        return new Mapper(config);
    }

    /// <summary>
    /// Creates an isolated <see cref="Mapper"/> with <c>User → UserDto</c>
    /// registered for the collection mapping tests.
    /// </summary>
    private static Mapper CreateUserMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();
        return new Mapper(config);
    }

    // ── Cycle-detection tests ─────────────────────────────────────────────────

    /// <summary>
    /// A type that directly references itself (e.g. <c>Category.Parent = category</c>)
    /// forms a back-edge in the DFS traversal.  The engine must detect it, emit
    /// <see langword="null"/> for the cyclic property, and return normally — a
    /// <see cref="StackOverflowException"/> must never escape.
    /// </summary>
    [Fact]
    public void Should_Detect_Cycles_And_Prevent_StackOverflow()
    {
        // Arrange
        var mapper = CreateCategoryMapper();

        var root = new Category { Id = 1, Name = "Root" };
        // Direct self-reference: root.Parent == root creates an infinite type cycle.
        root.Parent = root;

        // Act — must not throw StackOverflowException (or any other exception)
        var act = () => mapper.Map<Category, CategoryDto>(root);

        act.Should().NotThrow(
            because: "the DFS back-edge guard must break the cycle and return null for the cyclic property");

        var dest = act();

        // Assert — scalar properties are still mapped correctly
        dest.Id.Should().Be(root.Id);
        dest.Name.Should().Be(root.Name);

        // The cyclic back-edge must have been nulled out, not recursed into
        dest.Parent.Should().BeNull(
            because: "the DFS guard emits null when it detects a back-edge in the type graph");
    }

    /// <summary>
    /// <c>.MaxDepth(n)</c> overrides the default depth-1 cutoff (the behaviour
    /// verified above) to allow real, bounded recursion through a self-referencing
    /// type — e.g. walking a <c>Category.Parent</c> chain N levels deep before
    /// nulling out the next level, regardless of how much further the actual data goes.
    /// </summary>
    [Fact]
    public void MaxDepth_Allows_Configured_Levels_Of_SelfReferencing_Recursion()
    {
        // Arrange
        var config = new MapperConfiguration();
        config.CreateMap<Category, CategoryDto>().MaxDepth(3);
        var mapper = new Mapper(config);

        // Four generations deep — MaxDepth(3) should populate two Parent hops
        // beyond the root and null out the third, even though real data exists there.
        var greatGrandparent = new Category { Id = 0, Name = "GreatGrandparent" };
        var grandparent      = new Category { Id = 1, Name = "Grandparent", Parent = greatGrandparent };
        var parent           = new Category { Id = 2, Name = "Parent",      Parent = grandparent };
        var root             = new Category { Id = 3, Name = "Root",       Parent = parent };

        // Act
        var dest = mapper.Map<Category, CategoryDto>(root);

        // Assert
        dest.Parent.Should().NotBeNull();
        dest.Parent!.Name.Should().Be("Parent");
        dest.Parent.Parent.Should().NotBeNull();
        dest.Parent.Parent!.Name.Should().Be("Grandparent");
        dest.Parent.Parent.Parent.Should().BeNull(
            because: "MaxDepth(3) allows two levels of real Parent recursion before nulling the third, " +
                     "regardless of whether further ancestor data actually exists");
    }

    // ── Collection mapping tests ──────────────────────────────────────────────

    /// <summary>
    /// Mapping <c>List&lt;User&gt;</c> to <c>List&lt;UserDto&gt;</c> must produce
    /// a list of equal length where each element is independently mapped with all
    /// scalar and flattened properties transferred correctly.
    /// The element-level <c>User → UserDto</c> delegate is compiled once and reused
    /// for every element; the collection mapper itself is also cached.
    /// </summary>
    [Fact]
    public void Should_Map_Collections_Successfully()
    {
        // Arrange
        var mapper = CreateUserMapper();
        var sources = new List<User>
        {
            new() { Id = 1, Name = "Alice", Age = 28, Email = "alice@test.com",
                    Address = new Address { City = "London",   Country = "UK" } },
            new() { Id = 2, Name = "Bob",   Age = 34, Email = "bob@test.com",
                    Address = new Address { City = "New York", Country = "US" } },
            new() { Id = 3, Name = "Carol", Age = 22, Email = "carol@test.com",
                    Address = null }
        };

        // Act
        var dests = mapper.Map<List<User>, List<UserDto>>(sources);

        // Assert — correct element count
        dests.Should().HaveCount(3);

        // Each element is individually verified
        for (var i = 0; i < sources.Count; i++)
        {
            dests[i].Id.Should().Be(sources[i].Id);
            dests[i].Name.Should().Be(sources[i].Name);
            dests[i].Age.Should().Be(sources[i].Age);
            dests[i].Email.Should().Be(sources[i].Email);
            dests[i].AddressCity.Should().Be(sources[i].Address?.City);
            dests[i].AddressCountry.Should().Be(sources[i].Address?.Country);
        }
    }

    /// <summary>
    /// Mapping an empty <c>List&lt;User&gt;</c> must return an empty
    /// <c>List&lt;UserDto&gt;</c> without throwing any exception.
    /// The compiled delegate must guard against zero-element input the same way
    /// LINQ's <c>Select().ToList()</c> naturally does.
    /// </summary>
    [Fact]
    public void Should_Map_Empty_Collections_Without_Errors()
    {
        // Arrange
        var mapper  = CreateUserMapper();
        var sources = new List<User>();    // intentionally empty

        // Act
        var act  = () => mapper.Map<List<User>, List<UserDto>>(sources);

        // Assert — no exception
        act.Should().NotThrow();

        var dests = act();

        dests.Should().NotBeNull()
             .And.BeEmpty(because: "mapping an empty source list must yield an empty destination list");
    }
}
