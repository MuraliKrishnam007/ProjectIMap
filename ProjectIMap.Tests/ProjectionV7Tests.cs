using System.Linq;
using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates the v7.0 ProjectionCompiler upgrades: nested collection
/// <c>.Select(...)</c> projections, constructor-parameter projection for
/// positional records, and the per-configuration projection cache.
/// </summary>
public sealed class ProjectionV7Tests
{
    [Fact]
    public void ProjectTo_Projects_A_Nested_Collection_Property()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Blog, BlogDto>();

        var data = new[]
        {
            new Blog
            {
                Id    = 1,
                Title = "Engine internals",
                Posts = [new() { Heading = "Part 1", Views = 10 }, new() { Heading = "Part 2", Views = 20 }]
            }
        };

        var dto = data.AsQueryable().ProjectTo<BlogDto>(config).Single();

        dto.Title.Should().Be("Engine internals");
        dto.Posts.Should().HaveCount(2);
        dto.Posts[0].Should().BeOfType<PostDto>();
        dto.Posts[0].Heading.Should().Be("Part 1");
        dto.Posts[1].Views.Should().Be(20);
    }

    [Fact]
    public void ProjectTo_Constructs_A_Positional_Record_Destination()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Blog, BlogSummaryDto>();

        var data = new[] { new Blog { Id = 3, Title = "Records" } };

        var dto = data.AsQueryable().ProjectTo<BlogSummaryDto>(config).Single();

        dto.Should().Be(new BlogSummaryDto(3, "Records"));
    }

    [Fact]
    public void Projection_Cache_Is_Isolated_Per_Configuration()
    {
        // Regression guard: the cache was once static per (Type, Type), so the
        // FIRST configuration to project a pair silently dictated the projection
        // for every other configuration in the process.
        var plainConfig = new MapperConfiguration();
        plainConfig.CreateMap<User, UserDto>();

        var overriddenConfig = new MapperConfiguration();
        overriddenConfig.CreateMap<User, UserDto>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => "OVERRIDDEN"));

        // Address populated: projection flattening emits pure (un-guarded)
        // property chains — EF turns them into LEFT JOINs, but the in-memory
        // LINQ provider used here would NRE on a null Address.
        var data = new[]
        {
            new User
            {
                Id = 1, Name = "original",
                Address = new Address { Street = "s", City = "c", Country = "x", ZipCode = "z" }
            }
        };

        var plain      = data.AsQueryable().ProjectTo<UserDto>(plainConfig).Single();
        var overridden = data.AsQueryable().ProjectTo<UserDto>(overriddenConfig).Single();

        plain.Name.Should().Be("original");
        overridden.Name.Should().Be("OVERRIDDEN",
            because: "each configuration must compile and cache its own projection");
    }
}
