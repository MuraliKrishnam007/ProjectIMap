using System;
using System.Linq;
using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates global <see cref="MapperConfiguration.ConvertUsing{TSrc,TDst}"/>
/// type converters (v7.0): registered once, applied wherever a value of the
/// converter's source type adapts to its destination type — across every pair,
/// in both the runtime engine and <c>ProjectTo</c> projections.
/// </summary>
public sealed class ConvertUsingTests
{
    [Fact]
    public void Converter_Enables_An_Otherwise_Unmappable_Member()
    {
        var config = new MapperConfiguration();
        config.ConvertUsing<string, Guid>(s => Guid.Parse(s));
        config.CreateMap<ExternalRef, ExternalRefDto>();
        var mapper = new Mapper(config);

        var id  = Guid.NewGuid();
        var dto = mapper.Map<ExternalRef, ExternalRefDto>(
            new ExternalRef { CorrelationId = id.ToString(), Label = "ref" });

        dto.CorrelationId.Should().Be(id);
        dto.Label.Should().Be("ref");
    }

    [Fact]
    public void Converter_Takes_Precedence_Over_Direct_Assignability()
    {
        var config = new MapperConfiguration();
        config.ConvertUsing<string, string>(s => s.ToUpperInvariant());
        config.CreateMap<User, UserDto>();
        var mapper = new Mapper(config);

        var dto = mapper.Map<User, UserDto>(new User { Id = 1, Name = "quiet" });

        dto.Name.Should().Be("QUIET",
            because: "an explicit converter must win over the built-in identity assignment");
    }

    [Fact]
    public void Converter_Applies_Across_Every_Registered_Pair()
    {
        var config = new MapperConfiguration();
        config.ConvertUsing<string, string>(s => s.Trim());
        config.CreateMap<User, UserDto>();
        config.CreateMap<Order, OrderDto>();
        var mapper = new Mapper(config);

        var user  = mapper.Map<User, UserDto>(new User { Id = 1, Name = "  padded  " });
        var order = mapper.Map<Order, OrderDto>(new Order { Id = 2, Name = "  also  " });

        user.Name.Should().Be("padded");
        order.Name.Should().Be("also");
    }

    [Fact]
    public void Converter_Is_Inlined_Into_ProjectTo_Projections()
    {
        var config = new MapperConfiguration();
        config.ConvertUsing<string, Guid>(s => Guid.Parse(s));
        config.CreateMap<ExternalRef, ExternalRefDto>();

        var id   = Guid.NewGuid();
        var data = new[] { new ExternalRef { CorrelationId = id.ToString(), Label = "row" } };

        var dtos = data.AsQueryable().ProjectTo<ExternalRefDto>(config).ToList();

        dtos.Should().ContainSingle().Which.CorrelationId.Should().Be(id);
    }

    [Fact]
    public void Converter_Feeds_Constructor_Parameter_Mapping()
    {
        // BlogSummaryDto(int Id, string Title) from a source whose Title needs a
        // converter-driven transform proves converters run inside ctor matching too.
        var config = new MapperConfiguration();
        config.ConvertUsing<string, string>(s => s.ToUpperInvariant());
        config.CreateMap<Blog, BlogSummaryDto>();
        var mapper = new Mapper(config);

        var dto = mapper.Map<Blog, BlogSummaryDto>(new Blog { Id = 5, Title = "title" });

        dto.Should().Be(new BlogSummaryDto(5, "TITLE"));
    }

    [Fact]
    public void Profile_Registered_Converter_Merges_Into_The_Global_Configuration()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.MapperServiceCollectionExtensions
            .AddMyMapper(services, typeof(ConverterProfile).Assembly);
        var provider = Microsoft.Extensions.DependencyInjection
            .ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        var mapper = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IMapper>(provider);

        var id  = Guid.NewGuid();
        var dto = mapper.Map<ExternalRef, ExternalRefDto>(
            new ExternalRef { CorrelationId = id.ToString(), Label = "via-profile" });

        dto.CorrelationId.Should().Be(id);
    }
}

/// <summary>Profile used by <see cref="ConvertUsingTests"/> — discovered by assembly scan.</summary>
public sealed class ConverterProfile : MappingProfile
{
    public ConverterProfile()
    {
        ConvertUsing<string, Guid>(s => Guid.Parse(s));
        CreateMap<ExternalRef, ExternalRefDto>();
    }
}
