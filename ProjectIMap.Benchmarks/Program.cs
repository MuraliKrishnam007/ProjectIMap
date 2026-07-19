using BenchmarkDotNet.Running;
using ProjectIMap.Benchmarks;

// BenchmarkDotNet requires the host process to be compiled in Release mode.
// Run with:  dotnet run -c Release --project ProjectIMap.Benchmarks
//
// Quick smoke-test (Debug / no args): runs GlobalSetup + every benchmark once
// and prints the results so correctness can be verified before a full run.
#if DEBUG
var bench = new MapperBenchmarks();
bench.GlobalSetup();

var userDto  = bench.Custom_Map_SimpleObject();
Console.WriteLine($"[Custom]     User  → UserDto  : Id={userDto.Id}  Name={userDto.Name}  Email={userDto.Email}  Age={userDto.Age}");
var userDto2 = bench.AutoMapper_Map_SimpleObject();
Console.WriteLine($"[AutoMapper] User  → UserDto  : Id={userDto2.Id}  Name={userDto2.Name}  Email={userDto2.Email}  Age={userDto2.Age}");

var orderDto  = bench.Custom_Map_ComplexFlattenedObject();
Console.WriteLine($"[Custom]     Order → OrderDto : Id={orderDto.Id}  CustomerName={orderDto.CustomerName}  Total={orderDto.Total}");
var orderDto2 = bench.AutoMapper_Map_ComplexFlattenedObject();
Console.WriteLine($"[AutoMapper] Order → OrderDto : Id={orderDto2.Id}  CustomerName={orderDto2.CustomerName}  Total={orderDto2.Total}");

var list  = bench.Custom_Map_LargeCollection();
Console.WriteLine($"[Custom]     List<Order> count={list.Count}  first.CustomerName={list[0].CustomerName}");
var list2 = bench.AutoMapper_Map_LargeCollection();
Console.WriteLine($"[AutoMapper] List<Order> count={list2.Count}  first.CustomerName={list2[0].CustomerName}");
#else
BenchmarkRunner.Run<MapperBenchmarks>(args: args);
#endif
