using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectIMap;

namespace ProjectIMap.Tests;

// ── Test profile ──────────────────────────────────────────────────────────────

/// <summary>
/// Concrete profile used across <see cref="ProfileMappingTests"/> and
/// <see cref="DependencyInjectionTests"/>.
/// Registrations exercised here:
/// <list type="bullet">
///   <item><c>Order → OrderDto</c> with <c>Ignore</c> and <c>MapFrom</c> overrides</item>
///   <item>The inverse <c>OrderDto → Order</c> produced by <c>ReverseMap()</c></item>
/// </list>
/// </summary>
public sealed class TestProfile : MappingProfile
{
    public TestProfile()
    {
        CreateMap<Order, OrderDto>()
            // InternalId exists on both types but must NOT be copied forward.
            .ForMember(d => d.InternalId,      opt => opt.Ignore())
            // CalculatedTotal has no source counterpart — derive it from an expression.
            .ForMember(d => d.CalculatedTotal, opt => opt.MapFrom(s => s.Price * s.Quantity))
            .ReverseMap();
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Verifies <see cref="MappingProfile"/>-based configuration: <c>ForMember</c>
/// overrides and <c>ReverseMap</c> are all applied correctly after the profile
/// is loaded through <c>AddMyMapper</c>.
/// </summary>
public sealed class ProfileMappingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a service provider that has scanned the test assembly for profiles,
    /// then resolves <see cref="IMapper"/> from it.
    /// <see cref="MappingProfile.ApplyTo"/> is <c>internal</c>, so the only
    /// supported path for applying a profile is through the DI extension.
    /// </summary>
    private static IMapper CreateMapperViaProfile()
    {
        var services = new ServiceCollection();
        services.AddMyMapper(typeof(TestProfile).Assembly);
        return services.BuildServiceProvider().GetRequiredService<IMapper>();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A destination property marked with <c>.Ignore()</c> must retain its CLR
    /// default value; the Phase-0 override path must suppress all three convention
    /// phases for that property.
    /// </summary>
    [Fact]
    public void Should_Respect_Ignore_Rule_And_Leave_Property_Default()
    {
        // Arrange
        var mapper = CreateMapperViaProfile();
        var source = new Order
        {
            Id         = 10,
            Name       = "Widget",
            Price      = 9.99m,
            Quantity   = 3,
            InternalId = "INTERNAL-XYZ"   // ← must NOT appear in the DTO
        };

        // Act
        var dest = mapper.Map<Order, OrderDto>(source);

        // Assert — ignored property stays at default regardless of source value
        dest.InternalId.Should().Be(string.Empty,
            because: "InternalId is decorated with .Ignore() in TestProfile");

        // Sanity-check: other directly-matched properties are still mapped
        dest.Id.Should().Be(source.Id);
        dest.Name.Should().Be(source.Name);
    }

    /// <summary>
    /// A destination property bound via <c>.MapFrom(s =&gt; s.Price * s.Quantity)</c>
    /// must evaluate the inlined lambda body against the live source object.
    /// The expression is compiled directly into the mapping IL — no delegate
    /// invocation overhead at runtime.
    /// </summary>
    [Fact]
    public void Should_Execute_Custom_MapFrom_Expression()
    {
        // Arrange
        var mapper = CreateMapperViaProfile();
        var source = new Order
        {
            Id       = 5,
            Name     = "Gadget",
            Price    = 12.50m,
            Quantity = 4
        };
        var expectedTotal = source.Price * source.Quantity; // 50.00

        // Act
        var dest = mapper.Map<Order, OrderDto>(source);

        // Assert
        dest.CalculatedTotal.Should().Be(expectedTotal,
            because: "CalculatedTotal is driven by MapFrom(s => s.Price * s.Quantity)");
        dest.Id.Should().Be(source.Id);
        dest.Name.Should().Be(source.Name);
    }

    /// <summary>
    /// Calling <c>ReverseMap()</c> on the fluent chain must implicitly register
    /// the <c>OrderDto → Order</c> direction.  No second explicit
    /// <c>CreateMap&lt;OrderDto, Order&gt;()</c> call should be required.
    /// </summary>
    [Fact]
    public void Should_Apply_ReverseMap_Automatically()
    {
        // Arrange
        var mapper = CreateMapperViaProfile();
        var source = new OrderDto
        {
            Id              = 7,
            Name            = "Thingamajig",
            CalculatedTotal = 99.99m,
            InternalId      = "REV-001"
        };

        // Act — would throw InvalidOperationException if reverse pair was not registered
        var act  = () => mapper.Map<OrderDto, Order>(source);

        // Assert — no exception: the reverse direction exists
        act.Should().NotThrow(because: "ReverseMap() registers OrderDto → Order automatically");

        var dest = act();

        // Properties that exist with matching names on both sides must transfer
        dest.Id.Should().Be(source.Id);
        dest.Name.Should().Be(source.Name);
        // InternalId is NOT ignored on the reverse direction — it should be copied
        dest.InternalId.Should().Be(source.InternalId);
    }
}
