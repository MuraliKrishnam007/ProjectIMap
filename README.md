# 🗺️ ProjectIMap

[![NuGet](https://img.shields.io/nuget/v/ProjectIMap.svg)](https://www.nuget.org/packages/ProjectIMap)
[![Downloads](https://img.shields.io/nuget/dt/ProjectIMap.svg)](https://www.nuget.org/packages/ProjectIMap)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/MuraliKrishnam007/ProjectIMap/blob/main/LICENSE)

**A high-performance, convention-based object-to-object mapper for .NET 10.**

ProjectIMap turns your entities into DTOs (and back) without hand-written mapping code.
Every mapping is compiled **once per type pair** into a native-IL delegate via Expression
Trees, then cached — **zero reflection and zero boxing on the hot path**. One engine backs
both in-memory `Map(...)` and EF Core-translatable `ProjectTo<T>(...)` SQL projection.

```csharp
var config = new MapperConfiguration();
config.CreateMap<Order, OrderDto>();

IMapper mapper = new Mapper(config);
OrderDto dto = mapper.Map<Order, OrderDto>(order);   // that's it
```

---

## 📚 Contents

- [Why ProjectIMap](#-why-projectimap)
- [How it works](#-how-it-works)
- [Install](#-install)
- [Quick start](#-quick-start)
- [Dependency injection](#-dependency-injection)
- [Feature guide](#-feature-guide)
- [EF Core projection](#-ef-core-projection-projectto)
- [Error model](#-error-model)
- [Cheat sheet (for humans & AI agents)](#-cheat-sheet-for-humans--ai-agents)
- [Performance notes](#-performance-notes)
- [Version history](#%EF%B8%8F-version-history)
- [License](#-license)

---

## ✨ Why ProjectIMap

| | |
|---|---|
| ⚡ **Compiled, not reflected** | First call per type pair builds + JIT-compiles an Expression Tree; every later call invokes cached native IL. |
| 🧭 **Convention-first** | Same-name properties, flattening (`Customer.Name → CustomerName`) and unflattening (`CustomerName → Customer.Name`) work with zero configuration. |
| 🛢️ **EF Core-safe projection** | `ProjectTo<T>()` emits a *pure* expression (no blocks, no throws) that providers translate straight to SQL — only projected columns are selected. |
| 🛡️ **Fail-fast null contract** | A `null` `Nullable<T>` mapped onto a non-nullable destination throws a named exception with a fix suggestion — never a silent `0`/`default`. |
| 🧵 **Thread-safe singleton** | All caches are `ConcurrentDictionary`-backed. Configure once, inject `IMapper` everywhere. |
| 🪶 **Tiny footprint** | Single assembly; the only dependency is `Microsoft.Extensions.DependencyInjection.Abstractions`. MIT licensed. |

---

## ⚙️ How it works

```
  startup                          first Map<Order, OrderDto>() call
 ─────────                        ───────────────────────────────────
 CreateMap<Order, OrderDto>()               │
        │                                   ▼
        ▼                        ┌─────────────────────────┐
 ┌───────────────┐  registered   │  Expression Tree builder │
 │ Mapper        │  type pairs   │                         │
 │ Configuration │ ─────────────▶│  Phase 0  ForMember     │
 └───────────────┘               │  Phase 1  Name match    │
                                 │  Phase 2  Flattening    │
                                 │  Phase 3  Unflattening  │
                                 └────────────┬────────────┘
                                              │ compile once
                                              ▼
                                 ┌─────────────────────────┐
                                 │   native-IL delegate     │
                                 │   cached per type pair   │
                                 └────────────┬────────────┘
                                              │
              every subsequent call ──────────▶ direct invoke (no reflection)
```

**Member resolution** — for every writable destination property, the first phase that
produces a value wins:

```
 Phase 0   Explicit .ForMember(...) override?
           → Ignore / MapFrom / Resolver / Condition / NullSubstitute
 Phase 1   Source property with the same name (case-insensitive)?
           → direct assign, with type adaptation — or a nested object map
 Phase 2   Flattened path?          source.Customer.Name  →  dest.CustomerName
 Phase 3   Unflatten composite?     source.CustomerName   →  dest.Customer.Name
 (none)    Left unmapped            → surfaced by AssertConfigurationIsValid()
```

**Type adaptation** (applied automatically in Phases 1–3):
numeric widening (`int → long`), enum conversions, nullable lifting (`T → T?`),
and guarded nullable unwrapping (`T? → T`, throwing `MappingNullInvariantException`
when the value is `null` — see [Error model](#-error-model)).

---

## 📦 Install

```bash
dotnet add package ProjectIMap
```

Requires **.NET 8.0 or later** — the package ships `net8.0` and `net10.0` targets.

---

## 🚀 Quick start

```csharp
using ProjectIMap;

// 1. Describe your pairs (once, at startup)
var config = new MapperConfiguration();
config.CreateMap<Order, OrderDto>().ReverseMap();   // ReverseMap ⇒ OrderDto → Order too
config.CreateMap<Customer, CustomerDto>();

// 2. (Recommended) fail fast on unmapped members
config.AssertConfigurationIsValid();

// 3. Create the mapper (long-lived; thread-safe)
IMapper mapper = new Mapper(config);

// 4. Map!
OrderDto dto        = mapper.Map<OrderDto>(order);          // source type inferred at runtime
List<OrderDto> dtos = mapper.Map<List<OrderDto>>(orders);   // collections too
Order    roundTrip  = mapper.Map<OrderDto, Order>(dto);     // explicit two-generic form

// Both forms run the exact same compiled delegate — the inferred form just
// resolves the source type from the object once and caches the dispatch.
```

---

## 💉 Dependency injection

### Option A — inline configuration

```csharp
builder.Services.AddMyCustomMapper(cfg =>
{
    cfg.CreateMap<Order, OrderDto>().ReverseMap();
    cfg.CreateMap<Customer, CustomerDto>();
});
```

### Option B — profile classes discovered by assembly scan

```csharp
builder.Services.AddMyMapper(typeof(OrderProfile).Assembly);

public class OrderProfile : MappingProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>().ReverseMap();
        CreateMap<OrderLine, OrderLineDto>();
    }
}
```

Both register `MapperConfiguration` and `IMapper` as **singletons**. Inject as usual:

```csharp
public class OrderService(IMapper mapper)
{
    public OrderDto Get(Order order) => mapper.Map<Order, OrderDto>(order);
}
```

> 💡 When resolved from a DI container, `Mapper` automatically receives the
> `IServiceProvider` — which enables [DI-resolved value resolvers](#-value-resolvers).

---

## 🧰 Feature guide

### 🔤 Convention mapping, flattening & unflattening

```csharp
public class Order    { public int Id { get; set; } public Customer Customer { get; set; } }
public class OrderDto { public int Id { get; set; } public string CustomerName { get; set; } }

config.CreateMap<Order, OrderDto>();     // Id → Id, Customer.Name → CustomerName
config.CreateMap<Order, OrderDto>().ReverseMap();  // and CustomerName → Customer.Name back
```

Nested complex members with matching names are mapped **recursively by convention** —
no extra registration needed (register the nested pair only when you want to customize it).

### 🎛️ Per-member configuration — `ForMember`

```csharp
config.CreateMap<Order, OrderDto>()
      .ForMember(d => d.Total,  opt => opt.MapFrom(s => s.Price * s.Quantity))
      .ForMember(d => d.Secret, opt => opt.Ignore())
      .ForMember(d => d.Note,   opt => opt.NullSubstitute("n/a"))
      .ForMember(d => d.Email,  opt =>
      {
          opt.MapFrom(s => s.Email);
          opt.Condition(s => s.EmailVerified);   // skipped when false
      });
```

| Option | Effect |
|---|---|
| `MapFrom(lambda)` | Compute the member from any source expression. |
| `Ignore()` | Never map this member. |
| `Condition(predicate)` | Map only when the predicate is true (on map-into-existing, a false predicate **preserves** the current destination value). |
| `NullSubstitute(value)` | Fallback value when the source resolves to `null`. |
| `MapFrom(resolverInstance)` / `MapFrom<TResolver, TMember>()` | See [Value resolvers](#-value-resolvers). |

### 🏗️ Custom construction — `ConstructUsing`

For records, types without a parameterless constructor, or full construction control:

```csharp
config.CreateMap<Person, PersonDto>()
      .ConstructUsing(s => new PersonDto(s.Id, $"{s.First} {s.Last}"));
```

> ⚠️ `ConstructUsing` is **authoritative**: convention binding is skipped for that pair.
> The lambda is responsible for the whole object.

### 🔔 Lifecycle hooks — `BeforeMap` / `AfterMap`

```csharp
config.CreateMap<Order, OrderDto>()
      .BeforeMap((src, dest) => dest.AuditNote = "mapping…")   // dest constructed, not yet populated
      .AfterMap((src, dest) => dest.AuditNote = $"mapped {DateTime.UtcNow}");
```

Execution order: `construct → BeforeMap → member assignments → AfterMap`.
Hooks also fire on `Map(source, destination)`.

### ♻️ Map into an existing instance

```csharp
mapper.Map(order, existingDto);      // updates existingDto in place, returns it
```

### 🧺 Collections

```csharp
// Fresh collection — element pair must be registered:
List<OrderDto> dtos = mapper.Map<List<Order>, List<OrderDto>>(orders);
OrderDto[] array    = mapper.Map<Order[], OrderDto[]>(orderArray);

// Merge into an existing collection (destination must be a mutable ICollection<T>):
mapper.Map(orders, existingDtoList);   // default: clear + rebuild
```

### 🆔 Identity-based collection diffing — `EqualityComparison`

Opt a pair into **add / update / remove** merging instead of clear-and-rebuild —
ideal for syncing EF-tracked child collections:

```csharp
config.CreateMap<OrderLine, OrderLineDto>()
      .EqualityComparison((src, dst) => src.Id == dst.Id);

mapper.Map(sourceLines, trackedDtoLines);
// matched by Id  → updated in place
// only in dest   → removed
// only in source → mapped fresh and added
```

### 🧩 Value resolvers

Reusable, testable mapping logic as a class:

```csharp
public class FullNameResolver : IValueResolver<Employee, string>
{
    public string Resolve(Employee source) => $"{source.FirstName} {source.LastName}";
}

// Caller-constructed instance:
config.CreateMap<Employee, EmployeeDto>()
      .ForMember(d => d.FullName, opt => opt.MapFrom(new FullNameResolver()));

// DI-resolved — a fresh instance is resolved from the container on EVERY map call
// (correct behaviour for scoped/transient dependencies):
config.CreateMap<Employee, EmployeeDto>()
      .ForMember(d => d.FullName, opt => opt.MapFrom<FullNameResolver, string>());

var mapper = new Mapper(config, serviceProvider);   // DI resolvers need this constructor
```

### 🐕 Polymorphic / inheritance mapping — `Include`

```csharp
config.CreateMap<Animal, AnimalDto>()
      .Include<Dog, DogDto>()
      .Include<Cat, CatDto>();
config.CreateMap<Dog, DogDto>();     // derived pairs must be registered
config.CreateMap<Cat, CatDto>();
```

Runtime-type dispatch applies **everywhere**:

```csharp
mapper.Map<Animal, AnimalDto>(new Dog(...));            // → DogDto  (top level)
mapper.Map<List<Animal>, List<AnimalDto>>(mixedList);    // → DogDto/CatDto per element
mapper.Map<Owner, OwnerDto>(ownerWithDogPet);            // → nested Pet becomes DogDto
```

The dispatch cost is paid **only** by pairs that declare `Include<>` — everything else
keeps the zero-overhead compiled path.

### 🌐 Blanket rules — `ForAllMembers`

Apply one rule to every destination member that has no explicit override:

```csharp
config.CreateMap<Order, OrderDto>()
      .ForMember(d => d.Total, opt => opt.MapFrom(s => s.Price * s.Quantity))
      .ForAllMembers(opt => opt.NullSubstitute(string.Empty));   // ← call LAST
```

> ⚠️ Call `ForAllMembers` **after** all individual `ForMember` calls — it fills in
> members that have no override *at the moment it runs* and never replaces one.

### 🌀 Recursion & lookahead caps — `MaxDepth` / `FlattenDepth`

```csharp
// Self-referencing graphs (default depth: 1 — deeper self-references become null):
config.CreateMap<Category, CategoryDto>().MaxDepth(3);

// Flattening lookahead across navigation properties (default: 5):
config.CreateMap<Report, ReportDto>().FlattenDepth(2);
```

### ✅ Startup validation

```csharp
config.AssertConfigurationIsValid();
```

Walks every registered pair — **recursively, into nested complex-type pairs** — and
throws `MapperConfigurationException` listing every destination member that nothing
maps to. Call it once at startup (or in a unit test) to catch drift the moment a
property is renamed.

---

## 🛢️ EF Core projection — `ProjectTo`

```csharp
using ProjectIMap;

List<OrderDto> dtos = await db.Orders
    .Where(o => o.IsOpen)
    .ProjectTo<OrderDto>(config)     // note: takes the MapperConfiguration
    .ToListAsync();
```

```
 IQueryable<Order>                            SQL
 ───────────────────                         ─────────────────────────────
 .ProjectTo<OrderDto>(config)     ═════▶     SELECT o.Id, o.Name, c.Name
        │                                    FROM Orders o
        ▼                                    JOIN Customers c ON ...
 pure MemberInit expression                  -- only projected columns!
 (no blocks, no throws — fully
  provider-translatable)
```

`ProjectTo` honors `ForMember(MapFrom/Ignore/Condition/NullSubstitute)`,
`ConstructUsing`, flattening, and `FlattenDepth`.

> ⚠️ **Not available inside projections** (they cannot translate to SQL):
> value resolvers, `BeforeMap`/`AfterMap` hooks, and runtime polymorphic dispatch.
> Members configured with a resolver are left unbound in the projection.

---

## 🚨 Error model

Every failure is loud, early, and self-describing:

| Exception | Thrown when |
|---|---|
| `InvalidOperationException` | Mapping a pair that was never registered (message tells you the exact `CreateMap<,>()` to add) · using a DI resolver on a `Mapper` built without an `IServiceProvider` · merging into an array/read-only collection. |
| `MappingNullInvariantException` | A `null` `Nullable<T>` source value meets a non-nullable destination member. Fix: make the destination nullable, or add `NullSubstitute`. |
| `MapperConfigurationException` | `AssertConfigurationIsValid()` found unmapped destination members (all of them are listed). |
| `ArgumentNullException` | `null` `source`/`destination` argument. |

---

## 🤖 Cheat sheet (for humans & AI agents)

A complete, minimal reference for implementing against this package.

### Core types

| Type | Role |
|---|---|
| `MapperConfiguration` | Registry. `CreateMap<TSrc,TDst>()`, `AssertConfigurationIsValid()`. |
| `Mapper : IMapper` | Engine. `new Mapper(config)` or `new Mapper(config, serviceProvider)`. |
| `IMapper` | `Map<TDst>(src)` (source type inferred) · `Map<TSrc,TDst>(src)` · `Map<TSrc,TDst>(src, dest)`. |
| `MappingProfile` | Base class for scan-discovered profiles (`AddMyMapper(assembly)`). |
| `IValueResolver<TSrc,TMember>` | `TMember Resolve(TSrc source)`. |
| `QueryableExtensions` | `queryable.ProjectTo<TDst>(config)`. |

### Fluent surface

```csharp
config.CreateMap<TSrc, TDst>()
      .ReverseMap()                                   // also register TDst → TSrc
      .ForMember(d => d.X, opt => { ... })            // see member options below
      .ForAllMembers(opt => { ... })                  // blanket rule — call LAST
      .ConstructUsing(s => new TDst(...))             // authoritative construction
      .BeforeMap((s, d) => ...) .AfterMap((s, d) => ...)
      .Include<TDerivedSrc, TDerivedDst>()            // polymorphic dispatch
      .EqualityComparison((s, d) => bool)             // identity diff for merges
      .MaxDepth(int) .FlattenDepth(int);              // defaults: 1 and 5

// member options inside ForMember/ForAllMembers:
opt.MapFrom(s => expr);                    // lambda
opt.MapFrom(resolverInstance);             // IValueResolver instance
opt.MapFrom<TResolver, TMember>();         // DI-resolved (needs Mapper(config, sp))
opt.Ignore();
opt.Condition(s => bool);
opt.NullSubstitute(value);
```

### Rules & gotchas

1. **Register top-level pairs and collection element pairs explicitly.** Nested complex
   members map by convention without registration; register the nested pair only to
   customize it.
2. **`ConstructUsing` skips all convention binding** for that pair — the lambda builds
   the whole object.
3. **`ForAllMembers` must be the last configuration call** on a pair; it only fills
   members that have no override yet.
4. **One `BeforeMap` and one `AfterMap` per pair** — registering a second replaces the
   first.
5. **DI resolvers** (`MapFrom<TResolver,TMember>()`) require `new Mapper(config,
   serviceProvider)`; resolution is fresh per map call. Container-registered mappers get
   this automatically.
6. **`Map(source, destination)` with collections** mutates the destination; it must be a
   mutable `ICollection<T>` (arrays throw). Default is clear+rebuild; `EqualityComparison`
   on the *element* pair switches it to add/update/remove diffing.
7. **`Include<>` derived pairs must themselves be registered.** Dispatch covers top-level
   calls, collection elements, and nested members.
8. **Self-referencing types** stop recursing at `MaxDepth` (default 1) — deeper
   self-references map to `null`; raise `MaxDepth(n)` as needed.
9. **`ProjectTo` takes the `MapperConfiguration`**, not the `IMapper`, and cannot use
   resolvers/hooks/polymorphism (not SQL-translatable).
10. **`Mapper` is a thread-safe singleton** — build one, share it; first call per pair
    pays one-time compilation.
11. **Records / ctor-only types** → use `ConstructUsing`.
12. **Call `AssertConfigurationIsValid()` at startup** to fail fast on unmapped members.
13. **`Map<TDst>(source)` infers from the runtime type** — `source` must not be `null`
    (no type to infer), and a derived instance registered only via its base pair
    dispatches through the base + `Include<>` machinery automatically. Use the
    two-generic form when the static source type matters or the source may be null.

### Minimal end-to-end template

```csharp
using ProjectIMap;

var config = new MapperConfiguration();
config.CreateMap<Source, Dest>()
      .ForMember(d => d.Computed, opt => opt.MapFrom(s => s.A + s.B));
config.AssertConfigurationIsValid();

IMapper mapper = new Mapper(config);
Dest dest = mapper.Map<Source, Dest>(source);
```

---

## 🏎️ Performance notes

- **Compile once, run forever** — each type pair is compiled to IL on first use and
  cached in a `ConcurrentDictionary`; concurrent first calls compile at most once.
- **No reflection on the hot path** — property access compiles to direct
  call/`ldfld` instructions, never `GetValue`/`SetValue`.
- **Flattening uses a cached property trie** per source type for O(path) lookahead
  instead of repeated reflection scans.
- **Pay-for-what-you-use** — pairs without hooks/`ConstructUsing` keep a single
  `MemberInit` delegate; polymorphic dispatch costs one type check and only for pairs
  that declare `Include<>`.
- **The inferred `Map<TDst>(src)` overload is opt-in convenience** — it adds one
  `GetType()` + cache lookup (~15% on a trivial 4-property map, measured ~9M vs
  ~10M ops/s); the explicit two-generic path is byte-for-byte unchanged.

---

## 🗒️ Version history

| Version | Highlights |
|---|---|
| **6.0.0** | Multi-targets **net8.0 + net10.0** (per-TFM dependencies); new boilerplate-free `Map<TDestination>(source)` overload with runtime source-type inference (collections, LINQ sequences, and `Include<>` polymorphism all supported); explicit-overload hot path unchanged. |
| **5.1.x** | `Include<>` polymorphic dispatch extended to collection elements and nested members; standalone documentation. |
| **5.0.0** | DI-resolved value resolvers (`MapFrom<TResolver,TMember>()`), `ForAllMembers`, identity-based collection diffing (`EqualityComparison`). |
| **4.0.0** | Collection merge, `FlattenDepth`, `IValueResolver` classes, `Include<>` polymorphic mapping, recursive configuration validation. |
| **3.0.0** | Map-into-existing, `ConstructUsing`, `Condition`/`NullSubstitute`, `BeforeMap`/`AfterMap`, `MaxDepth`, `AssertConfigurationIsValid`. |

---

## 📄 License

[MIT](https://github.com/MuraliKrishnam007/ProjectIMap/blob/main/LICENSE) — free for
commercial and non-commercial use, at any scale.

---

<p align="center"><i>Built with Expression Trees, obsessive caching, and a fail-fast philosophy.</i> 🗺️</p>
