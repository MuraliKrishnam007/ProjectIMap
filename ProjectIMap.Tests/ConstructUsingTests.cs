using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="IMappingExpression{TSource,TDestination}.ConstructUsing"/>:
/// mapping to destination types with no public parameterless constructor (e.g.
/// C# records with a primary constructor), both at the top level and nested.
/// </summary>
public sealed class ConstructUsingTests
{
    [Fact]
    public void Should_Map_To_Record_Type_Using_ConstructUsing()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Person, PersonDto>()
              .ConstructUsing(s => new PersonDto(s.FirstName + " " + s.LastName, s.Age));
        var mapper = new Mapper(config);

        var person = new Person { FirstName = "Ada", LastName = "Lovelace", Age = 36 };
        var dto    = mapper.Map<Person, PersonDto>(person);

        dto.FullName.Should().Be("Ada Lovelace");
        dto.Age.Should().Be(36);
    }

    [Fact]
    public void Should_Throw_When_No_ParameterlessCtor_And_No_ConstructUsing_Registered()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Person, PersonDto>(); // no ConstructUsing — PersonDto has no parameterless ctor
        var mapper = new Mapper(config);

        var act = () => mapper.Map<Person, PersonDto>(new Person());

        act.Should().Throw<System.Exception>(
            because: "PersonDto has no public parameterless constructor and none was substituted");
    }

    [Fact]
    public void ConstructUsing_Applies_To_Nested_Property_Mapping()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Person, PersonDto>()
              .ConstructUsing(s => new PersonDto(s.FirstName + " " + s.LastName, s.Age));
        config.CreateMap<Company, CompanyDto>();
        var mapper = new Mapper(config);

        var company = new Company
        {
            Name  = "Acme",
            Owner = new Person { FirstName = "Ada", LastName = "Lovelace", Age = 36 }
        };

        var dto = mapper.Map<Company, CompanyDto>(company);

        dto.Name.Should().Be("Acme");
        dto.Owner.Should().NotBeNull();
        dto.Owner!.FullName.Should().Be("Ada Lovelace");
        dto.Owner.Age.Should().Be(36);
    }

    [Fact]
    public void ConstructUsing_Nested_Property_Is_Null_When_Source_Nested_Property_Is_Null()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Person, PersonDto>()
              .ConstructUsing(s => new PersonDto(s.FirstName + " " + s.LastName, s.Age));
        config.CreateMap<Company, CompanyDto>();
        var mapper = new Mapper(config);

        var company = new Company { Name = "NoOwner", Owner = null };

        var dto = mapper.Map<Company, CompanyDto>(company);

        dto.Name.Should().Be("NoOwner");
        dto.Owner.Should().BeNull(
            because: "the null-guard around a ConstructUsing-produced nested object must still apply");
    }
}
