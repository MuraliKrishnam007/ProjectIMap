using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="MapperConfiguration.AssertConfigurationIsValid"/>:
/// startup-time detection of missing constructors and unmapped, non-ignored
/// destination properties, instead of the first bad mapping failing lazily on
/// its first <c>Map</c> call.
/// </summary>
public sealed class ConfigurationValidationTests
{
    [Fact]
    public void Passes_For_FullyMapped_Pair_Including_Flattened_Properties()
    {
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>(); // AddressStreet/City/Country/ZipCode resolve via trie flatten

        var act = () => config.AssertConfigurationIsValid();

        act.Should().NotThrow();
    }

    [Fact]
    public void Throws_For_Unmapped_Destination_Property()
    {
        var config = new MapperConfiguration();
        // OrderDto.CalculatedTotal has no source counterpart and no ForMember override.
        config.CreateMap<Order, OrderDto>();

        var act = () => config.AssertConfigurationIsValid();

        var ex = act.Should().Throw<MapperConfigurationException>().Which;
        ex.Message.Should().Contain("CalculatedTotal");
    }

    [Fact]
    public void Passes_When_Unmapped_Property_Is_Explicitly_MappedFrom()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.CalculatedTotal, opt => opt.MapFrom(s => s.Price * s.Quantity));

        var act = () => config.AssertConfigurationIsValid();

        act.Should().NotThrow();
    }

    [Fact]
    public void Passes_When_Unmapped_Property_Is_Explicitly_Ignored()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.CalculatedTotal, opt => opt.Ignore());

        var act = () => config.AssertConfigurationIsValid();

        act.Should().NotThrow();
    }

    [Fact]
    public void Throws_For_Missing_Parameterless_Constructor()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Person, PersonDto>(); // PersonDto is a record with no parameterless ctor

        var act = () => config.AssertConfigurationIsValid();

        var ex = act.Should().Throw<MapperConfigurationException>().Which;
        ex.Message.Should().Contain("parameterless");
    }

    [Fact]
    public void Passes_When_ConstructUsing_Is_Registered()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Person, PersonDto>()
              .ConstructUsing(s => new PersonDto(s.FirstName + " " + s.LastName, s.Age));

        var act = () => config.AssertConfigurationIsValid();

        act.Should().NotThrow();
    }

    [Fact]
    public void Aggregates_Errors_Across_Multiple_Pairs()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>();   // missing CalculatedTotal mapping
        config.CreateMap<Person, PersonDto>(); // missing constructor

        var act = () => config.AssertConfigurationIsValid();

        var ex = act.Should().Throw<MapperConfigurationException>().Which;
        ex.Message.Should().Contain("CalculatedTotal");
        ex.Message.Should().Contain("parameterless");
    }

    // ── Recursive validation into nested complex-type pairs ──────────────────

    [Fact]
    public void Passes_For_SelfReferencing_Type_Without_Infinite_Recursion()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Category, CategoryDto>();

        var act = () => config.AssertConfigurationIsValid();

        act.Should().NotThrow(
            because: "the visited-pairs cycle guard must stop recursion into Category.Parent " +
                     "without throwing a StackOverflowException or reporting a false error");
    }

    [Fact]
    public void Recurses_Into_Nested_Complex_Type_And_Reports_Its_Unmapped_Property()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Container, ContainerDto>();
        // Person -> PersonInnerDto is not explicitly registered; validation must
        // still recurse into it (it's constructible) and find Nickname unmapped.

        var act = () => config.AssertConfigurationIsValid();

        var ex = act.Should().Throw<MapperConfigurationException>().Which;
        ex.Message.Should().Contain("Item");
        ex.Message.Should().Contain("Nickname");
    }

    [Fact]
    public void Throws_When_Nested_Property_Names_Match_But_Types_Are_Incompatible_And_Not_Constructible()
    {
        var config = new MapperConfiguration();
        // Company.Owner: Person -> CompanyDto.Owner: PersonDto — PersonDto has no
        // parameterless ctor and no ConstructUsing is registered for the pair.
        config.CreateMap<Company, CompanyDto>();

        var act = () => config.AssertConfigurationIsValid();

        var ex = act.Should().Throw<MapperConfigurationException>().Which;
        ex.Message.Should().Contain("Owner");
    }
}
