using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="IMappingExpression{TSource,TDestination}.FlattenDepth"/>:
/// the configurable navigation-property lookahead used by flattening/unflattening,
/// independent of <see cref="IMappingExpression{TSource,TDestination}.MaxDepth"/>
/// (which governs self-referencing object recursion, not this lookahead).
/// </summary>
/// <remarks>
/// <c>DeepA.B.C.D.E.F.Value</c> is a 6-property-segment chain. The engine's
/// default lookahead (5) reaches the <c>F</c> node itself but does not recurse
/// into <c>F</c>'s own properties, so <c>DeepDto.BCDEFValue</c> is unreachable at
/// the default depth and only resolves once <c>FlattenDepth(6)</c> is configured.
/// </remarks>
public sealed class FlattenDepthTests
{
    private static DeepA BuildSixLevelChain() => new()
    {
        B = new DeepB
        {
            C = new DeepC
            {
                D = new DeepD
                {
                    E = new DeepE
                    {
                        F = new DeepF { Value = "found" }
                    }
                }
            }
        }
    };

    [Fact]
    public void Default_FlattenDepth_Does_Not_Reach_SixLevelDeep_Property()
    {
        var config = new MapperConfiguration();
        config.CreateMap<DeepA, DeepDto>(); // default FlattenDepth = 5
        var mapper = new Mapper(config);

        var dest = mapper.Map<DeepA, DeepDto>(BuildSixLevelChain());

        dest.BCDEFValue.Should().BeNull(
            because: "the default FlattenDepth (5) does not reach a property 6 segments deep");
    }

    [Fact]
    public void FlattenDepth_Configured_Deeper_Reaches_SixLevelDeep_Property()
    {
        var config = new MapperConfiguration();
        config.CreateMap<DeepA, DeepDto>().FlattenDepth(6);
        var mapper = new Mapper(config);

        var dest = mapper.Map<DeepA, DeepDto>(BuildSixLevelChain());

        dest.BCDEFValue.Should().Be("found");
    }

    [Fact]
    public void FlattenDepth_Rejects_NonPositive_Values()
    {
        var config = new MapperConfiguration();
        var act     = () => config.CreateMap<DeepA, DeepDto>().FlattenDepth(0);

        act.Should().Throw<System.ArgumentOutOfRangeException>();
    }
}
