# ProjectIMap

A high-performance, convention-based **object-to-object mapper for .NET 10**.

Mapping delegates are compiled **once per type pair** via Expression Trees and
cached for the lifetime of the mapper — no reflection and no boxing on the hot
path. One runtime engine backs both in-memory `Map` and EF Core-safe `ProjectTo`
SQL projection.

Built as a free alternative to AutoMapper (which is
[commercial above $5M org revenue](https://automapper.io) as of 2025) — with no
licensing cliff at any scale.

---

## Install

```bash
dotnet add package ProjectIMap
```

Requires **.NET 10** or later. The only dependency is
`Microsoft.Extensions.DependencyInjection.Abstractions`.

---

## Quick start

```csharp
using ProjectIMap;

var config = new MapperConfiguration();
config.CreateMap<Order, OrderDto>();

var mapper = new Mapper(config);

OrderDto dto = mapper.Map<Order, OrderDto>(order);
```

### Dependency injection

```csharp
// Inline configuration:
builder.Services.AddMyCustomMapper(cfg =>
{
    cfg.CreateMap<Order, OrderDto>().ReverseMap();
    cfg.CreateMap<Customer, CustomerDto>();
});

// …or scan assemblies for MappingProfile subclasses:
builder.Services.AddMyMapper(typeof(OrderProfile).Assembly);

public class OrderProfile : MappingProfile
{
    public OrderProfile() => CreateMap<Order, OrderDto>().ReverseMap();
}

// Then inject IMapper anywhere:
public class OrderService(IMapper mapper) { /* … */ }
```

### EF Core projection

`ProjectTo` compiles a pure `MemberInit` expression (no `Block`/`throw`), so the
provider translates it straight to SQL — only the projected columns are selected.

```csharp
using ProjectIMap;

List<OrderDto> dtos = await db.Orders
    .Where(o => o.IsOpen)
    .ProjectTo<OrderDto>(config)
    .ToListAsync();
```

---

## Features

| Capability | API |
|---|---|
| Convention mapping (case-insensitive) | automatic |
| Flattening / unflattening | `Customer.Name` ⇄ `CustomerName` |
| Bi-directional mapping | `.ReverseMap()` |
| Member overrides | `.ForMember(d => d.X, o => o.MapFrom(s => …))` |
| Ignore / conditional / null-substitute | `.Ignore()`, `.Condition(…)`, `.NullSubstitute(…)` |
| Custom construction | `.ConstructUsing(s => new Dto(s.Id))` |
| Lifecycle hooks | `.BeforeMap(…)`, `.AfterMap(…)` |
| Map into an existing instance | `mapper.Map(source, destination)` |
| Collection mapping & merge | `List<T>` / `T[]` / `IEnumerable<T>` |
| Identity-based collection diff | `.EqualityComparison((s, d) => s.Id == d.Id)` |
| Value resolver classes | `IValueResolver<TSource, TMember>` |
| DI-resolved resolvers | `.MapFrom<TResolver, TMember>()` |
| Polymorphic / inheritance mapping | `.Include<TDerivedSource, TDerivedDestination>()` |
| Blanket per-pair rule | `.ForAllMembers(…)` |
| Recursion / lookahead caps | `.MaxDepth(n)`, `.FlattenDepth(n)` |
| Startup validation | `config.AssertConfigurationIsValid()` |
| EF Core SQL projection | `queryable.ProjectTo<TDest>(config)` |

Polymorphic dispatch resolves the runtime type of each element in a mapped
collection and of nested complex members — a `Dog` inside a `List<Animal>`, or a
nested `Animal` member holding a `Dog`, maps to `DogDto` — paid only for pairs
that declare `Include<>`.

---

## How it works

1. `CreateMap<TSource,TDestination>()` records the pair and any overrides.
2. On the **first** `Map` call for a pair, an Expression Tree is built by resolving
   destination members through four phases — explicit `ForMember` overrides, direct
   name match, flattening, then unflattening — and compiled to native IL.
3. The compiled delegate is cached in a `ConcurrentDictionary`; every later call
   for that pair runs it directly. Self-referencing graphs are bounded by DFS cycle
   detection and `MaxDepth`.

---

## Building from source

```bash
dotnet build ProjectIMap/ProjectIMap.slnx
dotnet test  ProjectIMap.Tests/ProjectIMap.Tests.csproj
```

---

## License

[MIT](LICENSE)
