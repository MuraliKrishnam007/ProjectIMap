namespace ProjectIMap.Tests;

// ── Source models ─────────────────────────────────────────────────────────────

/// <summary>Source entity with a nested <see cref="Address"/> object.</summary>
public sealed class User
{
    public int    Id       { get; set; }
    public string Name     { get; set; } = string.Empty;
    public int    Age      { get; set; }
    public string Email    { get; set; } = string.Empty;
    public Address? Address { get; set; }
}

/// <summary>Nested value object carried by <see cref="User"/>.</summary>
public sealed class Address
{
    public string Street  { get; set; } = string.Empty;
    public string City    { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
}

// ── Destination DTOs ──────────────────────────────────────────────────────────

/// <summary>
/// Flat DTO.  The <c>AddressCity</c>, <c>AddressStreet</c>, <c>AddressCountry</c>,
/// and <c>AddressZipCode</c> properties are populated from the nested
/// <c>User.Address.*</c> properties via the mapper's flattening convention
/// (Phase 2 trie traversal).
/// </summary>
public sealed class UserDto
{
    public int    Id             { get; set; }
    public string Name           { get; set; } = string.Empty;
    public int    Age            { get; set; }
    public string Email          { get; set; } = string.Empty;

    // Flattened from Address.*
    public string? AddressStreet  { get; set; }
    public string? AddressCity    { get; set; }
    public string? AddressCountry { get; set; }
    public string? AddressZipCode { get; set; }
}

// ── Order / OrderDto — used by ProfileMappingTests ────────────────────────────

/// <summary>
/// Source order with a line-item price and quantity.
/// <c>InternalId</c> is present on both sides but deliberately <em>ignored</em>
/// by <see cref="TestProfile"/> on the forward mapping.
/// </summary>
public sealed class Order
{
    public int     Id         { get; set; }
    public string  Name       { get; set; } = string.Empty;
    public decimal Price      { get; set; }
    public int     Quantity   { get; set; }
    public string  InternalId { get; set; } = string.Empty;
}

/// <summary>
/// Destination DTO whose <c>CalculatedTotal</c> is derived via a
/// <c>MapFrom(s =&gt; s.Price * s.Quantity)</c> expression and whose
/// <c>InternalId</c> is suppressed by <c>.Ignore()</c>.
/// </summary>
public sealed class OrderDto
{
    public int     Id              { get; set; }
    public string  Name            { get; set; } = string.Empty;
    /// <summary>Populated via <c>MapFrom(s =&gt; s.Price * s.Quantity)</c>.</summary>
    public decimal CalculatedTotal { get; set; }
    /// <summary>Should remain <c>default</c> after mapping because it is ignored.</summary>
    public string  InternalId      { get; set; } = string.Empty;
}

// ── Category / CategoryDto — used by AdvancedMappingTests (cycle detection) ──

/// <summary>
/// Self-referential tree node.  <c>Parent</c> may point to any ancestor,
/// including <em>itself</em>, creating a cycle the DFS guard must break.
/// </summary>
public sealed class Category
{
    public int              Id       { get; set; }
    public string           Name     { get; set; } = string.Empty;
    public Category?        Parent   { get; set; }
    public List<Category>   Children { get; set; } = [];
}

/// <summary>DTO mirror of <see cref="Category"/>.</summary>
public sealed class CategoryDto
{
    public int               Id       { get; set; }
    public string            Name     { get; set; } = string.Empty;
    public CategoryDto?      Parent   { get; set; }
    public List<CategoryDto> Children { get; set; } = [];
}

// ── Person / PersonDto — used by ConstructUsing / AssertConfigurationIsValid tests ──

/// <summary>Plain mutable source with a public parameterless constructor.</summary>
public sealed class Person
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName  { get; set; } = string.Empty;
    public int    Age       { get; set; }
}

/// <summary>
/// Immutable destination with only a primary constructor — no parameterless
/// constructor exists, so mapping to it requires <c>ConstructUsing</c>.
/// </summary>
public sealed record PersonDto(string FullName, int Age);

/// <summary>Source with a nested <see cref="Person"/> — used to exercise
/// <c>ConstructUsing</c> on a nested (non-top-level) property pair.</summary>
public sealed class Company
{
    public string  Name  { get; set; } = string.Empty;
    public Person? Owner { get; set; }
}

/// <summary>DTO mirror of <see cref="Company"/> whose <see cref="Owner"/> requires
/// the same <c>ConstructUsing</c> registered for <c>Person → PersonDto</c>.</summary>
public sealed class CompanyDto
{
    public string     Name  { get; set; } = string.Empty;
    public PersonDto? Owner { get; set; }
}

// ── Animal / Dog / Cat — used by PolymorphicMappingTests (Include<>) ──────────

public class Animal
{
    public string Name { get; set; } = string.Empty;
}

public sealed class Dog : Animal
{
    public string Breed { get; set; } = string.Empty;
}

public sealed class Cat : Animal
{
    public bool Indoor { get; set; }
}

public class AnimalDto
{
    public string Name { get; set; } = string.Empty;
}

public sealed class DogDto : AnimalDto
{
    public string Breed { get; set; } = string.Empty;
}

public sealed class CatDto : AnimalDto
{
    public bool Indoor { get; set; }
}

// ── AnimalOwner / AnimalOwnerDto — a nested polymorphic Animal member ─────────
// Used by PolymorphicMappingTests to prove a nested member whose runtime value is
// a derived type (Dog/Cat) maps to the derived DTO, not just its declared base.
public sealed class AnimalOwner
{
    public string  Name { get; set; } = string.Empty;
    public Animal? Pet  { get; set; }
}

public sealed class AnimalOwnerDto
{
    public string     Name { get; set; } = string.Empty;
    public AnimalDto? Pet  { get; set; }
}

// ── Invoice — nested collection property mapping (v7.0) ──────────────────────
public sealed class InvoiceLine
{
    public string Sku { get; set; } = string.Empty;
    public int    Qty { get; set; }
}

public sealed class InvoiceLineDto
{
    public string Sku { get; set; } = string.Empty;
    public int    Qty { get; set; }
}

public sealed class Invoice
{
    public int                Id    { get; set; }
    public List<InvoiceLine>? Lines { get; set; }
    public List<int>          Codes { get; set; } = [];
}

public sealed class InvoiceDto
{
    public int                   Id    { get; set; }
    public List<InvoiceLineDto>? Lines { get; set; }
    public int[]                 Codes { get; set; } = [];
}

// ── TreeNode — self-referencing collection graph (v7.0) ──────────────────────
public sealed class TreeNode
{
    public string         Name     { get; set; } = string.Empty;
    public List<TreeNode> Children { get; set; } = [];
}

public sealed class TreeNodeDto
{
    public string            Name     { get; set; } = string.Empty;
    public List<TreeNodeDto> Children { get; set; } = [];
}

// ── Zoo — polymorphic elements inside a nested collection (v7.0) ─────────────
public sealed class Zoo
{
    public string       Name    { get; set; } = string.Empty;
    public List<Animal> Animals { get; set; } = [];
}

public sealed class ZooDto
{
    public string          Name    { get; set; } = string.Empty;
    public List<AnimalDto> Animals { get; set; } = [];
}

// ── Blog/Post — nested collection projection, dedicated to ProjectTo tests ───
public sealed class Post
{
    public string Heading { get; set; } = string.Empty;
    public int    Views   { get; set; }
}

public sealed class PostDto
{
    public string Heading { get; set; } = string.Empty;
    public int    Views   { get; set; }
}

public sealed class Blog
{
    public int        Id    { get; set; }
    public string     Title { get; set; } = string.Empty;
    public List<Post> Posts { get; set; } = [];
}

public sealed class BlogDto
{
    public int           Id    { get; set; }
    public string        Title { get; set; } = string.Empty;
    public List<PostDto> Posts { get; set; } = [];
}

public sealed record BlogSummaryDto(int Id, string Title);

// ── Constructor-parameter mapping (v7.0) ─────────────────────────────────────
public sealed class CtorSource
{
    public int    Id    { get; set; }
    public string Name  { get; set; } = string.Empty;
    public string Extra { get; set; } = string.Empty;
}

public sealed record CtorPersonDto(int Id, string Name);

public sealed record CtorPersonWithExtraDto(int Id, string Name)
{
    public string Extra { get; set; } = string.Empty;
}

public sealed class PointSource
{
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class ImmutablePoint
{
    public ImmutablePoint(int x, int y) { X = x; Y = y; }
    public int X { get; }
    public int Y { get; }
}

public sealed class Shape
{
    public string      Label { get; set; } = string.Empty;
    public PointSource? Point { get; set; }
}

public sealed class ShapeDto
{
    public string          Label { get; set; } = string.Empty;
    public ImmutablePoint? Point { get; set; }
}

public sealed class UnmatchableDto
{
    public UnmatchableDto(string missingEverywhere) => Code = missingEverywhere;
    public string Code { get; }
}

// ── ConvertUsing global type converters (v7.0) ───────────────────────────────
public sealed class ExternalRef
{
    public string CorrelationId { get; set; } = string.Empty;
    public string Label         { get; set; } = string.Empty;
}

public sealed class ExternalRefDto
{
    public Guid   CorrelationId { get; set; }
    public string Label         { get; set; } = string.Empty;
}

// ── Employee / EmployeeDto — used by ValueResolverTests ──────────────────────

public sealed class Employee
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName  { get; set; } = string.Empty;
}

public sealed class EmployeeDto
{
    public string FullName { get; set; } = string.Empty;
}

// ── Deep navigation chain — used by FlattenDepthTests ─────────────────────────

public sealed class DeepA { public DeepB? B { get; set; } }
public sealed class DeepB { public DeepC? C { get; set; } }
public sealed class DeepC { public DeepD? D { get; set; } }
public sealed class DeepD { public DeepE? E { get; set; } }
public sealed class DeepE { public DeepF? F { get; set; } }
public sealed class DeepF { public string Value { get; set; } = string.Empty; }

public sealed class DeepDto
{
    public string? BCDEFValue { get; set; }
}

// ── Container / ContainerDto — used by recursive AssertConfigurationIsValid tests ──

/// <summary>Wraps a <see cref="Person"/> — used to prove validation recurses into
/// a nested complex-type pair and reports the nested pair's own unmapped members.</summary>
public sealed class Container
{
    public Person? Item { get; set; }
}

public sealed class ContainerDto
{
    public PersonInnerDto? Item { get; set; }
}

/// <summary>
/// Constructible via a plain parameterless constructor (unlike <see cref="PersonDto"/>),
/// but deliberately has a member (<see cref="Nickname"/>) with no counterpart on
/// <see cref="Person"/>, so validation recursing into it must report an error.
/// </summary>
public sealed class PersonInnerDto
{
    public string FirstName { get; set; } = string.Empty;
    public string Nickname  { get; set; } = string.Empty;
}
