using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates the v7.0 lifecycle-hook upgrades: hooks accumulate (a second
/// registration adds instead of replacing) and class-based
/// <see cref="IMappingAction{TSource,TDestination}"/> hooks resolve from the
/// container on every map call.
/// </summary>
public sealed class HookUpgradeTests
{
    [Fact]
    public void Multiple_BeforeMap_Hooks_All_Run_In_Registration_Order()
    {
        var events = new List<string>();
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .BeforeMap((_, _) => events.Add("first"))
              .BeforeMap((_, _) => events.Add("second"));
        var mapper = new Mapper(config);

        mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "X" });

        // A second BeforeMap must add to the pipeline, not silently replace the first.
        events.Should().Equal("first", "second");
    }

    [Fact]
    public void Multiple_AfterMap_Hooks_All_Run_In_Registration_Order()
    {
        var events = new List<string>();
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .AfterMap((_, _) => events.Add("first"))
              .AfterMap((_, _) => events.Add("second"));
        var mapper = new Mapper(config);

        mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "X" });

        events.Should().Equal("first", "second");
    }

    // ── Class-based, DI-resolved IMappingAction hooks ────────────────────────

    private sealed class Recorder
    {
        public List<string> Events { get; } = [];
    }

    private sealed class RecordingAction : IMappingAction<Order, OrderDto>
    {
        private readonly Recorder _recorder;
        public RecordingAction(Recorder recorder) => _recorder = recorder;
        public void Process(Order source, OrderDto destination) => _recorder.Events.Add("action");
    }

    private sealed class StampAction : IMappingAction<Order, OrderDto>
    {
        public void Process(Order source, OrderDto destination)
            => destination.Name = $"{destination.Name}-stamped";
    }

    [Fact]
    public void DiResolved_MappingAction_Runs_And_Can_Mutate_The_Destination()
    {
        var services = new ServiceCollection();
        services.AddTransient<StampAction>();
        var provider = services.BuildServiceProvider();

        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .AfterMap<StampAction>();
        var mapper = new Mapper(config, provider);

        var dto = mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "Widget" });

        dto.Name.Should().Be("Widget-stamped");
    }

    [Fact]
    public void Inline_And_Class_Hooks_Mix_In_Registration_Order()
    {
        var recorder = new Recorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddTransient<RecordingAction>();
        var provider = services.BuildServiceProvider();

        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .BeforeMap((_, _) => recorder.Events.Add("inline-1"))
              .BeforeMap<RecordingAction>()
              .BeforeMap((_, _) => recorder.Events.Add("inline-2"));
        var mapper = new Mapper(config, provider);

        mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "X" });

        recorder.Events.Should().Equal("inline-1", "action", "inline-2");
    }

    [Fact]
    public void MappingAction_Without_ServiceProvider_Throws_A_Clear_Error()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .AfterMap<StampAction>();
        var mapper = new Mapper(config);   // no IServiceProvider

        var act = () => mapper.Map<Order, OrderDto>(new Order { Id = 1, Name = "X" });

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*IServiceProvider*");
    }

    [Fact]
    public void Hooks_Also_Accumulate_On_Map_Into_Existing_Instance()
    {
        var events = new List<string>();
        var config = new MapperConfiguration();
        config.CreateMap<Order, OrderDto>()
              .AfterMap((_, _) => events.Add("first"))
              .AfterMap((_, _) => events.Add("second"));
        var mapper = new Mapper(config);

        mapper.Map(new Order { Id = 1, Name = "X" }, new OrderDto());

        events.Should().Equal("first", "second");
    }
}
