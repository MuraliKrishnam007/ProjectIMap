using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates nested collection property mapping (v7.0): a collection-typed
/// member pair with differing element types (e.g. <c>Invoice.Lines:
/// List&lt;InvoiceLine&gt;</c> → <c>InvoiceDto.Lines: List&lt;InvoiceLineDto&gt;</c>)
/// is mapped element-by-element instead of being silently skipped.
/// </summary>
public sealed class NestedCollectionMappingTests
{
    private static Mapper CreateInvoiceMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Invoice, InvoiceDto>();
        // Deliberately NOT registering InvoiceLine -> InvoiceLineDto: nested
        // collection elements map by convention, exactly like nested objects.
        return new Mapper(config);
    }

    [Fact]
    public void Maps_Nested_List_Of_Complex_Elements()
    {
        var mapper  = CreateInvoiceMapper();
        var invoice = new Invoice
        {
            Id    = 1,
            Lines = [new() { Sku = "A-1", Qty = 2 }, new() { Sku = "B-2", Qty = 5 }]
        };

        var dto = mapper.Map<Invoice, InvoiceDto>(invoice);

        dto.Lines.Should().HaveCount(2);
        dto.Lines![0].Should().BeOfType<InvoiceLineDto>();
        dto.Lines[0].Sku.Should().Be("A-1");
        dto.Lines[0].Qty.Should().Be(2);
        dto.Lines[1].Sku.Should().Be("B-2");
    }

    [Fact]
    public void Maps_Scalar_Element_Collection_To_A_Different_Collection_Shape()
    {
        var mapper  = CreateInvoiceMapper();
        var invoice = new Invoice { Id = 1, Codes = [7, 8, 9] };

        var dto = mapper.Map<Invoice, InvoiceDto>(invoice);

        dto.Codes.Should().BeOfType<int[]>(because: "List<int> materializes into the int[] destination");
        dto.Codes.Should().Equal(7, 8, 9);
    }

    [Fact]
    public void Null_Source_Collection_Maps_To_Null()
    {
        var mapper  = CreateInvoiceMapper();
        var invoice = new Invoice { Id = 1, Lines = null };

        var dto = mapper.Map<Invoice, InvoiceDto>(invoice);

        dto.Lines.Should().BeNull();
    }

    [Fact]
    public void SelfReferencing_Collection_Graph_Maps_Recursively()
    {
        var config = new MapperConfiguration();
        config.CreateMap<TreeNode, TreeNodeDto>();
        var mapper = new Mapper(config);

        var root = new TreeNode
        {
            Name     = "root",
            Children =
            [
                new() { Name = "child-1", Children = [new() { Name = "grandchild" }] },
                new() { Name = "child-2" }
            ]
        };

        var dto = mapper.Map<TreeNode, TreeNodeDto>(root);

        dto.Name.Should().Be("root");
        dto.Children.Should().HaveCount(2);
        dto.Children[0].Children.Should().ContainSingle().Which.Name.Should().Be("grandchild");
        dto.Children[1].Children.Should().BeEmpty();
    }

    [Fact]
    public void Polymorphic_Elements_Inside_A_Nested_Collection_Dispatch_On_Runtime_Type()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Zoo, ZooDto>();
        config.CreateMap<Animal, AnimalDto>()
              .Include<Dog, DogDto>()
              .Include<Cat, CatDto>();
        config.CreateMap<Dog, DogDto>();
        config.CreateMap<Cat, CatDto>();
        var mapper = new Mapper(config);

        var zoo = new Zoo
        {
            Name    = "City Zoo",
            Animals = [new Dog { Name = "Rex", Breed = "Husky" }, new Cat { Name = "Tom", Indoor = false }]
        };

        var dto = mapper.Map<Zoo, ZooDto>(zoo);

        dto.Animals[0].Should().BeOfType<DogDto>();
        ((DogDto)dto.Animals[0]).Breed.Should().Be("Husky");
        dto.Animals[1].Should().BeOfType<CatDto>();
    }

    [Fact]
    public void Map_Into_Existing_Instance_Replaces_The_Nested_Collection()
    {
        var mapper   = CreateInvoiceMapper();
        var invoice  = new Invoice { Id = 2, Lines = [new() { Sku = "NEW", Qty = 1 }] };
        var existing = new InvoiceDto { Id = 99, Lines = [new() { Sku = "STALE", Qty = 9 }] };

        mapper.Map(invoice, existing);

        existing.Id.Should().Be(2);
        existing.Lines.Should().ContainSingle().Which.Sku.Should().Be("NEW");
    }

    [Fact]
    public void Validation_Accepts_Nested_Collection_Pairs()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Invoice, InvoiceDto>();

        var act = () => config.AssertConfigurationIsValid();

        act.Should().NotThrow(because: "Lines and Codes are valid nested collection pairs, not unmapped members");
    }
}
