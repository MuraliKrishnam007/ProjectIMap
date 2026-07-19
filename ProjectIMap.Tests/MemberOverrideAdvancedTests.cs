using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="IMemberConfigurationExpression{TSource}.Condition"/> and
/// <see cref="IMemberConfigurationExpression{TSource}.NullSubstitute{TMember}"/>
/// for the fresh-instance <see cref="IMapper.Map{TSource,TDestination}(TSource)"/>
/// path. (The existing-instance "true skip" semantics of <c>Condition</c> are
/// covered separately in <see cref="ExistingInstanceMappingTests"/>.)
/// </summary>
public sealed class MemberOverrideAdvancedTests
{
    // ── Condition ─────────────────────────────────────────────────────────────

    [Fact]
    public void Condition_False_Yields_Clr_Default_On_Freshly_Constructed_Instance()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.Name, opt => opt.Condition(s => s.Quantity > 0));
        var mapper = new Mapper(config);

        var dto = mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "Hidden", Quantity = 0 });

        dto.Name.Should().BeNull(
            because: "a freshly constructed destination has no prior value to preserve — " +
                     "Condition can only choose between the source value and the CLR default");
    }

    [Fact]
    public void Condition_True_Maps_Value_On_New_Instance()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.Name, opt => opt.Condition(s => s.Quantity > 0));
        var mapper = new Mapper(config);

        var dto = mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "Visible", Quantity = 3 });

        dto.Name.Should().Be("Visible");
    }

    [Fact]
    public void Condition_Combines_With_MapFrom()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.CalculatedTotal, opt =>
              {
                  opt.MapFrom(s => s.Price * s.Quantity);
                  opt.Condition(s => s.Quantity > 0);
              });
        var mapper = new Mapper(config);

        var zeroQty = mapper.Map<Order, OrderDto>(new Order { Id = 1, Price = 10m, Quantity = 0 });
        var withQty = mapper.Map<Order, OrderDto>(new Order { Id = 2, Price = 10m, Quantity = 4 });

        zeroQty.CalculatedTotal.Should().Be(0m);
        withQty.CalculatedTotal.Should().Be(40m);
    }

    // ── NullSubstitute ────────────────────────────────────────────────────────

    [Fact]
    public void NullSubstitute_Replaces_Null_Source_Value()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.Name, opt => opt.NullSubstitute("Unnamed"));
        var mapper = new Mapper(config);

        var dto = mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = null!, Quantity = 1 });

        dto.Name.Should().Be("Unnamed");
    }

    [Fact]
    public void NullSubstitute_Does_Not_Affect_NonNull_Source_Value()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.Name, opt => opt.NullSubstitute("Unnamed"));
        var mapper = new Mapper(config);

        var dto = mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "Real", Quantity = 1 });

        dto.Name.Should().Be("Real");
    }

    [Fact]
    public void NullSubstitute_Combines_With_MapFrom()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.Name, opt =>
              {
                  opt.MapFrom(s => s.Name);
                  opt.NullSubstitute("Fallback");
              });
        var mapper = new Mapper(config);

        var dto = mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = null!, Quantity = 1 });

        dto.Name.Should().Be("Fallback");
    }
}
