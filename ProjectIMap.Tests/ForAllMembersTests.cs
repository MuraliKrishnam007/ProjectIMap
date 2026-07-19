using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="IMappingExpression{TSource,TDestination}.ForAllMembers"/>:
/// a blanket rule applied to every writable destination member that doesn't
/// already have an explicit <c>.ForMember(...)</c> override at the time it runs.
/// </summary>
public sealed class ForAllMembersTests
{
    [Fact]
    public void Applies_Configured_Rule_To_Members_Without_Explicit_Override()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.CalculatedTotal, opt => opt.MapFrom(s => s.Price * s.Quantity))
              .ForAllMembers(opt => opt.NullSubstitute(string.Empty));
        var mapper = new Mapper(config);

        var dto = mapper.Map<Order, OrderDto>(new Order
        {
            Id = 1, Name = null!, Quantity = 2, Price = 5m, InternalId = null!
        });

        dto.Name.Should().Be(string.Empty,
            because: "Name has no explicit override, so ForAllMembers' NullSubstitute applies to it");
        dto.InternalId.Should().Be(string.Empty,
            because: "InternalId also has no explicit override");
        dto.CalculatedTotal.Should().Be(10m,
            because: "CalculatedTotal's explicit MapFrom override, registered before ForAllMembers ran, is untouched");
    }

    [Fact]
    public void Explicit_ForMember_Wins_When_Configured_Before_ForAllMembers()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name))
              .ForAllMembers(opt => opt.Ignore());
        var mapper = new Mapper(config);

        var dto = mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "Widget" });

        dto.Name.Should().Be("Widget",
            because: "Name already had an explicit ForMember override when ForAllMembers ran, so it was skipped");
        dto.Id.Should().Be(0,
            because: "Id had no override yet when ForAllMembers ran, so the blanket Ignore() applies to it");
    }
}
