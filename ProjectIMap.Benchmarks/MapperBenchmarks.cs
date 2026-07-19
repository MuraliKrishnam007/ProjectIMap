using AutoMapper;
using BenchmarkDotNet.Attributes;
using ProjectIMap;

// Alias our custom mapper types to prevent name collisions with AutoMapper's
// identically-named public types.
using CustomMapperConfiguration = ProjectIMap.MapperConfiguration;
using CustomIMapper              = ProjectIMap.IMapper;

namespace ProjectIMap.Benchmarks;

// ─────────────────────────────────────────────────────────────────────────────
// Benchmark models — kept private to this file; they do not need to be
// accessible from the main library or test projects.
// ─────────────────────────────────────────────────────────────────────────────

// ── Simple pair ──────────────────────────────────────────────────────────────

/// <summary>Flat source entity with four scalar properties.</summary>
public sealed class BenchUser
{
    public int    Id    { get; set; }
    public string Name  { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int    Age   { get; set; }
}

/// <summary>Flat destination DTO — identical shape to <see cref="BenchUser"/>.</summary>
public sealed class BenchUserDto
{
    public int    Id    { get; set; }
    public string Name  { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int    Age   { get; set; }
}

// ── Complex / flattened pair ──────────────────────────────────────────────────

/// <summary>Nested customer value object carried by <see cref="BenchOrder"/>.</summary>
public sealed class BenchCustomer
{
    public string Name    { get; set; } = string.Empty;
    public string Email   { get; set; } = string.Empty;
    public string Phone   { get; set; } = string.Empty;
}

/// <summary>Line-item within a <see cref="BenchOrder"/>.</summary>
public sealed class BenchOrderItem
{
    public int     ProductId   { get; set; }
    public string  ProductName { get; set; } = string.Empty;
    public decimal UnitPrice   { get; set; }
    public int     Quantity    { get; set; }
}

/// <summary>
/// Rich source aggregate: a top-level order that owns a nested
/// <see cref="BenchCustomer"/> and a collection of <see cref="BenchOrderItem"/>s.
/// </summary>
public sealed class BenchOrder
{
    public int                  Id        { get; set; }
    public string               Reference { get; set; } = string.Empty;
    public decimal              Total     { get; set; }
    public BenchCustomer        Customer  { get; set; } = new();
    public List<BenchOrderItem> Items     { get; set; } = [];
}

/// <summary>
/// Flat destination DTO.
/// <list type="bullet">
///   <item>
///     <c>Id</c>, <c>Reference</c>, <c>Total</c> — direct Phase-1 matches.
///   </item>
///   <item>
///     <c>CustomerName</c>, <c>CustomerEmail</c>, <c>CustomerPhone</c> — resolved
///     by the custom mapper's Phase-2 trie traversal
///     (<c>source.Customer.Name</c> → <c>dest.CustomerName</c>, etc.).
///     AutoMapper resolves the same names via its own flattening convention.
///   </item>
/// </list>
/// </summary>
public sealed class BenchOrderDto
{
    public int     Id            { get; set; }
    public string  Reference     { get; set; } = string.Empty;
    public decimal Total         { get; set; }

    // Flattened from Customer.*
    public string  CustomerName  { get; set; } = string.Empty;
    public string  CustomerEmail { get; set; } = string.Empty;
    public string  CustomerPhone { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// Benchmarks
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Head-to-head throughput and allocation comparison between ProjectIMap and
/// AutoMapper for three representative workloads:
/// <list type="number">
///   <item>Simple flat object (4 scalar properties)</item>
///   <item>Complex object with a nested sub-object, flattened to a flat DTO</item>
///   <item>Bulk mapping of a 1 000-element <c>List&lt;BenchOrder&gt;</c></item>
/// </list>
/// <para>
/// <b>How to run:</b>
/// <code>dotnet run -c Release --project ProjectIMap.Benchmarks</code>
/// </para>
/// <para>
/// BenchmarkDotNet requires a Release build; running in Debug mode triggers a
/// warning and produces unreliable timings.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class MapperBenchmarks
{
    // ── Mapper instances (allocated once in GlobalSetup) ─────────────────────

    private CustomIMapper      _customMapper = null!;
    private AutoMapper.IMapper _autoMapper   = null!;

    // ── Pre-built dummy data (allocated once in GlobalSetup) ─────────────────
    // Keeping data construction out of the [Benchmark] methods ensures we
    // measure only the mapper's hot path, not object initialisation.

    private BenchUser             _singleUser      = null!;
    private BenchOrder            _singleOrder     = null!;
    private List<BenchOrder>      _largeOrderList  = null!;

    // ── One-time setup ────────────────────────────────────────────────────────

    /// <summary>
    /// Executed once before BenchmarkDotNet begins iterating.
    /// Both mapper stacks are fully "warmed up" here so that the first
    /// benchmark call hits the already-compiled delegate cache, not the
    /// one-time JIT / Expression Tree compilation step.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        // ── 1. Custom mapper ──────────────────────────────────────────────────

        var customConfig = new CustomMapperConfiguration();
        customConfig.CreateMap<BenchUser,  BenchUserDto>();
        customConfig.CreateMap<BenchOrder, BenchOrderDto>();

        _customMapper = new Mapper(customConfig);

        // ── 2. AutoMapper ─────────────────────────────────────────────────────
        // AutoMapper's own flattening convention resolves Customer.Name →
        // CustomerName automatically, mirroring the custom mapper's trie phase.

        var autoConfig = new AutoMapper.MapperConfiguration(cfg =>
        {
            cfg.CreateMap<BenchUser,  BenchUserDto>();

            // AutoMapper resolves CustomerName/CustomerEmail/CustomerPhone from
            // the nested Customer sub-object via its built-in flattening —
            // no explicit ForMember calls required, same as ProjectIMap.
            cfg.CreateMap<BenchOrder, BenchOrderDto>();
        });

        _autoMapper = autoConfig.CreateMapper();

        // ── 3. Dummy data ─────────────────────────────────────────────────────

        _singleUser = new BenchUser
        {
            Id    = 1,
            Name  = "Alice",
            Email = "alice@benchmarks.dev",
            Age   = 30
        };

        _singleOrder = BuildOrder(id: 1);

        // 1 000 orders — used by the large-collection benchmarks.
        _largeOrderList = Enumerable.Range(1, 1_000)
                                    .Select(BuildOrder)
                                    .ToList();

        // ── 4. Warm-up: force compilation of all Expression Tree delegates ────
        // BenchmarkDotNet runs a warm-up phase automatically, but triggering
        // compilation here guarantees the [Benchmark] iterations never include
        // the one-time compile cost regardless of which iteration order is used.
        _customMapper.Map<BenchUser,  BenchUserDto>(_singleUser);
        _customMapper.Map<BenchOrder, BenchOrderDto>(_singleOrder);
        _customMapper.Map<List<BenchOrder>, List<BenchOrderDto>>(_largeOrderList);

        _autoMapper.Map<BenchUser,        BenchUserDto>(_singleUser);
        _autoMapper.Map<BenchOrder,       BenchOrderDto>(_singleOrder);
        _autoMapper.Map<List<BenchOrder>, List<BenchOrderDto>>(_largeOrderList);
    }

    // ── Benchmark methods ─────────────────────────────────────────────────────

    // ── Simple flat object ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a four-property flat object using ProjectIMap.
    /// The compiled delegate performs four direct property assignments — no
    /// reflection, no dictionary lookups, no boxing.
    /// </summary>
    [Benchmark(Description = "Custom  — simple flat object")]
    public BenchUserDto Custom_Map_SimpleObject()
        => _customMapper.Map<BenchUser, BenchUserDto>(_singleUser);

    /// <summary>
    /// Maps the same four-property flat object using AutoMapper as a baseline.
    /// </summary>
    [Benchmark(Description = "AutoMapper — simple flat object")]
    public BenchUserDto AutoMapper_Map_SimpleObject()
        => _autoMapper.Map<BenchUser, BenchUserDto>(_singleUser);

    // ── Complex flattened object ──────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="BenchOrder"/> (nested <see cref="BenchCustomer"/> sub-object)
    /// to a flat <see cref="BenchOrderDto"/> using ProjectIMap's trie-based
    /// Phase-2 flattening convention.
    /// </summary>
    [Benchmark(Description = "Custom  — complex flattened object")]
    public BenchOrderDto Custom_Map_ComplexFlattenedObject()
        => _customMapper.Map<BenchOrder, BenchOrderDto>(_singleOrder);

    /// <summary>
    /// Maps the same <see cref="BenchOrder"/> to <see cref="BenchOrderDto"/>
    /// using AutoMapper's built-in flattening convention as a baseline.
    /// </summary>
    [Benchmark(Description = "AutoMapper — complex flattened object")]
    public BenchOrderDto AutoMapper_Map_ComplexFlattenedObject()
        => _autoMapper.Map<BenchOrder, BenchOrderDto>(_singleOrder);

    // ── Large collection ──────────────────────────────────────────────────────

    /// <summary>
    /// Maps 1 000 <see cref="BenchOrder"/> instances to a
    /// <c>List&lt;BenchOrderDto&gt;</c> using ProjectIMap's collection-mapping
    /// path (<c>source.Select(elementMapper).ToList()</c> compiled to IL).
    /// The element delegate is shared with the single-object benchmark —
    /// no recompilation occurs.
    /// </summary>
    [Benchmark(Description = "Custom  — 1 000-item collection")]
    public List<BenchOrderDto> Custom_Map_LargeCollection()
        => _customMapper.Map<List<BenchOrder>, List<BenchOrderDto>>(_largeOrderList);

    /// <summary>
    /// Maps the same 1 000 <see cref="BenchOrder"/> instances using AutoMapper
    /// as a baseline.
    /// </summary>
    [Benchmark(Description = "AutoMapper — 1 000-item collection")]
    public List<BenchOrderDto> AutoMapper_Map_LargeCollection()
        => _autoMapper.Map<List<BenchOrder>, List<BenchOrderDto>>(_largeOrderList);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Constructs a fully-populated <see cref="BenchOrder"/> with a non-null
    /// <see cref="BenchCustomer"/> and three <see cref="BenchOrderItem"/>s.
    /// Called only from <see cref="GlobalSetup"/> — never from a benchmark method.
    /// </summary>
    private static BenchOrder BuildOrder(int id) => new()
    {
        Id        = id,
        Reference = $"ORD-{id:D6}",
        Total     = id * 19.99m,
        Customer  = new BenchCustomer
        {
            Name  = $"Customer {id}",
            Email = $"customer{id}@benchmarks.dev",
            Phone = $"+1-555-{id:D4}"
        },
        Items =
        [
            new BenchOrderItem { ProductId = 1, ProductName = "Widget A", UnitPrice = 9.99m,  Quantity = 2 },
            new BenchOrderItem { ProductId = 2, ProductName = "Widget B", UnitPrice = 4.99m,  Quantity = 1 },
            new BenchOrderItem { ProductId = 3, ProductName = "Widget C", UnitPrice = 14.99m, Quantity = 3 }
        ]
    };
}
