using System;
using System.Collections.Generic;
using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="IMapper.Map{TSource,TDestination}(TSource,TDestination)"/>:
/// mapping onto an already-constructed destination instance instead of allocating
/// a new one, and how <c>Condition</c> behaves differently in this mode (a true
/// skip, preserving the existing destination value).
/// </summary>
public sealed class ExistingInstanceMappingTests
{
    private static Mapper CreateUserMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();
        return new Mapper(config);
    }

    [Fact]
    public void Should_Map_Into_Existing_Instance_And_Return_Same_Reference()
    {
        var mapper      = CreateUserMapper();
        var source      = new User { Id = 1, Name = "Alice", Age = 30, Email = "a@x.com" };
        var destination = new UserDto();

        var result = mapper.Map(source, destination);

        result.Should().BeSameAs(destination);
        destination.Id.Should().Be(1);
        destination.Name.Should().Be("Alice");
        destination.Age.Should().Be(30);
        destination.Email.Should().Be("a@x.com");
    }

    [Fact]
    public void Should_Overwrite_Existing_Values_On_Destination()
    {
        var mapper      = CreateUserMapper();
        var source      = new User { Id = 2, Name = "Bob", Age = 40, Email = "b@x.com" };
        var destination = new UserDto { Id = 999, Name = "Stale", Age = 1, Email = "stale@x.com" };

        mapper.Map(source, destination);

        destination.Id.Should().Be(2);
        destination.Name.Should().Be("Bob");
        destination.Age.Should().Be(40);
        destination.Email.Should().Be("b@x.com");
    }

    [Fact]
    public void Map_Into_Existing_Instance_Also_Supports_Collection_Types()
    {
        // Full coverage (replace semantics, empty source, array-destination failure
        // mode, etc.) lives in CollectionMergeTests; this is a quick sanity check
        // that the overload dispatches to the collection-merge path at all.
        var mapper       = CreateUserMapper();
        var sources      = new List<User> { new() { Id = 1, Name = "Alice" } };
        var destinations = new List<UserDto> { new() { Id = 999, Name = "Stale" } };

        mapper.Map(sources, destinations);

        destinations.Should().HaveCount(1);
        destinations[0].Id.Should().Be(1);
        destinations[0].Name.Should().Be("Alice");
    }

    [Fact]
    public void Should_Throw_On_Null_Source_Or_Destination()
    {
        var mapper = CreateUserMapper();

        var actNullSource = () => mapper.Map<User, UserDto>(null!, new UserDto());
        var actNullDest   = () => mapper.Map(new User(), (UserDto)null!);

        actNullSource.Should().Throw<ArgumentNullException>();
        actNullDest.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Condition_False_Leaves_Existing_Destination_Value_Untouched()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.Name, opt => opt.Condition(s => s.Quantity > 0));
        var mapper = new Mapper(config);

        var destination = new OrderDto { Name = "Preserved" };
        var source       = new Order { Id = 1, Name = "ShouldNotApply", Quantity = 0 };

        mapper.Map(source, destination);

        destination.Name.Should().Be("Preserved",
            because: "mapping into an existing instance with a false Condition must skip the assignment entirely");
    }

    [Fact]
    public void Condition_True_Overwrites_Destination_Value()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.Name, opt => opt.Condition(s => s.Quantity > 0));
        var mapper = new Mapper(config);

        var destination = new OrderDto { Name = "Old" };
        var source       = new Order { Id = 1, Name = "NewName", Quantity = 5 };

        mapper.Map(source, destination);

        destination.Name.Should().Be("NewName");
    }
}
