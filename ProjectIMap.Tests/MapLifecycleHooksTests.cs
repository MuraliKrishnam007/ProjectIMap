using System.Collections.Generic;
using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="IMappingExpression{TSource,TDestination}.BeforeMap"/> and
/// <see cref="IMappingExpression{TSource,TDestination}.AfterMap"/>: per-type-pair
/// lifecycle hooks that switch a pair from the zero-overhead <c>MemberInit</c>
/// path to a construct-then-assign pipeline.
/// </summary>
public sealed class MapLifecycleHooksTests
{
    [Fact]
    public void BeforeMap_Runs_On_Constructed_But_NotYetPopulated_Destination()
    {
        var config = new MapperConfiguration();
        var beforeSawEmptyName = false;

        config.CreateMap<Order, OrderDto>()
              .BeforeMap((_, dest) => beforeSawEmptyName = dest.Name == string.Empty);

        var mapper = new Mapper(config);
        mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "Widget" });

        beforeSawEmptyName.Should().BeTrue(
            because: "BeforeMap runs on the freshly constructed, not-yet-populated destination");
    }

    [Fact]
    public void AfterMap_Runs_After_Convention_Assignment_And_Observes_Mapped_Values()
    {
        var config = new MapperConfiguration();
        var seenName = string.Empty;

        config.CreateMap<Order, OrderDto>()
              .AfterMap((_, dest) => seenName = dest.Name);

        var mapper = new Mapper(config);
        mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "Gadget" });

        seenName.Should().Be("Gadget");
    }

    [Fact]
    public void BeforeMap_And_AfterMap_Both_Fire_In_Order()
    {
        var config = new MapperConfiguration();
        var events  = new List<string>();

        config.CreateMap<Order, OrderDto>()
              .BeforeMap((_, _) => events.Add("before"))
              .AfterMap((_, _) => events.Add("after"));

        var mapper = new Mapper(config);
        mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "Thing" });

        events.Should().Equal("before", "after");
    }

    [Fact]
    public void Hooks_Also_Fire_For_Map_Into_Existing_Instance()
    {
        var config = new MapperConfiguration();
        var events  = new List<string>();

        config.CreateMap<Order, OrderDto>()
              .BeforeMap((_, _) => events.Add("before"))
              .AfterMap((_, _) => events.Add("after"));

        var mapper = new Mapper(config);
        mapper.Map(new Order { Id = 1, Name = "Thing" }, new OrderDto());

        events.Should().Equal("before", "after");
    }

    [Fact]
    public void Pairs_Without_Hooks_Are_Unaffected()
    {
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();
        var mapper = new Mapper(config);

        var dto = mapper.Map<User, UserDto>(new User { Id = 1, Name = "Plain" });

        dto.Id.Should().Be(1);
        dto.Name.Should().Be("Plain");
    }
}
