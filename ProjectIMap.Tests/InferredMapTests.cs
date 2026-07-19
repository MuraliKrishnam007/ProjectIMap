using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates the boilerplate-free <see cref="IMapper.Map{TDestination}(object)"/>
/// overload: the source type is inferred from the object's runtime type, resolved
/// to a registered pair once per (runtime type, destination) combination, and
/// dispatched through the same compiled pipeline as the explicit two-generic form.
/// </summary>
public sealed class InferredMapTests
{
    private static Mapper CreateMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.CalculatedTotal, opt => opt.MapFrom(s => s.Price * s.Quantity));
        return new Mapper(config);
    }

    [Fact]
    public void Infers_Source_Type_From_Runtime_Object()
    {
        var mapper = CreateMapper();

        var dto = mapper.Map<UserDto>(new User { Id = 7, Name = "Ada" });

        dto.Id.Should().Be(7);
        dto.Name.Should().Be("Ada");
    }

    [Fact]
    public void Runs_The_Full_Pipeline_Including_ForMember_Overrides()
    {
        var mapper = CreateMapper();

        var dto = mapper.Map<OrderDto>(new Order { Id = 1, Name = "Widget", Price = 5m, Quantity = 3 });

        dto.CalculatedTotal.Should().Be(15m,
            because: "the inferred overload dispatches into the same compiled pipeline, overrides included");
    }

    [Fact]
    public void Produces_Identical_Result_To_The_Explicit_TwoGeneric_Overload()
    {
        var mapper = CreateMapper();
        var source = new Order { Id = 2, Name = "Gadget", Price = 4m, Quantity = 2 };

        var inferred = mapper.Map<OrderDto>(source);
        var explicit_ = mapper.Map<Order, OrderDto>(source);

        inferred.Should().BeEquivalentTo(explicit_);
    }

    // ── Collections ──────────────────────────────────────────────────────────

    [Fact]
    public void Infers_Collection_Element_Pair_For_A_List()
    {
        var mapper = CreateMapper();
        var users  = new List<User> { new() { Id = 1, Name = "A" }, new() { Id = 2, Name = "B" } };

        var dtos = mapper.Map<List<UserDto>>(users);

        dtos.Should().HaveCount(2);
        dtos.Select(d => d.Name).Should().Equal("A", "B");
    }

    [Fact]
    public void Infers_Collection_Pair_Even_For_A_Deferred_Linq_Sequence()
    {
        var mapper = CreateMapper();
        var users  = new List<User> { new() { Id = 1, Name = "A" }, new() { Id = 2, Name = "B" } };
        IEnumerable<User> deferred = users.Where(u => u.Id > 1);   // runtime type is a LINQ iterator

        var dtos = mapper.Map<List<UserDto>>(deferred);

        dtos.Should().ContainSingle().Which.Name.Should().Be("B");
    }

    // ── Polymorphism ─────────────────────────────────────────────────────────

    private static Mapper CreateAnimalMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Animal, AnimalDto>()
              .Include<Dog, DogDto>()
              .Include<Cat, CatDto>();
        config.CreateMap<Dog, DogDto>();
        config.CreateMap<Cat, CatDto>();
        return new Mapper(config);
    }

    [Fact]
    public void Falls_Back_To_The_Registered_Base_Pair_For_A_Derived_Instance()
    {
        // (Dog, AnimalDto) is not registered — only (Animal, AnimalDto) with
        // Include<Dog, DogDto>. The inferred overload must walk up to Animal and
        // let the existing polymorphic machinery produce a DogDto.
        var mapper = CreateAnimalMapper();
        object dog = new Dog { Name = "Rex", Breed = "Labrador" };

        var dto = mapper.Map<AnimalDto>(dog);

        dto.Should().BeOfType<DogDto>();
        ((DogDto)dto).Breed.Should().Be("Labrador");
    }

    [Fact]
    public void Uses_The_Exact_Pair_When_The_Runtime_Type_Is_Registered_Directly()
    {
        var mapper = CreateAnimalMapper();

        var dto = mapper.Map<DogDto>(new Dog { Name = "Rex", Breed = "Labrador" });

        dto.Breed.Should().Be("Labrador");
    }

    // ── Errors ───────────────────────────────────────────────────────────────

    [Fact]
    public void Throws_ArgumentNull_For_Null_Source()
    {
        var mapper = CreateMapper();

        var act = () => mapper.Map<UserDto>(null!);

        act.Should().Throw<ArgumentNullException>()
           .WithMessage("*runtime type*");
    }

    [Fact]
    public void Throws_With_The_Runtime_Type_Name_When_No_Pair_Is_Registered()
    {
        var mapper = CreateMapper();   // no (Employee, EmployeeDto) registration

        var act = () => mapper.Map<EmployeeDto>(new Employee { FirstName = "Ada" });

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*No mapping registered*Employee*");
    }
}
