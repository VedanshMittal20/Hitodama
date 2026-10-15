using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace TestRunner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("Hitodama In-Process Test Runner (100% Verification)");
        Console.WriteLine("=================================================");

        var testAssembly = typeof(Companion.Tests.Tier1_FeatureCoverage.SpringSolverTests).Assembly;
        var testTypes = testAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Tests"))
            .OrderBy(t => t.FullName)
            .ToList();

        int totalRan = 0;
        int passed = 0;
        int failed = 0;
        var failureMessages = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var type in testTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttributes().Any(a => a is FactAttribute))
                .OrderBy(m => m.Name)
                .ToList();

            if (methods.Count == 0) continue;

            Console.WriteLine($"\n[SUITE] {type.Name} ({methods.Count} tests)");

            foreach (var method in methods)
            {
                var factAttr = method.GetCustomAttribute<FactAttribute>();
                var theoryAttr = method.GetCustomAttribute<TheoryAttribute>();
                var inlineDataAttrs = method.GetCustomAttributes<InlineDataAttribute>().ToList();

                var invocations = new List<object?[]>();
                if (inlineDataAttrs.Count > 0)
                {
                    foreach (var id in inlineDataAttrs)
                    {
                        var dataRows = id.GetData(method)?.ToList();
                        if (dataRows == null || dataRows.Count == 0 || dataRows[0] == null)
                        {
                            invocations.Add(new object?[method.GetParameters().Length]);
                        }
                        else
                        {
                            foreach (var row in dataRows)
                            {
                                invocations.Add(row ?? new object?[method.GetParameters().Length]);
                            }
                        }
                    }
                }
                else
                {
                    invocations.Add(Array.Empty<object?>());
                }

                foreach (var paramList in invocations)
                {
                    totalRan++;
                    string testName = paramList.Length > 0
                        ? $"{method.Name}({string.Join(", ", paramList.Select(p => p?.ToString() ?? "null"))})"
                        : method.Name;

                    object? instance = null;
                    try
                    {
                        if (!method.IsStatic)
                        {
                            var ctor = type.GetConstructor(Type.EmptyTypes);
                            if (ctor is not null)
                            {
                                instance = Activator.CreateInstance(type);
                            }
                        }

                        object? result = method.Invoke(instance, paramList.Length > 0 ? paramList : null);
                        if (result is Task task)
                        {
                            await task;
                        }
                        else if (result is ValueTask vt)
                        {
                            await vt;
                        }

                        passed++;
                        Console.WriteLine($"  ✓ PASS: {testName}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        var realEx = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
                        string msg = $"  ✗ FAIL: {type.Name}.{testName} -> {realEx.GetType().Name}: {realEx.Message}";
                        Console.WriteLine(msg);
                        failureMessages.Add($"{msg}\n{realEx.StackTrace}");
                    }
                    finally
                    {
                        if (instance is IAsyncDisposable asyncDisp)
                        {
                            try { await asyncDisp.DisposeAsync(); } catch { }
                        }
                        else if (instance is IDisposable disp)
                        {
                            try { disp.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        stopwatch.Stop();
        Console.WriteLine("\n=================================================");
        Console.WriteLine($"RESULTS: Total: {totalRan} | Passed: {passed} | Failed: {failed} | Time: {stopwatch.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine("=================================================");

        if (failureMessages.Count > 0)
        {
            Console.WriteLine("\nFAILURES:");
            foreach (var f in failureMessages)
            {
                Console.WriteLine(f);
            }
            return 1;
        }

        return 0;
    }
}
