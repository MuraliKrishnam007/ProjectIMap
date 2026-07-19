using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectIMap;

namespace ProjectIMap.Tests;

// ── Stub profiles used only by DependencyInjectionTests ──────────────────────

/// <summary>
/// An abstract profile that must never be instantiated by the scanner.
/// If the scanner ignores the <c>type.IsAbstract</c> guard, calling
/// <see cref="Activator.CreateInstance"/> on an abstract type throws — which
/// would be caught by the <c>Should_Ignore_Abstract_Profiles</c> test.
/// </summary>
public abstract class AbstractOrderProfile : MappingProfile
{
    // Intentionally left empty — the body doesn't matter; what matters is that
    // this class is never instantiated by the scanner.
}

// ── Tests ─────────────────────────────────────────────────────────────────────

/// <summary>
/// End-to-end tests for the <c>AddMyMapper</c> DI extension:
/// profile discovery, graceful no-op on empty assemblies, and
/// the abstract-class guard in the type filter.
/// </summary>
public sealed class DependencyInjectionTests
{
    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>AddMyMapper</c> must scan the supplied assembly, discover
    /// <see cref="TestProfile"/>, apply its registrations, and register both
    /// <see cref="IMapper"/> and <see cref="MapperConfiguration"/> as singletons.
    /// Resolving <see cref="IMapper"/> and successfully executing a mapped call
    /// proves end-to-end wiring is correct.
    /// </summary>
    [Fact]
    public void Should_Discover_Profiles_And_Register_Mapper()
    {
        // Arrange — scan the test assembly; TestProfile lives there
        var services = new ServiceCollection();
        services.AddMyMapper(typeof(TestProfile).Assembly);
        var provider = services.BuildServiceProvider();

        // Act
        var mapper = provider.GetService<IMapper>();
        var config = provider.GetService<MapperConfiguration>();

        // Assert — both singletons are present
        mapper.Should().NotBeNull(
            because: "AddMyMapper must register IMapper as a singleton");
        config.Should().NotBeNull(
            because: "AddMyMapper must register MapperConfiguration as a singleton");

        // Verify the Order → OrderDto mapping was actually applied by executing it.
        // An unregistered pair would throw InvalidOperationException, not return a result.
        var order = new Order { Id = 99, Name = "Proof", Price = 5m, Quantity = 2 };
        var act   = () => mapper!.Map<Order, OrderDto>(order);
        act.Should().NotThrow(
            because: "TestProfile registers Order → OrderDto, so the map must exist after scanning");

        var dto = act();
        dto.Id.Should().Be(order.Id);
        dto.Name.Should().Be(order.Name);
    }

    /// <summary>
    /// When the scanned assembly contains no concrete <see cref="MappingProfile"/>
    /// subclasses (e.g. the core runtime assembly) the extension must complete
    /// without throwing and still register a valid — albeit empty —
    /// <see cref="IMapper"/> singleton.
    /// </summary>
    [Fact]
    public void Should_Not_Crash_If_Assembly_Has_No_Profiles()
    {
        // Arrange — the ProjectIMap library itself has no concrete profiles
        var libraryAssembly = typeof(IMapper).Assembly;

        var services = new ServiceCollection();
        var act      = () => services.AddMyMapper(libraryAssembly);

        // Assert — no exception during scanning
        act.Should().NotThrow(
            because: "scanning an assembly with no MappingProfile subclasses must be a silent no-op");

        // IMapper is still resolvable even when no profiles were found
        var provider = services.BuildServiceProvider();
        var mapper   = provider.GetService<IMapper>();
        mapper.Should().NotBeNull(
            because: "IMapper must be registered regardless of how many profiles were discovered");
    }

    /// <summary>
    /// <see cref="AbstractOrderProfile"/> is abstract; the scanner's
    /// <c>!type.IsAbstract</c> guard must exclude it.  If the guard were absent,
    /// <c>Activator.CreateInstance</c> would throw <see cref="MemberAccessException"/>,
    /// causing this test to fail.
    /// </summary>
    [Fact]
    public void Should_Ignore_Abstract_Profiles()
    {
        // Arrange — the test assembly contains both TestProfile (concrete) and
        // AbstractOrderProfile (abstract); only the former must be instantiated.
        var services = new ServiceCollection();
        var act      = () => services.AddMyMapper(typeof(AbstractOrderProfile).Assembly);

        // Assert — the abstract type is silently skipped; no exception surfaces
        act.Should().NotThrow(
            because: "abstract MappingProfile subclasses must be excluded by the IsAbstract guard");

        // The concrete TestProfile in the same assembly was still applied, so
        // Order → OrderDto must be functional.
        var provider = services.BuildServiceProvider();
        var mapper   = provider.GetRequiredService<IMapper>();
        var order    = new Order { Id = 1, Name = "Test", Price = 2m, Quantity = 3 };
        mapper.Invoking(m => m.Map<Order, OrderDto>(order))
              .Should().NotThrow(
                  because: "TestProfile's concrete mapping must still be registered");
    }
}
