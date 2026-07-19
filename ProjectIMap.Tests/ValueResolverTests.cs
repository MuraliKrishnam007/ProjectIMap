using System;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectIMap;
using Xunit;

namespace ProjectIMap.Tests;

/// <summary>
/// Validates <see cref="IMemberConfigurationExpression{TSource}.MapFrom{TResolver,TMember}"/>:
/// reusable/dependency-carrying <see cref="IValueResolver{TSource,TMember}"/>
/// classes as an alternative to an inline lambda.
/// </summary>
public sealed class ValueResolverTests
{
    private sealed class FullNameResolver : IValueResolver<Employee, string>
    {
        public string Resolve(Employee source) => $"{source.FirstName} {source.LastName}";
    }

    private sealed class PrefixedNameResolver : IValueResolver<Employee, string>
    {
        private readonly string _prefix;
        public PrefixedNameResolver(string prefix) => _prefix = prefix;
        public string Resolve(Employee source) => $"{_prefix}{source.FirstName} {source.LastName}";
    }

    [Fact]
    public void Resolver_Produces_Same_Value_As_Equivalent_Inline_Lambda()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Employee, EmployeeDto>()
              .ForMember(d => d.FullName, opt => opt.MapFrom(new FullNameResolver()));
        var mapper = new Mapper(config);

        var dto = mapper.Map<Employee, EmployeeDto>(new Employee { FirstName = "Ada", LastName = "Lovelace" });

        dto.FullName.Should().Be("Ada Lovelace");
    }

    [Fact]
    public void Resolver_Instance_Can_Carry_Constructor_Dependencies()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Employee, EmployeeDto>()
              .ForMember(d => d.FullName, opt => opt.MapFrom(new PrefixedNameResolver("Dr. ")));
        var mapper = new Mapper(config);

        var dto = mapper.Map<Employee, EmployeeDto>(new Employee { FirstName = "Ada", LastName = "Lovelace" });

        dto.FullName.Should().Be("Dr. Ada Lovelace");
    }

    [Fact]
    public void Resolver_Composes_With_Condition()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Employee, EmployeeDto>()
              .ForMember(d => d.FullName, opt =>
              {
                  opt.MapFrom(new FullNameResolver());
                  opt.Condition(s => !string.IsNullOrEmpty(s.LastName));
              });
        var mapper = new Mapper(config);

        var withLastName    = mapper.Map<Employee, EmployeeDto>(new Employee { FirstName = "Ada", LastName = "Lovelace" });
        var withoutLastName = mapper.Map<Employee, EmployeeDto>(new Employee { FirstName = "Ada", LastName = "" });

        withLastName.FullName.Should().Be("Ada Lovelace");
        withoutLastName.FullName.Should().BeNull(
            because: "Condition is false and there is nothing else to leave a freshly-constructed member as");
    }

    // ── DI-resolved resolvers: MapFrom<TResolver,TMember>() ──────────────────

    private sealed class DiFullNameResolver : IValueResolver<Employee, string>
    {
        public string Resolve(Employee source) => $"{source.FirstName} {source.LastName}";
    }

    private sealed class InstanceIdResolver : IValueResolver<Employee, string>
    {
        private static int _counter;
        private readonly int _id = Interlocked.Increment(ref _counter);
        public string Resolve(Employee source) => _id.ToString();
    }

    [Fact]
    public void DiResolver_Produces_Correct_Value()
    {
        var services = new ServiceCollection();
        services.AddTransient<DiFullNameResolver>();
        var provider = services.BuildServiceProvider();

        var config = new MapperConfiguration();
        config.CreateMap<Employee, EmployeeDto>()
              .ForMember(d => d.FullName, opt => opt.MapFrom<DiFullNameResolver, string>());
        var mapper = new Mapper(config, provider);

        var dto = mapper.Map<Employee, EmployeeDto>(new Employee { FirstName = "Ada", LastName = "Lovelace" });

        dto.FullName.Should().Be("Ada Lovelace");
    }

    [Fact]
    public void DiResolver_Resolves_Fresh_Instance_Per_Call_When_Registered_Transient()
    {
        var services = new ServiceCollection();
        services.AddTransient<InstanceIdResolver>();
        var provider = services.BuildServiceProvider();

        var config = new MapperConfiguration();
        config.CreateMap<Employee, EmployeeDto>()
              .ForMember(d => d.FullName, opt => opt.MapFrom<InstanceIdResolver, string>());
        var mapper = new Mapper(config, provider);

        var employee = new Employee { FirstName = "Ada", LastName = "Lovelace" };
        var first  = mapper.Map<Employee, EmployeeDto>(employee).FullName;
        var second = mapper.Map<Employee, EmployeeDto>(employee).FullName;

        first.Should().NotBe(second,
            because: "a Transient registration must resolve a fresh instance on every map call " +
                     "(correct behaviour for scoped/transient dependencies)");
    }

    [Fact]
    public void DiResolver_Resolves_Same_Instance_Per_Call_When_Registered_Singleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InstanceIdResolver>();
        var provider = services.BuildServiceProvider();

        var config = new MapperConfiguration();
        config.CreateMap<Employee, EmployeeDto>()
              .ForMember(d => d.FullName, opt => opt.MapFrom<InstanceIdResolver, string>());
        var mapper = new Mapper(config, provider);

        var employee = new Employee { FirstName = "Ada", LastName = "Lovelace" };
        var first  = mapper.Map<Employee, EmployeeDto>(employee).FullName;
        var second = mapper.Map<Employee, EmployeeDto>(employee).FullName;

        first.Should().Be(second, because: "a Singleton registration always resolves the same instance");
    }

    [Fact]
    public void DiResolver_Throws_When_Mapper_Has_No_ServiceProvider()
    {
        var config = new MapperConfiguration();
        config.CreateMap<Employee, EmployeeDto>()
              .ForMember(d => d.FullName, opt => opt.MapFrom<DiFullNameResolver, string>());
        var mapper = new Mapper(config); // no IServiceProvider

        var act = () => mapper.Map<Employee, EmployeeDto>(new Employee { FirstName = "Ada", LastName = "Lovelace" });

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*IServiceProvider*");
    }

    [Fact]
    public void DiResolver_Throws_When_Resolver_Not_Registered_In_Container()
    {
        var services = new ServiceCollection(); // deliberately does not register DiFullNameResolver
        var provider = services.BuildServiceProvider();

        var config = new MapperConfiguration();
        config.CreateMap<Employee, EmployeeDto>()
              .ForMember(d => d.FullName, opt => opt.MapFrom<DiFullNameResolver, string>());
        var mapper = new Mapper(config, provider);

        var act = () => mapper.Map<Employee, EmployeeDto>(new Employee { FirstName = "Ada", LastName = "Lovelace" });

        act.Should().Throw<InvalidOperationException>();
    }
}
