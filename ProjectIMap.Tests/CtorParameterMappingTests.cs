using System;
using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates constructor-parameter mapping (v7.0): destinations with no public
/// parameterless constructor — positional records, immutable ctor-only types —
/// map with zero configuration by matching constructor parameters to source
/// properties by name (case-insensitive), with full type adaptation.
/// </summary>
public sealed class CtorParameterMappingTests
{
    [Fact]
    public void Maps_To_A_Positional_Record_Without_Configuration()
    {
        var config = new MapperConfiguration();
        config.CreateMap<CtorSource, CtorPersonDto>();
        var mapper = new Mapper(config);

        var dto = mapper.Map<CtorSource, CtorPersonDto>(
            new CtorSource { Id = 7, Name = "Ada", Extra = "ignored" });

        dto.Should().Be(new CtorPersonDto(7, "Ada"));
    }

    [Fact]
    public void Extra_Settable_Property_Is_Still_Bound_By_Convention()
    {
        var config = new MapperConfiguration();
        config.CreateMap<CtorSource, CtorPersonWithExtraDto>();
        var mapper = new Mapper(config);

        var dto = mapper.Map<CtorSource, CtorPersonWithExtraDto>(
            new CtorSource { Id = 7, Name = "Ada", Extra = "kept" });

        dto.Id.Should().Be(7);
        dto.Name.Should().Be("Ada");
        dto.Extra.Should().Be("kept",
            because: "members not consumed by the constructor still bind by convention");
    }

    [Fact]
    public void Maps_To_An_Immutable_Class_With_GetOnly_Properties()
    {
        var config = new MapperConfiguration();
        config.CreateMap<PointSource, ImmutablePoint>();
        var mapper = new Mapper(config);

        var point = mapper.Map<PointSource, ImmutablePoint>(new PointSource { X = 3, Y = 4 });

        point.X.Should().Be(3);
        point.Y.Should().Be(4);
    }

    [Fact]
    public void Nested_CtorOnly_Destination_Is_Constructed_From_Its_Source_Member()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Shape, ShapeDto>();
        var mapper = new Mapper(config);

        var dto = mapper.Map<Shape, ShapeDto>(
            new Shape { Label = "L", Point = new PointSource { X = 1, Y = 2 } });

        dto.Label.Should().Be("L");
        dto.Point.Should().NotBeNull();
        dto.Point!.X.Should().Be(1);
        dto.Point.Y.Should().Be(2);
    }

    [Fact]
    public void Validation_Accepts_A_CtorMapped_Destination()
    {
        var config = new MapperConfiguration();
        config.CreateMap<CtorSource, CtorPersonDto>();

        var act = () => config.AssertConfigurationIsValid();

        act.Should().NotThrow();
    }

    [Fact]
    public void Throws_A_Helpful_Error_When_No_Constructor_Matches()
    {
        var config = new MapperConfiguration();
        config.CreateMap<CtorSource, UnmatchableDto>();
        var mapper = new Mapper(config);

        var act = () => mapper.Map<CtorSource, UnmatchableDto>(new CtorSource { Id = 1 });

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*constructor*match source properties by name*");
    }
}
