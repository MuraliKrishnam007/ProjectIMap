using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="IMappingExpression{TSource,TDestination}.Include{TDerivedSource,TDerivedDestination}"/>:
/// runtime-type-based dispatch to a derived pair's own mapping for direct
/// top-level <see cref="IMapper.Map{TSource,TDestination}(TSource)"/> calls.
/// </summary>
public sealed class PolymorphicMappingTests
{
    private static Mapper CreateAnimalMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Animal, AnimalDto>()
              .Include<Dog, DogDto>()
              .Include<Cat, CatDto>();
        config.CreateMap<Dog, DogDto>();
        config.CreateMap<Cat, CatDto>();
        config.CreateMap<AnimalOwner, AnimalOwnerDto>();
        return new Mapper(config);
    }

    [Fact]
    public void Dispatches_To_Derived_Mapping_When_Runtime_Type_Is_A_Registered_Subtype()
    {
        var mapper = CreateAnimalMapper();
        Animal dog = new Dog { Name = "Rex", Breed = "Labrador" };

        var dto = mapper.Map<Animal, AnimalDto>(dog);

        dto.Should().BeOfType<DogDto>();
        dto.Name.Should().Be("Rex");
        ((DogDto)dto).Breed.Should().Be("Labrador");
    }

    [Fact]
    public void Dispatches_To_A_Different_Registered_Subtype()
    {
        var mapper = CreateAnimalMapper();
        Animal cat = new Cat { Name = "Whiskers", Indoor = true };

        var dto = mapper.Map<Animal, AnimalDto>(cat);

        dto.Should().BeOfType<CatDto>();
        dto.Name.Should().Be("Whiskers");
        ((CatDto)dto).Indoor.Should().BeTrue();
    }

    [Fact]
    public void Uses_Base_Mapping_For_A_PlainBaseType_Instance()
    {
        var mapper = CreateAnimalMapper();
        Animal plain = new Animal { Name = "Generic" };

        var dto = mapper.Map<Animal, AnimalDto>(plain);

        dto.Should().BeOfType<AnimalDto>();
        dto.Name.Should().Be("Generic");
    }

    [Fact]
    public void Throws_When_Included_Derived_Pair_Was_Never_Registered()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Animal, AnimalDto>()
              .Include<Dog, DogDto>();
        // deliberately omit config.CreateMap<Dog, DogDto>()
        var mapper = new Mapper(config);

        Animal dog = new Dog { Name = "Rex" };
        var act = () => mapper.Map<Animal, AnimalDto>(dog);

        act.Should().Throw<System.InvalidOperationException>()
           .WithMessage("*No mapping registered*");
    }

    // ── Collection-level polymorphic dispatch ────────────────────────────────

    [Fact]
    public void Collection_Mapping_Dispatches_Each_Element_On_Its_Runtime_Type()
    {
        var mapper = CreateAnimalMapper();
        var animals = new List<Animal>
        {
            new Dog { Name = "Rex", Breed = "Labrador" },
            new Cat { Name = "Whiskers", Indoor = true },
            new Animal { Name = "Generic" }
        };

        var dtos = mapper.Map<List<Animal>, List<AnimalDto>>(animals);

        dtos[0].Should().BeOfType<DogDto>();
        ((DogDto)dtos[0]).Breed.Should().Be("Labrador");
        dtos[1].Should().BeOfType<CatDto>();
        ((CatDto)dtos[1]).Indoor.Should().BeTrue();
        dtos[2].Should().BeOfType<AnimalDto>(because: "a genuine base instance keeps the base mapping");
    }

    [Fact]
    public void Collection_Merge_Into_Existing_List_Also_Dispatches_Polymorphically()
    {
        var mapper  = CreateAnimalMapper();
        var animals = new List<Animal> { new Dog { Name = "Rex", Breed = "Labrador" } };
        var dest    = new List<AnimalDto>();

        mapper.Map(animals, dest);

        dest.Should().ContainSingle().Which.Should().BeOfType<DogDto>();
        ((DogDto)dest[0]).Breed.Should().Be("Labrador");
    }

    // ── Nested-member polymorphic dispatch ───────────────────────────────────

    [Fact]
    public void Nested_Member_Dispatches_On_Its_Runtime_Type()
    {
        var mapper = CreateAnimalMapper();
        var owner  = new AnimalOwner { Name = "Ada", Pet = new Dog { Name = "Rex", Breed = "Labrador" } };

        var dto = mapper.Map<AnimalOwner, AnimalOwnerDto>(owner);

        dto.Name.Should().Be("Ada");
        dto.Pet.Should().BeOfType<DogDto>();
        ((DogDto)dto.Pet!).Breed.Should().Be("Labrador");
    }

    [Fact]
    public void Nested_Member_Uses_Base_Mapping_For_A_Base_Instance()
    {
        var mapper = CreateAnimalMapper();
        var owner  = new AnimalOwner { Name = "Ada", Pet = new Animal { Name = "Generic" } };

        var dto = mapper.Map<AnimalOwner, AnimalOwnerDto>(owner);

        dto.Pet.Should().BeOfType<AnimalDto>();
        dto.Pet!.Name.Should().Be("Generic");
    }

    [Fact]
    public void Nested_Null_Member_Maps_To_Null()
    {
        var mapper = CreateAnimalMapper();
        var owner  = new AnimalOwner { Name = "Ada", Pet = null };

        var dto = mapper.Map<AnimalOwner, AnimalOwnerDto>(owner);

        dto.Name.Should().Be("Ada");
        dto.Pet.Should().BeNull();
    }
}
