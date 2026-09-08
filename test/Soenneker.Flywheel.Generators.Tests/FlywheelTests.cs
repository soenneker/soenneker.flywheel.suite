using Soenneker.Flywheel.Core.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Soenneker.Flywheel.Core;

namespace Soenneker.Flywheel.Generators.Tests;

public sealed class FlywheelTests
{
    private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(string source)
    {
        IEnumerable<string> paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                                                                                                .Append(typeof(FlywheelJobAttribute).Assembly.Location)
                                                                                                .Append(typeof(Microsoft.Extensions.DependencyInjection.ServiceCollection).Assembly.Location).Distinct();
        var compilation = CSharpCompilation.Create("GeneratedTests", new[] { CSharpSyntaxTree.ParseText(source) },
            paths.Select(p => MetadataReference.CreateFromFile(p)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new FlywheelGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);
        return (output, driver.GetRunResult());
    }
    [Test]
    public void DisambiguatesHandlerNamesAcrossNamespaces()
    {
        var result = Generate("""
            using System.Threading;
            using System.Threading.Tasks;
            using Soenneker.Flywheel.Core.Attributes;
            namespace First {
                public class Jobs { [FlywheelJob("first")] public Task Run(string value, CancellationToken ct) => Task.CompletedTask; }
            }
            namespace Second {
                public class Jobs { [FlywheelJob("second")] public Task Run(string value, CancellationToken ct) => Task.CompletedTask; }
            }
            """);
        Diagnostic[] errors = result.Compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new Exception(string.Join("\n", errors.Select(x => x.ToString())));
        var properties = result.Compilation.GetTypeByMetadataName("Soenneker.Flywheel.Generated.FlywheelJobs")!
            .GetMembers().OfType<IPropertySymbol>().ToArray();
        if (properties.Length != 2 || properties.Any(x => !x.Name.StartsWith("Jobs_Run_", StringComparison.Ordinal)))
            throw new Exception("Colliding handler symbols were not disambiguated");
    }

    [Test]
    public void GeneratesCompilableTypedAdapter()
    {
        (Compilation Compilation, GeneratorDriverRunResult Result) result = Generate("""
            using System.Threading;
            using System.Threading.Tasks;
            using Soenneker.Flywheel.Core.Attributes;
            public sealed record Payload(string Value);
            public sealed class Jobs {
                [FlywheelJob("job.v1")] public Task Send(Payload value, CancellationToken token) => Task.CompletedTask;
            }
            """);
        Diagnostic[] errors = result.Compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new Exception(string.Join("\n", errors.Select(x => x.ToString())));
        if (result.Result.GeneratedTrees.Length != 2) throw new Exception("Expected separate catalog and invoker files");
        string text = string.Join("\n", result.Result.GeneratedTrees.Select(tree => tree.ToString()));
        if (!text.Contains("Jobs_Send") || !text.Contains("handler.@Send") || text.Contains("MethodInfo")) throw new Exception("Missing typed adapter");
    }

    [Test]
    public void GeneratesDeclaredMethodPolicy()
    {
        var result = Generate("""
            using System.Threading;
            using System.Threading.Tasks;
            using Soenneker.Flywheel.Core.Attributes;
            public sealed class Jobs {
                [FlywheelJob("exclusive.v1", MaxConcurrency = 1, RateLimit = 10, RateWindowSeconds = 30)]
                public Task Run(string value, CancellationToken token) => Task.CompletedTask;
            }
            """);
        Diagnostic[] errors = result.Compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new Exception(string.Join("\n", errors.Select(x => x.ToString())));
        string text = string.Join("\n", result.Result.GeneratedTrees.Select(tree => tree.ToString()));
        if (!text.Contains("MaxConcurrency = 1") || !text.Contains("RateLimit = 10") ||
            !text.Contains("TimeSpan.FromSeconds(30)"))
            throw new Exception("Declared method policy was not generated");
    }
    [Test]
    public void GeneratedJobIsAccessibleWithIntelliSenseDocumentation()
    {
        (Compilation Compilation, GeneratorDriverRunResult Result) result = Generate("""
            using System.Threading;
            using System.Threading.Tasks;
            using Soenneker.Flywheel.Core.Attributes;
            using Soenneker.Flywheel.Core.Services.Abstract;
            using Soenneker.Flywheel.Generated;
            public sealed record Payload(string Value);
            public interface IJobs {
                /// <summary>Sends a welcome message.</summary>
                Task Send(Payload value, CancellationToken token);
            }
            public sealed class Jobs : IJobs {
                [FlywheelJob("welcome&message.v1")]
                public Task Send(Payload value, CancellationToken token) => Task.CompletedTask;
            }
            public static class Producer {
                public static Task Submit(IJobClient client) =>
                    client.Enqueue(FlywheelJobs.Jobs_Send, new Payload("Hello"));
            }
            """);
        Diagnostic[] errors = result.Compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new Exception(string.Join("\n", errors.Select(x => x.ToString())));
        INamedTypeSymbol catalog = result.Compilation.GetTypeByMetadataName("Soenneker.Flywheel.Generated.FlywheelJobs")!;
        IPropertySymbol job = catalog.GetMembers("Jobs_Send").OfType<IPropertySymbol>().Single();
        string documentation = job.GetDocumentationCommentXml()!;
        foreach (string expected in new[] { "Sends a welcome message.", "welcome&amp;message.v1", "M:Jobs.Send", "Payload", "IJobClient.Enqueue" })
            if (!documentation.Contains(expected)) throw new Exception("Missing IntelliSense documentation: " + expected);
    }

    [Test]
    public void GeneratedJobRejectsWrongPayloadAndDocumentsArrayPayload()
    {
        (Compilation Compilation, GeneratorDriverRunResult Result) result = Generate("""
            using System.Threading;
            using System.Threading.Tasks;
            using Soenneker.Flywheel.Core.Attributes;
            using Soenneker.Flywheel.Core.Services.Abstract;
            using Soenneker.Flywheel.Generated;
            public sealed class Jobs {
                [FlywheelJob("batch.v1")]
                public Task Send(string[] values, CancellationToken token) => Task.CompletedTask;
            }
            public static class Producer {
                public static Task Submit(IJobClient client) =>
                    client.Enqueue(FlywheelJobs.Jobs_Send, 123);
            }
            """);
        if (result.Result.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
            throw new Exception("Generator failed for an array payload");
        Diagnostic[] errors = result.Compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 1 || errors[0].Id != "CS0411")
            throw new Exception("Expected payload type inference error: " + string.Join("\n", errors.Select(x => x.ToString())));
        ISymbol job = result.Compilation.GetTypeByMetadataName("Soenneker.Flywheel.Generated.FlywheelJobs")!.GetMembers("Jobs_Send").Single();
        if (!job.GetDocumentationCommentXml()!.Contains("string[]")) throw new Exception("Missing array payload documentation");
    }

    [Test]
    public void GeneratesCompilableCronRegistrationAndTypedChain()
    {
        (Compilation Compilation, GeneratorDriverRunResult Result) result = Generate("""
            using System.Threading;
            using System.Threading.Tasks;
            using Soenneker.Flywheel.Core.Attributes;
            using Soenneker.Flywheel.Core.Services.Abstract;
            using Soenneker.Flywheel.Generated;
            public sealed record Payload(string Value);
            public sealed class Jobs {
                [FlywheelJob("daily.v1")]
                [FlywheelCron("0 9 * * *", TimeZoneId = "America/Chicago", PayloadJson = "{\"Value\":\"hello\"}")]
                public Task Send(Payload value, CancellationToken token) => Task.CompletedTask;
            }
            public static class Producer {
                public static async Task Submit(IJobClient client) {
                    await client.RegisterGeneratedSchedules();
                    await client.Schedule("runtime", FlywheelJobs.Jobs_Send, new Payload("runtime"), "*/5 * * * *");
                    await client.Chain([FlywheelJobs.Jobs_Send.With(new Payload("first")), FlywheelJobs.Jobs_Send.With(new Payload("second"))]);
                }
            }
            """);
        Diagnostic[] errors = result.Compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new Exception(string.Join("\n", errors.Select(x => x.ToString())));
        if (result.Result.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)) throw new Exception("Cron generation failed");
    }

    [Test]
    public void ReportsOrphanAndDuplicateCronDeclarations()
    {
        (Compilation Compilation, GeneratorDriverRunResult Result) result = Generate("""
            using System.Threading;
            using System.Threading.Tasks;
            using Soenneker.Flywheel.Core.Attributes;
            public sealed class Jobs {
                [FlywheelCron("* * * * *")]
                public Task Orphan(string value, CancellationToken token) => Task.CompletedTask;
                [FlywheelJob("first")][FlywheelCron("* * * * *", Id = "same")]
                public Task First(string value, CancellationToken token) => Task.CompletedTask;
                [FlywheelJob("second")][FlywheelCron("* * * * *", Id = "same")]
                public Task Second(string value, CancellationToken token) => Task.CompletedTask;
            }
            """);
        if (result.Result.Diagnostics.Count(x => x.Id == "FW003") != 2) throw new Exception("Cron declarations were not validated");
    }

    [Test]
    public void ReportsInvalidSignaturesAndDuplicateNames()
    {
        (Compilation Compilation, GeneratorDriverRunResult Result) invalid = Generate("""
            using Soenneker.Flywheel.Core.Attributes;
            public class Jobs { [FlywheelJob("bad")] public static void Run() {} }
            """);
        if (!invalid.Result.Diagnostics.Any(x => x.Id == "FW001")) throw new Exception("No signature diagnostic");
        (Compilation Compilation, GeneratorDriverRunResult Result) duplicate = Generate("""
            using Soenneker.Flywheel.Core.Attributes;
            public class Jobs { [FlywheelJob("same")] public void A() {} [FlywheelJob("same")] public void B() {} }
            """);
        if (duplicate.Result.Diagnostics.Count(x => x.Id == "FW002") != 2) throw new Exception("No duplicate diagnostics");
    }
}
