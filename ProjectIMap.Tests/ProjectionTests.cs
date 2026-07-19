using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="ProjectionCompiler"/> and <see cref="QueryableExtensions.ProjectTo{T}"/>.
/// </summary>
/// <remarks>
/// Three categories of assertions are made per test:
/// <list type="number">
///   <item>Structural — the expression tree must contain only EF Core–safe nodes.</item>
///   <item>Functional — projecting an in-memory <see cref="IQueryable"/> must produce
///   correct values (validates the compiled lambda without requiring a real database).</item>
///   <item>Caching — calling <see cref="ProjectionCompiler.BuildProjection"/> twice for
///   the same pair must return the exact same <see cref="LambdaExpression"/> instance.</item>
/// </list>
/// </remarks>
public sealed class ProjectionTests
{
    // ── Structural assertion helper ───────────────────────────────────────────

    /// <summary>
    /// Walks the expression tree and fails the test if any forbidden node kind
    /// (Block, Assign, try/catch, local variable declaration) is found.
    /// </summary>
    private static void AssertEfSafeTree(Expression root)
    {
        var visitor = new ForbiddenNodeVisitor();
        visitor.Visit(root);
        visitor.ForbiddenNodes.Should().BeEmpty(
            because: "EF Core cannot translate Block, Assign, or TryCatch nodes to SQL");
    }

    private sealed class ForbiddenNodeVisitor : ExpressionVisitor
    {
        public List<string> ForbiddenNodes { get; } = [];

        public override Expression? Visit(Expression? node)
        {
            if (node is null) return null;

            switch (node.NodeType)
            {
                case ExpressionType.Block:
                    ForbiddenNodes.Add($"Block at type {node.Type.Name}");
                    break;
                case ExpressionType.Assign:
                    ForbiddenNodes.Add($"Assign at type {node.Type.Name}");
                    break;
                case ExpressionType.Try:
                    ForbiddenNodes.Add($"TryCatch at type {node.Type.Name}");
                    break;
                case ExpressionType.Throw:
                    ForbiddenNodes.Add($"Throw at type {node.Type.Name}");
                    break;
            }

            return base.Visit(node);
        }

        protected override Expression VisitBlock(BlockExpression node)
        {
            ForbiddenNodes.Add($"Block (variables: {node.Variables.Count})");
            return base.VisitBlock(node);
        }
    }

    // ── Test 1: Direct property match — User → UserDto ───────────────────────

    [Fact]
    public void BuildProjection_DirectMatch_ProducesEfSafeMemberInit()
    {
        // Arrange
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();

        // Act
        var lambda = ProjectionCompiler.BuildProjection(typeof(User), typeof(UserDto), config);

        // Assert — structural
        lambda.Should().NotBeNull();
        lambda.Body.NodeType.Should().Be(ExpressionType.MemberInit,
            because: "the root of a projection lambda must always be MemberInit");
        AssertEfSafeTree(lambda);
    }

    [Fact]
    public void ProjectTo_DirectMatch_MapsScalarPropertiesCorrectly()
    {
        // Arrange
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();

        // Address must be non-null: the projection emits a pure property chain
        // (src.Address.City) with no null guard — correct for EF Core (LEFT JOIN),
        // but in-memory EnumerableQuery compiles and executes the lambda directly.
        var addr = new Address { Street = "1 A St", City = "A", Country = "US", ZipCode = "00000" };
        var users = new[]
        {
            new User { Id = 1, Name = "Alice", Age = 30, Email = "alice@example.com", Address = addr },
            new User { Id = 2, Name = "Bob",   Age = 25, Email = "bob@example.com",   Address = addr }
        }.AsQueryable();

        // Act
        var dtos = users.ProjectTo<UserDto>(config).ToList();

        // Assert — functional
        dtos.Should().HaveCount(2);
        dtos[0].Id.Should().Be(1);
        dtos[0].Name.Should().Be("Alice");
        dtos[0].Age.Should().Be(30);
        dtos[0].Email.Should().Be("alice@example.com");
        dtos[1].Id.Should().Be(2);
        dtos[1].Name.Should().Be("Bob");
    }

    // ── Test 2: Flattening — User.Address.City → UserDto.AddressCity ─────────

    [Fact]
    public void BuildProjection_Flattening_ProducesEfSafePureChain()
    {
        // Arrange
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();

        // Act
        var lambda = ProjectionCompiler.BuildProjection(typeof(User), typeof(UserDto), config);

        // Assert — structural: no Block means the Address.City chain is a pure MemberExpression
        AssertEfSafeTree(lambda);

        // Assert — the binding for AddressCity must be a nested MemberExpression chain,
        // not a Block. Inspect the MemberInit bindings to find it.
        var memberInit  = (MemberInitExpression)lambda.Body;
        var cityBinding = memberInit.Bindings
            .OfType<MemberAssignment>()
            .FirstOrDefault(b => b.Member.Name == nameof(UserDto.AddressCity));

        cityBinding.Should().NotBeNull(because: "AddressCity should be mapped via flattening");
        cityBinding!.Expression.NodeType.Should().NotBe(ExpressionType.Block,
            because: "flattened chain must be a pure MemberExpression for EF Core");
    }

    [Fact]
    public void ProjectTo_Flattening_MapsNestedPropertiesCorrectly()
    {
        // Arrange
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();

        var users = new[]
        {
            new User
            {
                Id   = 42,
                Name = "Carol",
                Address = new Address
                {
                    Street  = "123 Main St",
                    City    = "Springfield",
                    Country = "US",
                    ZipCode = "12345"
                }
            }
        }.AsQueryable();

        // Act
        var dtos = users.ProjectTo<UserDto>(config).ToList();

        // Assert — functional
        dtos.Should().HaveCount(1);
        dtos[0].AddressCity.Should().Be("Springfield");
        dtos[0].AddressStreet.Should().Be("123 Main St");
        dtos[0].AddressCountry.Should().Be("US");
        dtos[0].AddressZipCode.Should().Be("12345");
    }

    // ── Test 3: ForMember Ignore / MapFrom ───────────────────────────────────

    [Fact]
    public void BuildProjection_ForMember_IgnoreAndMapFrom_ProducesEfSafeTree()
    {
        // Arrange
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.InternalId,      opt => opt.Ignore())
              .ForMember(d => d.CalculatedTotal, opt => opt.MapFrom(s => s.Price * s.Quantity));

        // Act
        var lambda = ProjectionCompiler.BuildProjection(typeof(Order), typeof(OrderDto), config);

        // Assert — structural
        lambda.Body.NodeType.Should().Be(ExpressionType.MemberInit);
        AssertEfSafeTree(lambda);
    }

    [Fact]
    public void ProjectTo_ForMember_IgnoreAndMapFrom_CorrectValues()
    {
        // Arrange
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .ForMember(d => d.InternalId,      opt => opt.Ignore())
              .ForMember(d => d.CalculatedTotal, opt => opt.MapFrom(s => s.Price * s.Quantity));

        var orders = new[]
        {
            new Order { Id = 1, Name = "Widget", Price = 9.99m, Quantity = 3, InternalId = "INTERNAL" },
            new Order { Id = 2, Name = "Gadget", Price = 4.50m, Quantity = 10, InternalId = "SKIP" }
        }.AsQueryable();

        // Act
        var dtos = orders.ProjectTo<OrderDto>(config).ToList();

        // Assert — functional
        dtos[0].CalculatedTotal.Should().Be(9.99m * 3);
        dtos[0].InternalId.Should().Be(string.Empty, because: "InternalId is ignored");
        dtos[1].CalculatedTotal.Should().Be(45.00m);
        dtos[1].InternalId.Should().Be(string.Empty, because: "InternalId is ignored");
    }

    // ── Test 4: Nested object mapping — pure MemberInit recursion ────────────

    [Fact]
    public void BuildProjection_NestedObject_NoBLockInSubInit()
    {
        // Arrange: map a source type that has a nested complex property to a dest
        // that also has a matching complex property (tests TryBuildNestedProjection).
        var config = new MapperConfiguration();
        config.CreateMap<Address, AddressDto>();
        config.CreateMap<UserWithAddress, UserWithAddressDto>();

        // Act
        var lambda = ProjectionCompiler.BuildProjection(
            typeof(UserWithAddress), typeof(UserWithAddressDto), config);

        // Assert — structural
        AssertEfSafeTree(lambda);

        var memberInit      = (MemberInitExpression)lambda.Body;
        var addressBinding  = memberInit.Bindings
            .OfType<MemberAssignment>()
            .FirstOrDefault(b => b.Member.Name == nameof(UserWithAddressDto.HomeAddress));

        addressBinding.Should().NotBeNull(because: "HomeAddress should be mapped via nested object mapping");
        addressBinding!.Expression.NodeType.Should().Be(ExpressionType.MemberInit,
            because: "nested object projection must be a pure MemberInit — no Block wrapper");
    }

    [Fact]
    public void ProjectTo_NestedObject_MapsSubPropertiesCorrectly()
    {
        // Arrange
        var config = new MapperConfiguration();
        config.CreateMap<Address, AddressDto>();
        config.CreateMap<UserWithAddress, UserWithAddressDto>();

        var users = new[]
        {
            new UserWithAddress
            {
                Id          = 7,
                HomeAddress = new Address { City = "Metropolis", Country = "US" }
            }
        }.AsQueryable();

        // Act
        var dtos = users.ProjectTo<UserWithAddressDto>(config).ToList();

        // Assert — functional
        dtos[0].Id.Should().Be(7);
        dtos[0].HomeAddress.Should().NotBeNull();
        dtos[0].HomeAddress!.City.Should().Be("Metropolis");
        dtos[0].HomeAddress.Country.Should().Be("US");
    }

    // ── Test 5: Projection lambda is cached (same instance returned) ─────────

    [Fact]
    public void BuildProjection_CalledTwice_ReturnsSameCachedInstance()
    {
        // Arrange
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();

        // Act
        var first  = ProjectionCompiler.BuildProjection(typeof(User), typeof(UserDto), config);
        var second = ProjectionCompiler.BuildProjection(typeof(User), typeof(UserDto), config);

        // Assert
        first.Should().BeSameAs(second,
            because: "projection lambdas must be cached to avoid rebuilding expression trees");
    }

    // ── Test 6: Unregistered pair throws with clear message ──────────────────

    [Fact]
    public void BuildProjection_UnregisteredPair_ThrowsInvalidOperationException()
    {
        // Arrange
        var config = new MapperConfiguration();
        // deliberately omit CreateMap<User, OrderDto>()

        // Act
        var act = () => ProjectionCompiler.BuildProjection(typeof(User), typeof(OrderDto), config);

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*No mapping registered*");
    }

    // ── Test 7: ConstructUsing — destination with no parameterless constructor ──

    [Fact]
    public void BuildProjection_ConstructUsing_ProducesEfSafeTree()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Person, PersonDto>()
              .ConstructUsing(s => new PersonDto(s.FirstName + " " + s.LastName, s.Age));

        var lambda = ProjectionCompiler.BuildProjection(typeof(Person), typeof(PersonDto), config);

        AssertEfSafeTree(lambda);
    }

    [Fact]
    public void ProjectTo_ConstructUsing_ProducesCorrectValues()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Person, PersonDto>()
              .ConstructUsing(s => new PersonDto(s.FirstName + " " + s.LastName, s.Age));

        var people = new[]
        {
            new Person { FirstName = "Ada", LastName = "Lovelace", Age = 36 }
        }.AsQueryable();

        var dtos = people.ProjectTo<PersonDto>(config).ToList();

        dtos[0].FullName.Should().Be("Ada Lovelace");
        dtos[0].Age.Should().Be(36);
    }

    // ── Test 8: Condition — CASE WHEN, still EF-safe ─────────────────────────
    //
    // Uses dedicated ConditionalOrder(Dto) models rather than Order/OrderDto:
    // ProjectionCompiler's cache is static and keyed only by (Type, Type), so
    // reusing a pair already registered elsewhere in this file with a different
    // ForMember configuration would silently return the other test's stale
    // cached lambda instead of building this one.

    [Fact]
    public void BuildProjection_Condition_ProducesEfSafeConditionalBinding()
    {
        var config = new MapperConfiguration();
        config.CreateMap<ConditionalOrder, ConditionalOrderDto>()
              .ForMember(d => d.Name, opt => opt.Condition(s => s.Quantity > 0));

        var lambda = ProjectionCompiler.BuildProjection(typeof(ConditionalOrder), typeof(ConditionalOrderDto), config);

        // Condition compiles to a Conditional (CASE WHEN) node, not Block/Assign/Throw.
        AssertEfSafeTree(lambda);
    }

    [Fact]
    public void ProjectTo_Condition_False_YieldsDefault_True_YieldsSourceValue()
    {
        var config = new MapperConfiguration();
        config.CreateMap<ConditionalOrder, ConditionalOrderDto>()
              .ForMember(d => d.Name, opt => opt.Condition(s => s.Quantity > 0));

        var orders = new[]
        {
            new ConditionalOrder { Name = "Hidden",  Quantity = 0 },
            new ConditionalOrder { Name = "Visible", Quantity = 3 }
        }.AsQueryable();

        var dtos = orders.ProjectTo<ConditionalOrderDto>(config).ToList();

        dtos[0].Name.Should().BeNull();
        dtos[1].Name.Should().Be("Visible");
    }

    // ── Test 9: NullSubstitute — EF-safe fallback, no MappingNullInvariantException ──

    [Fact]
    public void ProjectTo_NullSubstitute_ReplacesNullWithFallback()
    {
        var config = new MapperConfiguration();
        config.CreateMap<NullableNameSource, NullableNameDto>()
              .ForMember(d => d.Name, opt => opt.NullSubstitute("Unnamed"));

        var sources = new[]
        {
            new NullableNameSource { Name = null }
        }.AsQueryable();

        var dtos = sources.ProjectTo<NullableNameDto>(config).ToList();

        dtos[0].Name.Should().Be("Unnamed");
    }

    // ── Test 10: MaxDepth — depth-counted DFS guard stays EF-safe ────────────

    [Fact]
    public void BuildProjection_MaxDepth_ProducesEfSafeTree()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Category, CategoryDto>().MaxDepth(2);

        var lambda = ProjectionCompiler.BuildProjection(typeof(Category), typeof(CategoryDto), config);

        AssertEfSafeTree(lambda);
    }

    // ── Supplementary models for nested-object tests ─────────────────────────

    public sealed class UserWithAddress
    {
        public int      Id          { get; set; }
        public Address? HomeAddress { get; set; }
    }

    public sealed class UserWithAddressDto
    {
        public int         Id          { get; set; }
        public AddressDto? HomeAddress { get; set; }
    }

    public sealed class AddressDto
    {
        public string City    { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
    }

    // ── Dedicated models for Condition / NullSubstitute tests (see Test 8/9) ──

    public sealed class ConditionalOrder
    {
        public string Name     { get; set; } = string.Empty;
        public int    Quantity { get; set; }
    }

    public sealed class ConditionalOrderDto
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class NullableNameSource
    {
        public string? Name { get; set; }
    }

    public sealed class NullableNameDto
    {
        public string Name { get; set; } = string.Empty;
    }
}
