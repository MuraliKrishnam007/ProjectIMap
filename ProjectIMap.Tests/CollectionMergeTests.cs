using System;
using System.Collections.Generic;
using FluentAssertions;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates the collection branch of
/// <see cref="IMapper.Map{TSource,TDestination}(TSource,TDestination)"/>: clears
/// the destination <c>ICollection&lt;T&gt;</c> and repopulates it via the element
/// mapper — the same "replace contents" default AutoMapper applies without an
/// identity/equality comparer configured. Not an add/update/remove diff.
/// </summary>
public sealed class CollectionMergeTests
{
    private static Mapper CreateUserMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>();
        return new Mapper(config);
    }

    [Fact]
    public void Merges_Source_Elements_Into_Existing_List_Replacing_Prior_Contents()
    {
        var mapper = CreateUserMapper();
        var sources = new List<User>
        {
            new() { Id = 1, Name = "Alice" },
            new() { Id = 2, Name = "Bob" }
        };
        var destination = new List<UserDto> { new() { Id = 999, Name = "Stale" } };

        var result = mapper.Map(sources, destination);

        result.Should().BeSameAs(destination, because: "the existing List<T> instance is reused, not replaced");
        destination.Should().HaveCount(2);
        destination[0].Id.Should().Be(1);
        destination[0].Name.Should().Be("Alice");
        destination[1].Id.Should().Be(2);
        destination[1].Name.Should().Be("Bob");
    }

    [Fact]
    public void Empty_Source_Clears_Destination()
    {
        var mapper = CreateUserMapper();
        var sources = new List<User>();
        var destination = new List<UserDto> { new() { Id = 1, Name = "WillBeCleared" } };

        mapper.Map(sources, destination);

        destination.Should().BeEmpty();
    }

    [Fact]
    public void Throws_When_Destination_Is_An_Array()
    {
        var mapper = CreateUserMapper();
        var sources = new List<User> { new() { Id = 1, Name = "Alice" } };
        var destination = new UserDto[1];

        var act = () => mapper.Map(sources, destination);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*ICollection*");
    }

    [Fact]
    public void Throws_When_Element_Pair_Is_Not_Registered()
    {
        var config = new MapperConfiguration(); // deliberately no CreateMap<User, UserDto>()
        var mapper = new Mapper(config);

        var sources = new List<User> { new() { Id = 1, Name = "Alice" } };
        var destination = new List<UserDto>();

        var act = () => mapper.Map(sources, destination);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*No mapping registered*");
    }

    // ── Identity-based diffing via EqualityComparison ────────────────────────

    private static Mapper CreateIdentityDiffMapper()
    {
        var config = new MapperConfiguration();
        config.CreateMap<User, UserDto>()
              .EqualityComparison((src, dst) => src.Id == dst.Id);
        return new Mapper(config);
    }

    [Fact]
    public void EqualityComparison_Updates_Matched_Elements_InPlace()
    {
        var mapper = CreateIdentityDiffMapper();
        var sources = new List<User> { new() { Id = 1, Name = "Alice-Updated" } };
        var destination = new List<UserDto> { new() { Id = 1, Name = "Alice-Old" } };

        mapper.Map(sources, destination);

        destination.Should().HaveCount(1);
        destination[0].Name.Should().Be("Alice-Updated");
    }

    [Fact]
    public void EqualityComparison_Removes_Unmatched_Destination_Elements()
    {
        var mapper = CreateIdentityDiffMapper();
        var sources = new List<User> { new() { Id = 1, Name = "Alice" } };
        var destination = new List<UserDto>
        {
            new() { Id = 1, Name = "Alice" },
            new() { Id = 2, Name = "Bob-ToRemove" }
        };

        mapper.Map(sources, destination);

        destination.Should().HaveCount(1);
        destination.Should().ContainSingle(d => d.Id == 1);
    }

    [Fact]
    public void EqualityComparison_Adds_Unmatched_Source_Elements()
    {
        var mapper = CreateIdentityDiffMapper();
        var sources = new List<User>
        {
            new() { Id = 1, Name = "Alice" },
            new() { Id = 3, Name = "Carol-New" }
        };
        var destination = new List<UserDto> { new() { Id = 1, Name = "Alice" } };

        mapper.Map(sources, destination);

        destination.Should().HaveCount(2);
        destination.Should().Contain(d => d.Id == 3 && d.Name == "Carol-New");
    }

    [Fact]
    public void EqualityComparison_Handles_Add_Update_And_Remove_Together()
    {
        var mapper = CreateIdentityDiffMapper();
        var sources = new List<User>
        {
            new() { Id = 1, Name = "Alice-Updated" }, // update
            new() { Id = 3, Name = "Carol-New" }      // add
        };
        var destination = new List<UserDto>
        {
            new() { Id = 1, Name = "Alice-Old" },
            new() { Id = 2, Name = "Bob-ToRemove" }   // remove
        };

        mapper.Map(sources, destination);

        destination.Should().HaveCount(2);
        destination.Should().Contain(d => d.Id == 1 && d.Name == "Alice-Updated");
        destination.Should().Contain(d => d.Id == 3 && d.Name == "Carol-New");
        destination.Should().NotContain(d => d.Id == 2);
    }
}
