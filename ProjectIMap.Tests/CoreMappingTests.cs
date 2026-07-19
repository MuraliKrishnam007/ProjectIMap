using FluentAssertions;
using ProjectIMap;

namespace ProjectIMap.Tests;

/// <summary>
/// Core mapping behaviour tests — all instances are configured and wired locally
/// without any DI container so each test is fully isolated.
/// </summary>
public sealed class CoreMappingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="Mapper"/> with <c>User → UserDto</c> registered.
    /// Keeping configuration construction in one place means the registration
    /// logic is not repeated in every test and is trivial to extend.
    /// </summary>
    private static Mapper CreateMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();
        return new Mapper(config);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Primitive properties whose names and CLR types match exactly between source
    /// and destination must be copied verbatim by the Phase-1 direct-match path.
    /// </summary>
    [Fact]
    public void Should_Map_Primitive_Properties_Successfully()
    {
        // Arrange
        var mapper = CreateMapper();
        var source = new User
        {
            Id    = 42,
            Name  = "Alice",
            Age   = 30,
            Email = "alice@example.com"
        };

        // Act
        var dest = mapper.Map<User, UserDto>(source);

        // Assert — every directly-matching scalar property is transferred
        dest.Id.Should().Be(source.Id);
        dest.Name.Should().Be(source.Name);
        dest.Age.Should().Be(source.Age);
        dest.Email.Should().Be(source.Email);
    }

    /// <summary>
    /// The Phase-2 trie traversal must resolve <c>User.Address.City</c> to the
    /// flat destination property <c>UserDto.AddressCity</c> (and likewise for all
    /// other <c>Address.*</c> sub-properties) when <c>Address</c> is not null.
    /// </summary>
    [Fact]
    public void Should_Flatten_Nested_Objects_Using_Trie()
    {
        // Arrange
        var mapper = CreateMapper();
        var source = new User
        {
            Id   = 1,
            Name = "Bob",
            Address = new Address
            {
                Street  = "10 Elm St",
                City    = "Springfield",
                Country = "US",
                ZipCode = "62701"
            }
        };

        // Act
        var dest = mapper.Map<User, UserDto>(source);

        // Assert — flattened address properties are present
        dest.AddressStreet.Should().Be(source.Address.Street);
        dest.AddressCity.Should().Be(source.Address.City);
        dest.AddressCountry.Should().Be(source.Address.Country);
        dest.AddressZipCode.Should().Be(source.Address.ZipCode);

        // Scalar properties still arrive correctly alongside flattened ones
        dest.Id.Should().Be(source.Id);
        dest.Name.Should().Be(source.Name);
    }

    /// <summary>
    /// When <c>User.Address</c> is <see langword="null"/> the compiled mapping
    /// delegate must NOT throw a <see cref="NullReferenceException"/>.  The trie
    /// traversal emits a null-guard block expression that short-circuits to
    /// <see langword="null"/> / <c>default</c> for each flattened property.
    /// </summary>
    [Fact]
    public void Should_Handle_Null_Nested_Objects_Safely()
    {
        // Arrange
        var mapper = CreateMapper();
        var source = new User
        {
            Id      = 7,
            Name    = "Carol",
            Age     = 25,
            Address = null   // ← explicitly null nested object
        };

        // Act
        var act = () => mapper.Map<User, UserDto>(source);

        // Assert — no exception is raised ...
        act.Should().NotThrow();

        var dest = act();

        // ... and all flattened properties default to null rather than blowing up
        dest.AddressStreet.Should().BeNull();
        dest.AddressCity.Should().BeNull();
        dest.AddressCountry.Should().BeNull();
        dest.AddressZipCode.Should().BeNull();

        // Scalar properties are still mapped correctly
        dest.Id.Should().Be(source.Id);
        dest.Name.Should().Be(source.Name);
        dest.Age.Should().Be(source.Age);
    }
}
